namespace FixPortal.Ci.Backend.Api.Tests;

/// <summary>
/// A directory the test created itself, holding a witness file only the test wrote. Dispose deletes it
/// recursively, but only after printing the resolved path and confirming the witness, so a path that was
/// merely computed, or that production code regressed into, is never deleted on trust.
/// </summary>
internal sealed class OwnedTempDirectory : IDisposable
{
    private const string WitnessName = ".fixture-owner";
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public OwnedTempDirectory(string parent, string prefix)
    {
        Root = Path.GetFullPath(Path.Join(parent, prefix + _owner));
        if (Directory.Exists(Root) || File.Exists(Root))
        {
            throw new IOException($"Refusing to claim an existing fixture path: {Root}");
        }
        _ = Directory.CreateDirectory(Root);
        File.WriteAllText(WitnessPath, _owner);
        // Failed construction never reaches Dispose, so no unvalidated cleanup is attempted.
    }

    public string Root { get; }

    private string WitnessPath => Path.Join(Root, WitnessName);

    public void Dispose()
    {
        Console.WriteLine($"Owned fixture cleanup target: {Root}");
        // A regression under test may already have removed the directory; that must not mask its assertion.
        if (!Directory.Exists(Root))
        {
            Console.WriteLine("Owned fixture is already gone; nothing to delete.");
            return;
        }
        if (!File.Exists(WitnessPath) || File.ReadAllText(WitnessPath) != _owner)
        {
            Console.WriteLine($"Refusing recursive cleanup without the ownership witness: {Root}");
            return;
        }
        Directory.Delete(Root, recursive: true);
    }
}
