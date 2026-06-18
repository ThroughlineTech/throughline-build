using System;
using System.IO;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// The worktree-confined reader that backs the pre-load mechanism. The reads/misses prove the wiring;
/// the containment cases prove a path that resolves outside the worktree is refused (defense in depth
/// over PreloadedContextBuilder's own path-validity gate).
/// </summary>
public class ImplementPhaseWorktreeReaderTests : IDisposable
{
    private readonly string _root;

    public ImplementPhaseWorktreeReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tlb-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "data"));
        File.WriteAllText(Path.Combine(_root, "src", "data", "types.ts"), "export type Survey = {};");
        // A sibling secret OUTSIDE the worktree root, to prove `..`-escape is refused.
        File.WriteAllText(Path.Combine(_root, "..", "secret-" + Path.GetFileName(_root) + ".txt"), "TOPSECRET");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(Path.Combine(_root, "..", "secret-" + Path.GetFileName(_root) + ".txt")); } catch { }
    }

    [Fact]
    public void Reads_ExistingFile_UnderRoot()
    {
        var read = ImplementPhase.MakeWorktreeReader(_root);
        Assert.Equal("export type Survey = {};", read("src/data/types.ts"));
    }

    [Fact]
    public void Returns_Null_ForMissingFile()
    {
        var read = ImplementPhase.MakeWorktreeReader(_root);
        Assert.Null(read("src/data/nope.ts"));
    }

    [Fact]
    public void Returns_Null_ForParentEscape()
    {
        var read = ImplementPhase.MakeWorktreeReader(_root);
        var escape = "../secret-" + Path.GetFileName(_root) + ".txt";
        Assert.Null(read(escape)); // resolves outside the worktree root -> refused
    }

    [Fact]
    public void Returns_Null_ForAbsolutePathOutsideRoot()
    {
        var read = ImplementPhase.MakeWorktreeReader(_root);
        var outside = Path.Combine(_root, "..", "secret-" + Path.GetFileName(_root) + ".txt");
        Assert.Null(read(Path.GetFullPath(outside)));
    }
}
