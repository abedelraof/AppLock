using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AppLock;

sealed record InstalledApp(string Name, string ExePath, Bitmap? Icon);

/// Finds installed programs from Start menu shortcuts and the "App Paths" registry.
static class AppScanner
{
    public static List<InstalledApp> Scan()
    {
        var byPath = new Dictionary<string, (string name, int rank)>(StringComparer.OrdinalIgnoreCase);

        void Add(string? exe, string name, int rank)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            exe = Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return;
            string file = Path.GetFileName(exe);
            if (AppEditDialog.IsForbidden(file) || IsJunk(file, name)) return;
            // Cached MSI icons and Store-app internals aren't something AppLock can lock.
            if (exe.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase) ||
                exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return;
            // Shortcuts like "Hyper-V Manager" just run a shared tool (mmc.exe); show the tool's real name.
            if (SharedHosts.Contains(file)) name = FriendlyName(exe);
            if (byPath.TryGetValue(exe, out var old) && old.rank <= rank) return;
            byPath[exe] = (name, rank);
        }

        // 1. Start menu shortcuts (all users + current user). Best names.
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var lnk in links)
            {
                var (target, args) = Shortcut.Resolve(lnk);
                Add(UnwrapLauncher(target, args), Path.GetFileNameWithoutExtension(lnk), 0);
            }
        }

        // 2. Programs registered under App Paths.
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var ap = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (ap is null) continue;
                foreach (var sub in ap.GetSubKeyNames())
                {
                    try
                    {
                        using var k = ap.OpenSubKey(sub);
                        if (k?.GetValue(null) is string exe) Add(exe, FriendlyName(exe), 1);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var result = new List<InstalledApp>();
        foreach (var (exe, (name, _)) in byPath)
        {
            Bitmap? bmp = null;
            try { using var ic = Icon.ExtractAssociatedIcon(exe); bmp = ic?.ToBitmap(); } catch { }
            result.Add(new InstalledApp(name, exe, bmp));
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// Squirrel-style apps (Discord, Slack, Teams…) start via "Update.exe --processStart App.exe".
    static string? UnwrapLauncher(string? target, string? args)
    {
        if (target is null) return null;
        if (args is not null && Path.GetFileName(target).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            int i = args.IndexOf("--processStart", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string name = CmdLine.First(args.Substring(i + "--processStart".Length), out _);
                string candidate = Path.Combine(Path.GetDirectoryName(target)!, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return target;
    }

    static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mmc.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "control.exe", "rundll32.exe", "wscript.exe",
        "cscript.exe", "msiexec.exe", "pythonw.exe", "python.exe", "javaw.exe", "java.exe", "msedge_proxy.exe",
    };

    static bool IsJunk(string file, string name) =>
        file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
        file.Contains("uninstall", StringComparison.OrdinalIgnoreCase);

    static string FriendlyName(string exe)
    {
        try
        {
            var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }
}

static class Shortcut
{
    public static (string? target, string? args) Resolve(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var path = new StringBuilder(1024);
                link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
                var args = new StringBuilder(2048);
                link.GetArguments(args, args.Capacity);
                return (path.Length > 0 ? path.ToString() : null, args.ToString());
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch { return (null, null); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

/// Searchable list of installed apps with their icons.
sealed class InstalledAppsDialog : Form
{
    readonly TextBox _search;
    readonly ListView _list;
    readonly Label _status;
    readonly Button _ok;
    readonly ImageList _icons = new() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
    List<InstalledApp> _apps = [];

    public string? SelectedPath { get; private set; }

    public InstalledAppsDialog()
    {
        Ui.Setup(this, "Choose an installed app", 560, 480);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 320);

        Ui.Label(this, "Search:", 12, 15);
        _search = Ui.Box(this, 70, 12, 478);
        _search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _search.PlaceholderText = "Type to filter…";

        _list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            Location = new Point(12, 44), Size = new Size(536, 384), SmallImageList = _icons,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _list.Columns.Add("App", 200);
        _list.Columns.Add("Location", 310);
        Controls.Add(_list);

        _status = Ui.Label(this, "Finding installed apps…", 12, 444, 330);
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _ok = Ui.Button(this, "Choose", 362, 438);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Enabled = false;
        var cancel = Ui.Button(this, "Cancel", 458, 438);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _ok;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _list.Items.Count > 0)
            {
                _list.Focus();
                _list.Items[0].Selected = _list.Items[0].Focused = true;
                e.Handled = true;
            }
        };
        _list.SelectedIndexChanged += (_, _) => _ok.Enabled = _list.SelectedItems.Count > 0;
        _list.DoubleClick += (_, _) => Choose();
        _ok.Click += (_, _) => Choose();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Focus();
        _apps = await Task.Run(AppScanner.Scan);
        _icons.Images.Add("none", SystemIcons.Application.ToBitmap());
        foreach (var a in _apps)
            if (a.Icon is not null) _icons.Images.Add(a.ExePath, a.Icon);
        Fill();
    }

    void Fill()
    {
        string q = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _apps)
        {
            if (q.Length > 0 && !a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) &&
                !a.ExePath.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            var item = new ListViewItem(a.Name) { ImageKey = a.Icon is not null ? a.ExePath : "none", Tag = a };
            item.SubItems.Add(a.ExePath);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _status.Text = $"{_list.Items.Count} of {_apps.Count} apps";
        _ok.Enabled = _list.SelectedItems.Count > 0;
    }

    void Choose()
    {
        if (_list.SelectedItems.Count == 0) return;
        SelectedPath = ((InstalledApp)_list.SelectedItems[0].Tag!).ExePath;
        DialogResult = DialogResult.OK;
    }
}
