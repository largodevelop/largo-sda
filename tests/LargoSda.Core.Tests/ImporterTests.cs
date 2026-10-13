using LargoSda.Core;
using Xunit;

namespace LargoSda.Core.Tests;

public sealed class ImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "largosda-tests-" + Guid.NewGuid().ToString("N"));

    public ImporterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string folder, string name, string secret, ulong steamId, string extra = "") =>
        File.WriteAllText(Path.Combine(folder, name + ".maFile"),
            $$"""{"shared_secret":"{{secret}}","account_name":"{{name}}","Session":{"SteamID":{{steamId}}}{{extra}}}""");

    [Fact]
    public void CopiesNewAccountsAndKeepsSources()
    {
        var target = Folder("mine");
        var other = Folder("other");
        Write(target, "a", "AAAA", 1);
        Write(other, "b", "BBBB", 2);
        Write(other, "c", "CCCC", 3);

        var result = MaFileImporter.Import([other], target);

        Assert.Equal(2, result.Added);
        Assert.Equal(3, Directory.GetFiles(target, "*.maFile").Length);
        Assert.Equal(2, Directory.GetFiles(other, "*.maFile").Length);
    }

    [Fact]
    public void NeverOverwritesAnAccountThatIsAlreadyPresent()
    {
        var target = Folder("mine");
        var other = Folder("other");
        Write(target, "a", "AAAA", 1, ""","marker":"local" """.TrimEnd());
        Write(other, "a", "AAAA", 1);

        var result = MaFileImporter.Import([other], target);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Contains("local", File.ReadAllText(Path.Combine(target, "a.maFile")));
    }

    [Fact]
    public void RenamesWhenDifferentAccountHasSameFileName()
    {
        var target = Folder("mine");
        var other = Folder("other");
        Write(target, "a", "AAAA", 1);
        Write(other, "a", "ZZZZ", 9);

        MaFileImporter.Import([other], target);

        Assert.True(File.Exists(Path.Combine(target, "a (2).maFile")));
    }

    [Fact]
    public void FindsNestedMaFilesFolderAndReportsBrokenFiles()
    {
        var target = Folder("mine");
        var sda = Folder("sda");
        var nested = Folder(Path.Combine("sda", "maFiles"));
        Write(nested, "b", "BBBB", 2);
        File.WriteAllText(Path.Combine(nested, "broken.maFile"), "{not json");

        var result = MaFileImporter.Import([sda], target);

        Assert.Equal(1, result.Added);
        Assert.Single(result.Errors);
    }
}
