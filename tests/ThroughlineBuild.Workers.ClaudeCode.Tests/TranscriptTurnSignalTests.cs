using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class TranscriptTurnSignalTests : IDisposable
{
    // Hermetic config home so the test never reads or writes the real ~/.claude tree.
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), $"lattice-turn-signal-{Guid.NewGuid():N}");
    private readonly TimeSpan _poll = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task WaitForTurnAsync_EndTurnForMatchingCwd_Completes()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        WriteTranscript("session-a.jsonl", worktree, stopReason: "end_turn");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await signal.WaitForTurnAsync(worktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        // Completing without throwing is the assertion.
    }

    [Fact]
    public async Task WaitForTurnAsync_NoEndTurn_KeepsWaiting()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        // An assistant message that is still streaming (tool_use), not end_turn.
        WriteTranscript("session-a.jsonl", worktree, stopReason: "tool_use");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource();
        var wait = signal.WaitForTurnAsync(worktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(wait, completed); // it did not complete within the window
        cts.Cancel();
        await SwallowCancel(wait);
    }

    [Fact]
    public async Task WaitForTurnAsync_EndTurnForDifferentCwd_KeepsWaiting()
    {
        var worktree = Path.Combine(_configHome, "this run");
        var otherWorktree = Path.Combine(_configHome, "another run");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(otherWorktree);
        WriteTranscript("other.jsonl", otherWorktree, stopReason: "end_turn");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource();
        var wait = signal.WaitForTurnAsync(worktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(wait, completed); // a different cwd's end_turn does not satisfy this run
        cts.Cancel();
        await SwallowCancel(wait);
    }

    private static async Task SwallowCancel(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task WaitForTurnAsync_IsCancellationAware()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        WriteTranscript("session-a.jsonl", worktree, stopReason: "tool_use"); // never end_turn

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource();
        var wait = signal.WaitForTurnAsync(worktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForTurnAsync_SymlinkWorktree_MatchesResolvedTranscriptCwd()
    {
        // macOS exposes /var as a symlink to /private/var, so Path.GetTempPath() hands
        // back an unresolved worktree while claude records the resolved cwd. The detector
        // must canonicalize both so the symlink path matches. Unix-only: Windows temp has
        // no parent symlinks and Directory.CreateSymbolicLink needs elevation there.
        if (OperatingSystem.IsWindows()) return;

        // The REAL worktree (what claude resolves cwd to) and a symlink that points at it.
        var realWorktree = Path.Combine(_configHome, "real worktree");
        Directory.CreateDirectory(realWorktree);
        var resolvedCwd = ClaudeRealPath.Resolve(realWorktree);
        var symlinkWorktree = Path.Combine(_configHome, "symlink worktree");
        Directory.CreateSymbolicLink(symlinkWorktree, realWorktree);

        // Transcript records the RESOLVED real path as cwd.
        WriteTranscript("session-a.jsonl", resolvedCwd, stopReason: "end_turn");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Pass the SYMLINK path as the worktree. Without canonicalization this hangs.
        await signal.WaitForTurnAsync(symlinkWorktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        // Completing without throwing (i.e. before the 10s token fires) is the assertion.
    }

    [Fact]
    public void ClaudeRealPath_Resolve_ResolvesSymlinkToRealPath()
    {
        // Unix-only symlink resolution check. On Windows Resolve is just GetFullPath.
        if (OperatingSystem.IsWindows()) return;

        var realDir = Path.Combine(_configHome, "real dir");
        Directory.CreateDirectory(realDir);
        var linkDir = Path.Combine(_configHome, "link dir");
        Directory.CreateSymbolicLink(linkDir, realDir);

        var resolvedReal = ClaudeRealPath.Resolve(realDir);
        var resolvedLink = ClaudeRealPath.Resolve(linkDir);

        // The symlink resolves to the same canonical path as the real directory.
        Assert.Equal(resolvedReal, resolvedLink);
    }

    [Fact]
    public async Task WaitForTurnAsync_ToleratesUnparseableLinesAndMissingFields()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        var dir = Path.Combine(_configHome, "projects", "encoded-name");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session-a.jsonl"), string.Join('\n',
            "this is not json",
            "{}",
            "[1,2,3]",
            AssistantLine(worktree, "end_turn"),
            "{ broken"));

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await signal.WaitForTurnAsync(worktree, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
    }

    private void WriteTranscript(string fileName, string cwd, string stopReason)
    {
        var dir = Path.Combine(_configHome, "projects", "encoded-name");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), AssistantLine(cwd, stopReason) + "\n");
    }

    // Built by concatenation rather than a raw interpolated string so the trailing
    // JSON object braces never collide with the interpolation's closing braces.
    private static string AssistantLine(string cwd, string stopReason) =>
        "{\"type\":\"assistant\",\"cwd\":" + Json(cwd) +
        ",\"message\":{\"id\":\"m1\",\"stop_reason\":\"" + stopReason + "\"}}";

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    public void Dispose()
    {
        if (Directory.Exists(_configHome)) Directory.Delete(_configHome, recursive: true);
    }
}
