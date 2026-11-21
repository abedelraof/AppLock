using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

namespace AppLock;

/// Background service (runs as SYSTEM). After a correct password it removes an app's lock until
/// every copy of that app has exited, so apps that start helper copies of themselves work normally.
sealed class LockService : ServiceBase
{
    public const string Name = "AppLockSvc";
    public const string PipeName = "AppLockSvc";

    static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);     // time for the app to start after unlocking
    static readonly TimeSpan GoneFor = TimeSpan.FromSeconds(4);    // relock once no copy has run for this long

    sealed class Unlocked { public DateTime Since; public DateTime LastSeen; }

    readonly Dictionary<string, Unlocked> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _cts = new();
    readonly string _gate = Environment.ProcessPath!;

    public LockService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        RelockAll(); // restore every lock (e.g. after a crash or power loss while an app was unlocked)
        _ = Task.Run(() => PipeLoop(_cts.Token));
        _ = Task.Run(() => WatchLoop(_cts.Token));
    }

    protected override void OnStop() { _cts.Cancel(); RelockAll(); }
    protected override void OnShutdown() { _cts.Cancel(); RelockAll(); }

    void RelockAll()
    {
        lock (_unlocked) _unlocked.Clear();
        var cfg = ConfigStore.Load();
        if (cfg is null) return;
        try { Ifeo.Sync(cfg, _gate); } catch { /* conflicts are reported by the manager */ }
    }

    async Task PipeLoop(CancellationToken ct)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var opts = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, opts, 0, 0, sec);
                first = false;
                await pipe.WaitForConnectionAsync(ct);
                await Handle(pipe, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    async Task Handle(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, utf8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, utf8, 1024, leaveOpen: true) { AutoFlush = true };

        string? cmd = await reader.ReadLineAsync(timeout.Token);
        string? exe = await reader.ReadLineAsync(timeout.Token);
        string? pw = await reader.ReadLineAsync(timeout.Token);

        bool ok = false;
        if (cmd == "UNLOCK" && exe is not null && pw is not null)
        {
            var cfg = ConfigStore.Load();
            var app = cfg?.Find(exe);
            if (cfg is not null && app is not null && cfg.Verify(app, pw))
            {
                lock (_unlocked) _unlocked[app.ExeName] = new Unlocked { Since = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
                Ifeo.SetLocked(app.ExeName, false, _gate);
                ok = true;
            }
            else await Task.Delay(1000, ct); // slow down guessing
        }

        await writer.WriteLineAsync(ok ? "OK" : "NO");
        pipe.WaitForPipeDrain();
    }

    async Task WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }

            KeyValuePair<string, Unlocked>[] items;
            lock (_unlocked) items = _unlocked.ToArray();
            var now = DateTime.UtcNow;

            foreach (var (exe, st) in items)
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();

                if (running) { st.LastSeen = now; continue; }
                if (now - st.Since < Grace || now - st.LastSeen < GoneFor) continue;

                lock (_unlocked) _unlocked.Remove(exe);
                if (ConfigStore.Load()?.Find(exe) is not null)
                    try { Ifeo.SetLocked(exe, true, _gate); } catch { }
            }
        }
    }
}

/// Used by the gate to ask the service to unlock an app.
static class ServiceClient
{
    public static bool Unlock(string exeName, string password)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", LockService.PipeName, PipeDirection.InOut);
            c.Connect(3000);
            var utf8 = new UTF8Encoding(false);
            using var w = new StreamWriter(c, utf8, 1024, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(c, utf8, false, 1024, leaveOpen: true);
            w.WriteLine("UNLOCK");
            w.WriteLine(exeName);
            w.WriteLine(password);
            return r.ReadLine() == "OK";
        }
        catch { return false; }
    }
}

/// Registers / starts / stops the Windows service.
static class ServiceSetup
{
    public static void Stop()
    {
        try
        {
            using var sc = new ServiceController(LockService.Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (InvalidOperationException) { /* not installed */ }
    }

    public static void Remove()
    {
        Stop();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return;
        try
        {
            IntPtr svc = OpenServiceW(scm, LockService.Name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return;
            DeleteService(svc);
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }
    }

    public static void InstallAndStart(string exePath)
    {
        string bin = $"\"{exePath}\" --service";
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            IntPtr svc = OpenServiceW(scm, LockService.Name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                svc = CreateServiceW(scm, LockService.Name, "AppLock", SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, bin, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            }
            else if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_NO_CHANGE, bin,
                         null, IntPtr.Zero, null, null, null, "AppLock"))
            {
                CloseServiceHandle(svc);
                throw new System.ComponentModel.Win32Exception();
            }
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }

        using var sc = new ServiceController(LockService.Name);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
    }

    const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SERVICE_ALL_ACCESS = 0xF01FF, SERVICE_WIN32_OWN_PROCESS = 0x10,
        SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1, SERVICE_NO_CHANGE = 0xFFFFFFFF;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type, uint start,
        uint error, string bin, string? group, IntPtr tag, string? deps, string? user, string? pass);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string bin, string? group,
        IntPtr tag, string? deps, string? user, string? pass, string display);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DeleteService(IntPtr svc);
}
