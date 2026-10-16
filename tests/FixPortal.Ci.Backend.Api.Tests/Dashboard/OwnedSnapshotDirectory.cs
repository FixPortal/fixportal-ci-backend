using System.Runtime.ExceptionServices;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

internal sealed class OwnedSnapshotDirectory
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly string _witness;

    private OwnedSnapshotDirectory()
    {
        _root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"ci-snapshot-{_owner}"));
        _witness = Path.Join(_root, ".owner");
        if (Directory.Exists(_root) || File.Exists(_root))
        {
            throw new IOException($"Refusing existing fixture path: {_root}");
        }
        _ = Directory.CreateDirectory(_root);
        using var witness = new FileStream(_witness, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(witness);
        writer.Write(_owner);
        Console.WriteLine($"Owned snapshot fixture: {_root}; witness: {_witness}");
    }

    public string SnapshotPath => Path.Join(_root, "snapshot.json");

    public static async Task RunAsync(Func<OwnedSnapshotDirectory, Task> body)
    {
        // Failed construction never arms cleanup against an unowned directory.
        var fixture = new OwnedSnapshotDirectory();
        Exception? primary = null;
        try
        {
            await body(fixture);
        }
        catch (Exception ex)
        {
            primary = ex;
        }
        try
        {
            fixture.Cleanup();
        }
        catch (Exception cleanup) when (primary is not null)
        {
            primary.Data["OwnedSnapshotDirectory cleanup failure"] = cleanup.ToString();
            Console.Error.WriteLine(cleanup);
        }
        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    private void Cleanup()
    {
        if (File.ReadAllText(_witness) != _owner)
        {
            throw new IOException($"Refusing cleanup without ownership witness: {_root}");
        }
        string[] ownedFiles = [SnapshotPath, SnapshotPath + ".tmp", _witness];
        if (Directory.EnumerateFileSystemEntries(_root).Any(entry => !ownedFiles.Contains(entry)))
        {
            throw new IOException($"Refusing cleanup of unexpected fixture contents: {_root}");
        }
        Console.WriteLine($"Cleaning owned snapshot files in: {_root}");
        foreach (var file in ownedFiles)
        {
            File.Delete(file);
        }
        Directory.Delete(_root, recursive: false);
    }
}
