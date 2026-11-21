namespace AppLock;

static class Manager
{
    public static int Run()
    {
        var cfg = ConfigStore.Load() ?? new AppConfig();

        if (cfg.MasterHash is null)
        {
            using var d = new NewPasswordDialog("Welcome to AppLock",
                "Create a master password. You'll need it to open AppLock, and to unlock apps that use it.");
            if (d.ShowDialog() != DialogResult.OK) return 1;
            (cfg.MasterHash, cfg.MasterSalt) = Crypto.Hash(d.Password);
            ConfigStore.Save(cfg);
        }
        else
        {
            using var p = new PasswordDialog("AppLock", "Enter your master password to manage locked apps.",
                pw => Crypto.Verify(pw, cfg.MasterHash, cfg.MasterSalt), okText: "Open");
            if (p.ShowDialog() != DialogResult.OK) return 1;
        }

        string gate;
        try { gate = Installer.EnsureInstalled(); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not install AppLock into Program Files:\n\n" + ex.Message, "AppLock",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Application.Run(new ManagerForm(cfg, gate));
        return 0;
    }
}

sealed class ManagerForm : Form
{
    readonly AppConfig _cfg;
    readonly string _gate;
    readonly ListView _list;
    readonly ImageList _icons = new() { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
    readonly Button _edit, _remove;

    public ManagerForm(AppConfig cfg, string gate)
    {
        _cfg = cfg;
        _gate = gate;
        Text = "AppLock";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75F);
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(560, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Shield;

        _list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            Location = new Point(12, 12), Size = new Size(570, 396),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            SmallImageList = _icons,
        };
        _list.Columns.Add("Program", 160);
        _list.Columns.Add("Location", 290);
        _list.Columns.Add("Password", 110);
        Controls.Add(_list);

        var right = AnchorStyles.Top | AnchorStyles.Right;
        var add = Ui.Button(this, "Lock an app…", 594, 12, 114); add.Anchor = right;
        _edit = Ui.Button(this, "Edit…", 594, 48, 114); _edit.Anchor = right;
        _remove = Ui.Button(this, "Unlock / remove", 594, 84, 114); _remove.Anchor = right;
        var master = Ui.Button(this, "Master password…", 594, 140, 114); master.Anchor = right;
        master.Height = 46;

        add.Click += (_, _) => Add();
        _edit.Click += (_, _) => Edit();
        _list.DoubleClick += (_, _) => Edit();
        _remove.Click += (_, _) => Remove();
        master.Click += (_, _) => ChangeMaster();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        Refill();
        Apply(); // re-sync Windows with the saved list on every start
    }

    LockedApp? Selected => _list.SelectedItems.Count > 0 ? (LockedApp)_list.SelectedItems[0].Tag! : null;

    void UpdateButtons() => _edit.Enabled = _remove.Enabled = Selected is not null;

    void Refill()
    {
        _list.Items.Clear();
        _icons.Images.Clear();
        foreach (var app in _cfg.Apps.OrderBy(a => a.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            int img = -1;
            try
            {
                if (app.Path is not null && File.Exists(app.Path) && Icon.ExtractAssociatedIcon(app.Path) is { } ic)
                { _icons.Images.Add(ic.ToBitmap()); img = _icons.Images.Count - 1; }
            }
            catch { }
            var item = new ListViewItem(app.ExeName, img) { Tag = app };
            item.SubItems.Add(app.Path ?? "(any location)");
            item.SubItems.Add(app.UseMaster ? "Master" : "Own password");
            _list.Items.Add(item);
        }
        UpdateButtons();
    }

    bool Apply()
    {
        try
        {
            ConfigStore.Save(_cfg);
            Ifeo.Sync(_cfg, _gate);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AppLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    void Add()
    {
        using var d = new AppEditDialog(_cfg, null);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _cfg.Apps.Add(d.Result);
        Apply();
        Refill();
    }

    void Edit()
    {
        var app = Selected;
        if (app is null) return;
        using var d = new AppEditDialog(_cfg, app);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _cfg.Apps[_cfg.Apps.IndexOf(app)] = d.Result;
        Apply();
        Refill();
    }

    void Remove()
    {
        var app = Selected;
        if (app is null) return;
        if (MessageBox.Show(this, $"Unlock {app.ExeName}? It will open without a password.", "AppLock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cfg.Apps.Remove(app);
        Apply();
        Refill();
    }

    void ChangeMaster()
    {
        using var check = new PasswordDialog("Change master password", "Enter your current master password.",
            pw => Crypto.Verify(pw, _cfg.MasterHash, _cfg.MasterSalt));
        if (check.ShowDialog(this) != DialogResult.OK) return;
        using var d = new NewPasswordDialog("Change master password", "Choose a new master password.");
        if (d.ShowDialog(this) != DialogResult.OK) return;
        (_cfg.MasterHash, _cfg.MasterSalt) = Crypto.Hash(d.Password);
        if (Apply())
            MessageBox.Show(this, "Master password changed.", "AppLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
