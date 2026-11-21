using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AppLock;

static class Program
{
    [STAThread]
    static int Main()
    {
        ApplicationConfiguration.Initialize();

        // Gate mode: Windows runs  "AppLock.exe" --gate <original command line>
        CmdLine.First(Environment.CommandLine, out var rest);
        rest = rest.TrimStart();
        if (rest.StartsWith("--gate", StringComparison.Ordinal) &&
            (rest.Length == 6 || char.IsWhiteSpace(rest[6])))
        {
            return Gate.Run(rest.Substring(6).TrimStart());
        }

        if (rest.StartsWith("--service", StringComparison.Ordinal))
        {
            System.ServiceProcess.ServiceBase.Run(new LockService());
            return 0;
        }

        // Used by the installer (which already runs as admin).
        if (rest.StartsWith("--install-service", StringComparison.Ordinal))
        {
            ServiceSetup.InstallAndStart(Environment.ProcessPath!);
            return 0;
        }
        if (rest.StartsWith("--uninstall", StringComparison.Ordinal))
        {
            ServiceSetup.Remove();
            Ifeo.Sync(new AppConfig(), Environment.ProcessPath!); // removes every lock AppLock created
            return 0;
        }

        // Manager mode needs admin rights (it writes to HKLM and Program Files).
        if (!IsAdmin())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch (Win32Exception) { /* user declined UAC */ }
            return 0;
        }

        return Manager.Run();
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

static class CmdLine
{
    /// Splits off the first token of a Windows command line (handles quotes).
    public static string First(string s, out string rest)
    {
        s = s.TrimStart();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) { rest = ""; return s.Substring(1); }
            rest = s.Substring(end + 1);
            return s.Substring(1, end - 1);
        }
        int sp = s.IndexOfAny([' ', '\t']);
        if (sp < 0) { rest = ""; return s; }
        rest = s.Substring(sp);
        return s.Substring(0, sp);
    }
}
