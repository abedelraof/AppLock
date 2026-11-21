namespace AppLock;

static class Ui
{
    public static void Setup(Form f, string title, int w, int h)
    {
        f.Text = title;
        f.AutoScaleDimensions = new SizeF(96F, 96F);
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.Font = new Font("Segoe UI", 9.75F);
        f.ClientSize = new Size(w, h);
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false;
        f.MinimizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
    }

    public static Label Label(Control parent, string text, int x, int y, int w = 0, int h = 0)
    {
        var l = new Label { Text = text, Location = new Point(x, y), AutoSize = w == 0 };
        if (w > 0) l.Size = new Size(w, h > 0 ? h : 22);
        parent.Controls.Add(l);
        return l;
    }

    public static TextBox Box(Control parent, int x, int y, int w, bool password = false)
    {
        var t = new TextBox { Location = new Point(x, y), Width = w, UseSystemPasswordChar = password };
        parent.Controls.Add(t);
        return t;
    }

    public static Button Button(Control parent, string text, int x, int y, int w = 90)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 30) };
        parent.Controls.Add(b);
        return b;
    }
}

/// Asks for a password and checks it. Wrong attempts get a growing wait.
sealed class PasswordDialog : Form
{
    readonly TextBox _pw;
    readonly Label _err;
    readonly Button _ok;
    readonly Func<string, bool> _verify;
    int _fails;

    /// The password that was accepted.
    public string Password { get; private set; } = "";

    public PasswordDialog(string title, string prompt, Func<string, bool> verify, Icon? icon = null, string okText = "OK")
    {
        _verify = verify;
        Ui.Setup(this, title, 400, 176);
        TopMost = true;
        ShowInTaskbar = true;

        var pic = new PictureBox
        {
            Location = new Point(16, 18), Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom,
            Image = (icon ?? SystemIcons.Shield).ToBitmap(),
        };
        Controls.Add(pic);
        if (icon is not null) Icon = icon;

        Ui.Label(this, prompt, 64, 16, 320, 44);
        _pw = Ui.Box(this, 64, 66, 320, password: true);
        _err = Ui.Label(this, "", 64, 98, 320);
        _err.ForeColor = Color.Firebrick;

        _ok = Ui.Button(this, okText, 198, 130);
        var cancel = Ui.Button(this, "Cancel", 294, 130);
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _ok;
        CancelButton = cancel;
        _ok.Click += async (_, _) => await TryUnlock();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _pw.Focus();
    }

    async Task TryUnlock()
    {
        string pw = _pw.Text;
        _ok.Enabled = _pw.Enabled = false;
        _err.Text = "Checking…";
        bool good = await Task.Run(() => _verify(pw));
        if (good) { Password = pw; DialogResult = DialogResult.OK; return; }

        _fails++;
        _pw.Clear();
        if (_fails >= 3)
        {
            int wait = Math.Min(_fails - 2, 10) * 2;
            for (int s = wait; s > 0; s--)
            {
                _err.Text = $"Wrong password. Try again in {s}s.";
                await Task.Delay(1000);
            }
        }
        _err.Text = "Wrong password.";
        _ok.Enabled = _pw.Enabled = true;
        _pw.Focus();
    }
}

/// Create a new password (typed twice).
sealed class NewPasswordDialog : Form
{
    readonly TextBox _a, _b;
    readonly Label _err;
    public string Password => _a.Text;

    public NewPasswordDialog(string title, string prompt)
    {
        Ui.Setup(this, title, 400, 200);
        Ui.Label(this, prompt, 16, 14, 368, 40);
        Ui.Label(this, "Password:", 16, 63);
        _a = Ui.Box(this, 120, 60, 264, password: true);
        Ui.Label(this, "Confirm:", 16, 97);
        _b = Ui.Box(this, 120, 94, 264, password: true);
        _err = Ui.Label(this, "", 120, 124, 264);
        _err.ForeColor = Color.Firebrick;

        var ok = Ui.Button(this, "OK", 198, 156);
        var cancel = Ui.Button(this, "Cancel", 294, 156);
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            string? problem = PasswordRules.Check(_a.Text, _b.Text);
            if (problem is not null) { _err.Text = problem; return; }
            DialogResult = DialogResult.OK;
        };
    }
}

static class PasswordRules
{
    public static string? Check(string a, string b)
    {
        if (a.Length < 4) return "Use at least 4 characters.";
        if (a != b) return "The passwords don't match.";
        return null;
    }
}

/// Add or edit one locked app.
sealed class AppEditDialog : Form
{
    public static bool IsForbidden(string exeName) => Forbidden.Contains(exeName);

