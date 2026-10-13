using System.Text.Json;

namespace LargoSda.App;

/// <summary>
/// Portable preferences: LargoSDA.settings.json next to the exe.
/// By default the app uses its own "maFiles" folder next to the exe — nothing has to be chosen.
/// A folder inside the app folder is stored as a relative path, so the whole app folder can be moved.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Null means the app's own folder.</summary>
    public string? MaFilesFolder { get; set; }

    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string PortablePath = Path.Combine(AppDir, "LargoSDA.settings.json");

    // Used only when the app folder is read-only (e.g. Program Files).
    private static readonly string FallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LargoSDA", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultFolder => Path.Combine(AppDir, "maFiles");

    /// <summary>Absolute path of the maFiles folder.</summary>
    public string ResolvedFolder =>
        string.IsNullOrEmpty(MaFilesFolder) ? DefaultFolder : Path.GetFullPath(MaFilesFolder, AppDir);

    public bool IsDefaultFolder =>
        string.Equals(Path.TrimEndingDirectorySeparator(ResolvedFolder), Path.TrimEndingDirectorySeparator(DefaultFolder),
            StringComparison.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        var path = File.Exists(PortablePath) ? PortablePath : AppDirWritable() ? null : FallbackPath;
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch
        {
            // Broken settings file: start with defaults.
        }
        return new AppSettings();
    }

    public void SetFolder(string? folder)
    {
        if (folder is null)
        {
            MaFilesFolder = null;
        }
        else
        {
            var relative = Path.GetRelativePath(AppDir, folder);
            MaFilesFolder = relative.StartsWith("..") || Path.IsPathRooted(relative) ? folder : relative;
        }
        Save();
    }

    public void Save()
    {
        var text = JsonSerializer.Serialize(this, Json);
        try
        {
            File.WriteAllText(PortablePath, text);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            File.WriteAllText(FallbackPath, text);
        }
    }

    private static bool AppDirWritable()
    {
        try
        {
            var probe = Path.Combine(AppDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
