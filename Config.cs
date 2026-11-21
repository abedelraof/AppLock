using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace AppLock;

public sealed class LockedApp
{
    public string ExeName { get; set; } = "";
    public string? Path { get; set; }
    public bool UseMaster { get; set; } = true;
    public string? Hash { get; set; }
    public string? Salt { get; set; }
}

public sealed class AppConfig
{
    public string? MasterHash { get; set; }
    public string? MasterSalt { get; set; }
    public List<LockedApp> Apps { get; set; } = [];

    public LockedApp? Find(string exeName) =>
        Apps.FirstOrDefault(a => string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase));

    public bool Verify(LockedApp app, string password) =>
        app.UseMaster ? Crypto.Verify(password, MasterHash, MasterSalt)
                      : Crypto.Verify(password, app.Hash, app.Salt);
}

static class ConfigStore
{
    public static string Dir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLock");

    public static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppConfig? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
        }
        catch { return null; }
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        SecureDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// Admins/SYSTEM can change the config; normal users can only read it (the gate needs to read it).
    static void SecureDirectory(string dir)
    {
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(ds);
    }
}

static class Crypto
{
    const int Iterations = 210_000;

    public static (string hash, string salt) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] h = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(h), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hash, string? salt)
    {
        if (hash is null || salt is null) return false;
        try
        {
            byte[] expected = Convert.FromBase64String(hash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt),
                Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }
}