    static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "applock.exe", "explorer.exe", "winlogon.exe", "wininit.exe", "csrss.exe", "lsass.exe", "smss.exe",
        "services.exe", "svchost.exe", "dwm.exe", "userinit.exe", "logonui.exe", "consent.exe", "sihost.exe",
        "ctfmon.exe", "fontdrvhost.exe", "runtimebroker.exe", "conhost.exe", "dllhost.exe", "taskhostw.exe",
    };

    readonly TextBox _path, _pw1, _pw2;
    readonly RadioButton _master, _custom;
    readonly Label _err;
    readonly LockedApp? _existing;
    readonly AppConfig _cfg;

    public LockedApp Result { get; private set; } = new();

    public AppEditDialog(AppConfig cfg, LockedApp? existing)
    {
        _cfg = cfg;
        _existing = existing;
        Ui.Setup(this, existing is null ? "Lock an app" : "Edit locked app", 580, 290);

        Ui.Label(this, "Program:", 16, 19);
        _path = Ui.Box(this, 100, 16, 228);
        var installed = Ui.Button(this, "Installed apps…", 336, 14, 132);
        var browse = Ui.Button(this, "Browse…", 474, 14, 90);
        var hint = Ui.Label(this, "Choose from your installed apps, browse for an .exe, or type its file name (e.g. chrome.exe).", 100, 44, 464, 20);
        hint.ForeColor = SystemColors.GrayText;
        hint.Font = new Font("Segoe UI", 8.5F);

        var group = new GroupBox { Text = "Password to unlock it", Location = new Point(16, 72), Size = new Size(548, 150) };
        Controls.Add(group);
        _master = new RadioButton { Text = "Use the master password", Location = new Point(14, 26), AutoSize = true, Checked = true };
        _custom = new RadioButton { Text = "Use its own password:", Location = new Point(14, 52), AutoSize = true };
        group.Controls.Add(_master);
        group.Controls.Add(_custom);
        Ui.Label(group, "Password:", 36, 85);
        _pw1 = Ui.Box(group, 120, 82, 410, password: true);
        Ui.Label(group, "Confirm:", 36, 117);
        _pw2 = Ui.Box(group, 120, 114, 410, password: true);

        _err = Ui.Label(this, "", 16, 232, 350, 40);
        _err.ForeColor = Color.Firebrick;
        var ok = Ui.Button(this, "Save", 378, 246);
        var cancel = Ui.Button(this, "Cancel", 474, 246);
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;

        if (existing is not null)
        {
            _path.Text = existing.Path ?? existing.ExeName;
            _custom.Checked = !existing.UseMaster;
            if (!existing.UseMaster) _pw1.PlaceholderText = _pw2.PlaceholderText = "(leave blank to keep current)";
        }

        void Toggle() => _pw1.Enabled = _pw2.Enabled = _custom.Checked;
        _custom.CheckedChanged += (_, _) => Toggle();
        Toggle();

        installed.Click += (_, _) =>
        {
            using var d = new InstalledAppsDialog();
            if (d.ShowDialog(this) == DialogResult.OK && d.SelectedPath is not null) _path.Text = d.SelectedPath;
        };
        browse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Programs (*.exe)|*.exe",
                Title = "Choose a program to lock",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (ofd.ShowDialog(this) == DialogResult.OK) _path.Text = ofd.FileName;
        };
        ok.Click += (_, _) => Save();
    }

    void Save()
    {
        string input = _path.Text.Trim().Trim('"');
        if (input.Length == 0) { _err.Text = "Choose a program."; return; }

        string exeName = Path.GetFileName(input);
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { _err.Text = "That isn't an .exe file."; return; }
        if (Forbidden.Contains(exeName)) { _err.Text = $"{exeName} is part of Windows (or AppLock) and can't be locked."; return; }

        string? fullPath = Path.IsPathRooted(input) ? input : null;
        if (fullPath is not null && !File.Exists(fullPath)) { _err.Text = "That file doesn't exist."; return; }

        var dup = _cfg.Find(exeName);
        if (dup is not null && !ReferenceEquals(dup, _existing)) { _err.Text = $"{exeName} is already locked."; return; }

        var result = new LockedApp { ExeName = exeName, Path = fullPath, UseMaster = _master.Checked };
        if (_custom.Checked)
        {
            bool keepOld = _existing is { UseMaster: false } && _pw1.Text.Length == 0 && _pw2.Text.Length == 0;
            if (keepOld)
            {
                result.Hash = _existing!.Hash;
                result.Salt = _existing.Salt;
            }
            else
            {
                string? problem = PasswordRules.Check(_pw1.Text, _pw2.Text);
                if (problem is not null) { _err.Text = problem; return; }
                (result.Hash, result.Salt) = Crypto.Hash(_pw1.Text);
            }
        }
        Result = result;
        DialogResult = DialogResult.OK;
    }
}
