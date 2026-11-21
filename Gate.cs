using System.Runtime.InteropServices;
using System.Text;

namespace AppLock;

/// Runs instead of a locked program: asks for the password, then starts the real program.
static class Gate
{
    public static int Run(string targetCmdLine)
    {
        if (string.IsNullOrWhiteSpace(targetCmdLine)) return 1;

        string exe = CmdLine.First(targetCmdLine, out _);
        string exeName = Path.GetFileName(exe);

        var cfg = ConfigStore.Load();
        if (cfg is null)
        {
            // Windows says this app is locked, but we can't read the settings: stay locked.
            MessageBox.Show($"{exeName} is locked, but AppLock's settings could not be read.\n\nOpen AppLock to fix this.",
                "AppLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var app = cfg.Find(exeName);

        string? appPath = null;
        if (Path.IsPathRooted(exe) && File.Exists(exe)) appPath = exe;
        else if (app?.Path is { } p && File.Exists(p)) appPath = p;

        if (app is not null)
        {
            Icon? icon = null;
            try { if (appPath is not null) icon = Icon.ExtractAssociatedIcon(appPath); } catch { }

            string display = Path.GetFileNameWithoutExtension(exeName);
            using var dlg = new PasswordDialog($"{display} is locked",
                $"{display} is locked.\nEnter the password to open it.",
                pw => cfg.Verify(app, pw), icon, "Unlock");
            if (dlg.ShowDialog() != DialogResult.OK) return 1;

            // Ask the service to lift the lock until the app fully closes, so the app's own
            // helper processes (Chrome, Discord, …) start without being stopped by the prompt.
            if (!ServiceClient.Unlock(app.ExeName, dlg.Password))
                MessageBox.Show("The AppLock service isn't running, so this app may ask for the password again.\n\n" +
                    "Open AppLock once to restart the service.", "AppLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return Launch(appPath, targetCmdLine) ? 0 : 1;
    }

    /// Starting the process "under a debugger" makes Windows skip the IFEO redirect (no loop back to us);
    /// we then detach immediately so it runs normally.
    static bool Launch(string? appPath, string cmdLine)
    {
        GetStartupInfoW(out var si);
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        var cmd = new StringBuilder(cmdLine, cmdLine.Length + 1);

        // Handles the caller meant for the app were inherited by us; pass them on.
        if (!CreateProcessW(appPath, cmd, IntPtr.Zero, IntPtr.Zero, true, DEBUG_ONLY_THIS_PROCESS,
                IntPtr.Zero, null, ref si, out var pi))
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err == 740
                ? "This program needs administrator rights. Right-click it and choose \"Run as administrator\"."
                : new System.ComponentModel.Win32Exception(err).Message;
            MessageBox.Show($"Could not start the program.\n\n{msg}", "AppLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        DebugSetProcessKillOnExit(false);
        DebugActiveProcessStop(pi.dwProcessId);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }

    const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    static extern void GetStartupInfoW(out STARTUPINFO si);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll")] static extern bool DebugActiveProcessStop(int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
