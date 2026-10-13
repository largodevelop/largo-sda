namespace LargoSda.Core;

public sealed record MaFileLoadResult(IReadOnlyList<MaFile> Accounts, IReadOnlyList<string> Errors);

public static class MaFileStore
{
    /// <summary>Loads every *.maFile in <paramref name="directory"/>; broken files are reported, not thrown.</summary>
    public static MaFileLoadResult LoadDirectory(string directory)
    {
        var accounts = new List<MaFile>();
        var errors = new List<string>();

        if (!Directory.Exists(directory))
            return new MaFileLoadResult(accounts, [$"Folder not found: {directory}"]);

        foreach (var path in Directory.EnumerateFiles(directory, "*.maFile"))
        {
            try
            {
                var file = MaFile.Parse(File.ReadAllText(path));
                file.FilePath = path;
                if (string.IsNullOrEmpty(file.AccountName))
                    file.AccountName = Path.GetFileNameWithoutExtension(path);
                accounts.Add(file);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        accounts.Sort((a, b) => string.Compare(a.AccountName, b.AccountName, StringComparison.OrdinalIgnoreCase));
        return new MaFileLoadResult(accounts, errors);
    }

    /// <summary>Writes the file atomically (temp file + replace) so a crash never leaves a half-written maFile.</summary>
    public static void Save(MaFile file)
    {
        var path = file.FilePath ?? throw new InvalidOperationException("maFile has no path.");
        var temp = path + ".tmp";
        File.WriteAllText(temp, file.ToJson());
        File.Move(temp, path, overwrite: true);
    }
}
