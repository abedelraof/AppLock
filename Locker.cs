using Microsoft.Win32;

namespace AppLock;

/// Uses "Image File Execution Options" so Windows runs the AppLock gate whenever a locked exe starts.
static class Ifeo
{
    const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    const string Marker = "AppLockManaged";

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static void Sync(AppConfig cfg, string gatePath)
    {
        using var hklm = Hklm;
        using var root = hklm.OpenSubKey(Root, writable: true)!;

        // Remove locks we created that are no longer configured.
        // Some entries (e.g. Microsoft Defender's) deny even admins, so peek read-only and skip what we can't open.
        foreach (string name in root.GetSubKeyNames())
        {
            if (cfg.Find(name) is not null) continue;
            try
            {
                using (var k = root.OpenSubKey(name, writable: false))
                    if (k?.GetValue(Marker) is null) continue;
                Unlock(root, name);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        string debugger = $"\"{gatePath}\" --gate";
        var conflicts = new List<string>();
        foreach (var app in cfg.Apps)
        {
            try
            {
                using var k = root.CreateSubKey(app.ExeName, writable: true);
                var existing = k.GetValue("Debugger") as string;
                if (existing is not null && k.GetValue(Marker) is null)
                {
                    conflicts.Add($"{app.ExeName}: already has a debugger set by another program ({existing})");
                    continue;
                }
                k.SetValue("Debugger", debugger, RegistryValueKind.String);
                k.SetValue(Marker, 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                conflicts.Add($"{app.ExeName}: Windows protects this program, so it can't be locked");
            }
        }

        if (conflicts.Count > 0)
            throw new InvalidOperationException("Some apps could not be locked:\n\n" + string.Join("\n", conflicts));
    }

    /// Temporarily turn one of our locks off/on (used by the service).
    public static void SetLocked(string exeName, bool locked, string gatePath)
    {
        using var hklm = Hklm;
        using var k = hklm.OpenSubKey(Root + "\\" + exeName, writable: true);
        if (k?.GetValue(Marker) is null) return;
        if (locked) k.SetValue("Debugger", $"\"{gatePath}\" --gate", RegistryValueKind.String);
        else k.DeleteValue("Debugger", throwOnMissingValue: false);
    }

    static void Unlock(RegistryKey root, string name)
    {
        using (var k = root.OpenSubKey(name, writable: true))
        {
            if (k is null) return;
            k.DeleteValue("Debugger", throwOnMissingValue: false);
            k.DeleteValue(Marker, throwOnMissingValue: false);
            if (k.ValueCount > 0 || k.SubKeyCount > 0) return;
        }
        root.DeleteSubKey(name, throwOnMissingSubKey: false);
    }
}

/// Copies AppLock into Program Files so the gate lives somewhere normal users can't tamper with.
static class Installer
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AppLock");

    public static string EnsureInstalled()
    {
        string self = Environment.ProcessPath!;
        string target = Path.Combine(InstallDir, "AppLock.exe");
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            ServiceSetup.InstallAndStart(target);
            return target;
        }

        Directory.CreateDirectory(InstallDir);
        if (!File.Exists(target) || !SameFile(self, target))
        {
            ServiceSetup.Stop(); // the service keeps the installed exe open
            for (int i = 0; ; i++)
            {
                try { File.Copy(self, target, overwrite: true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }
                catch (IOException) when (File.Exists(target)) { break; /* in use by an open password prompt; keep old copy */ }
            }
        }
        ServiceSetup.InstallAndStart(target);
        return target;
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
