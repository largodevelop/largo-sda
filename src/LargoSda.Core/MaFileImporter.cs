namespace LargoSda.Core;

public sealed record MaFileImportResult(int Added, int AlreadyPresent, IReadOnlyList<string> Errors,
    IReadOnlyList<string> SourceFolders)
{
    public int Total => Added + AlreadyPresent + Errors.Count;
}

/// <summary>
/// Merges .maFile files from other folders into the app's folder. Files are copied, never moved:
/// sources stay untouched, and an account that is already present is never overwritten
/// (the local copy may hold a newer Steam session).
/// </summary>
public static class MaFileImporter
{
    /// <param name="sources">Folders and/or individual .maFile paths (e.g. from drag &amp; drop).</param>
    public static MaFileImportResult Import(IEnumerable<string> sources, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var target = Path.GetFullPath(targetFolder);

        var existing = MaFileStore.LoadDirectory(target).Accounts;
        var knownSteamIds = existing.Where(a => a.SteamId != 0).Select(a => a.SteamId).ToHashSet();
        var knownSecrets = existing.Select(a => a.SharedSecret).ToHashSet();

        int added = 0, present = 0;
        var errors = new List<string>();
        var folders = new List<string>();

        foreach (var path in ExpandSources(sources, folders))
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), target, StringComparison.OrdinalIgnoreCase))
            {
                present++; // dropped our own folder / file
                continue;
            }

            MaFile file;
            try
            {
                file = MaFile.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            if ((file.SteamId != 0 && knownSteamIds.Contains(file.SteamId)) || knownSecrets.Contains(file.SharedSecret))
            {
                present++;
                continue;
            }

            var destination = UniquePath(target, Path.GetFileNameWithoutExtension(path));
            File.Copy(path, destination);
            if (file.SteamId != 0) knownSteamIds.Add(file.SteamId);
            knownSecrets.Add(file.SharedSecret);
            added++;
        }

        return new MaFileImportResult(added, present, errors, folders);
    }

    /// <summary>Folders give their *.maFile (also a nested "maFiles" folder, e.g. when the SDA folder itself is dropped).</summary>
    private static IEnumerable<string> ExpandSources(IEnumerable<string> sources, List<string> folders)
    {
        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                if (source.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(source))!;
                    if (!folders.Contains(dir, StringComparer.OrdinalIgnoreCase)) folders.Add(dir);
                    yield return source;
                }
                continue;
            }
            if (!Directory.Exists(source)) continue;

            var folder = Directory.EnumerateFiles(source, "*.maFile").Any()
                ? source
                : Path.Combine(source, "maFiles");
            if (!Directory.Exists(folder)) continue;

            folders.Add(Path.GetFullPath(folder));
            foreach (var file in Directory.EnumerateFiles(folder, "*.maFile"))
                yield return file;
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".maFile");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}).maFile");
        return path;
    }
}
