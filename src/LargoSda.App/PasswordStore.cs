using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LargoSda.App;

/// <summary>
/// Account passwords, encrypted with Windows DPAPI (current user) and stored next to the maFiles,
/// so they move with the folder but only decrypt for the same Windows user.
/// </summary>
public sealed class PasswordStore
{
    public const string FileName = "passwords.dpapi.json";

    private static readonly byte[] Entropy = "LargoSDA.passwords.v1"u8.ToArray();

    private readonly string _path;
    private readonly Dictionary<string, string> _blobs;

    private PasswordStore(string path, Dictionary<string, string> blobs)
    {
        _path = path;
        _blobs = blobs;
    }

    public static PasswordStore Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            var blobs = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                : null;
            return new PasswordStore(path, new Dictionary<string, string>(blobs ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return new PasswordStore(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public bool Has(string login) => _blobs.ContainsKey(login);

    /// <summary>Adds passwords from another folder's store for logins that have none here. Returns how many were added.</summary>
    public int MergeFrom(string otherFolder)
    {
        var other = Load(otherFolder);
        if (string.Equals(Path.GetFullPath(other._path), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            return 0;

        var added = 0;
        foreach (var (login, blob) in other._blobs)
        {
            if (_blobs.ContainsKey(login)) continue;
            _blobs[login] = blob;
            added++;
        }
        if (added > 0) Write();
        return added;
    }

    /// <summary>Null when there is no password or it was saved by another Windows user / PC.</summary>
    public string? Get(string login)
    {
        if (!OperatingSystem.IsWindows() || !_blobs.TryGetValue(login, out var blob)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Set(string login, string? password)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Сохранение паролей пока доступно только в Windows.");

        if (string.IsNullOrEmpty(password))
            _blobs.Remove(login);
        else
            _blobs[login] = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

        Write();
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_blobs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
