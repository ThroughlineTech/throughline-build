using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class TranscriptTurnSignalTests : IDisposable
{
    // Hermetic config home so the test never reads or writes the real ~/.claude tree.
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), $"lattice-turn-signal-{Guid.NewGuid():N}");
    private readonly TimeSpan _poll = TimeSpan.FromMilliseconds(20);

    // The per-run correlation token the transport embeds in the prompt; the transcript
    // must carry it for the turn-detector to claim it. Most tests reuse one value.
    private const string Nonce = "run-nonce-abcdef0123456789";

    [Fact]
    public async Task WaitForTurnAsync_EndTurnForMatchingCwd_Completes()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        WriteTranscript("session-a.jsonl", worktree, stopReason: "end_turn");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await signal.WaitForTurnAsync(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
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
        var wait = signal.WaitForTurnAsync(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

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
        var wait = signal.WaitForTurnAsync(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(wait, completed); // a different cwd's end_turn does not satisfy this run
        cts.Cancel();
        await SwallowCancel(wait);
    }

    [Fact]
    public async Task WaitForTurnAsync_StalePriorRunSameWorktree_WaitsForThisRunsNonce()
    {
        // Sequential same-worktree: a PRIOR run left an end_turn transcript in the SAME
        // worktree (same cwd). Without the nonce it would immediately complete this run
        // and return the prior result. The nonce makes the detector wait for THIS run's
        // transcript.
        var worktree = Path.Combine(_configHome, "shared worktree");
        Directory.CreateDirectory(worktree);
        WriteTranscript("prior-run.jsonl", worktree, stopReason: "end_turn", nonce: "prior-run-nonce-0000");

        const string thisRunNonce = "this-run-nonce-1111";
        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = signal.WaitForTurnAsync(worktree, thisRunNonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

        // The stale prior-run transcript must NOT complete this run.
        var early = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(wait, early);

        // This run's transcript appears; now it completes - and resolves to THIS run's file.
        WriteTranscript("this-run.jsonl", worktree, stopReason: "end_turn", nonce: thisRunNonce);
        var matched = await wait;
        Assert.Equal(TranscriptPath("this-run.jsonl"), matched);
    }

    [Fact]
    public async Task WaitForTurnAsync_CompetingSessionNewerSameCwd_MatchesThisRunsNonce()
    {
        // Competing session: another claude session wrote a NEWER transcript for the same
        // cwd while this run was in flight. Recency-based selection would pick the
        // competitor; the nonce must override recency and select THIS run's transcript.
        var worktree = Path.Combine(_configHome, "contended worktree");
        Directory.CreateDirectory(worktree);
        const string thisRunNonce = "this-run-nonce-2222";

        WriteTranscript("this-run.jsonl", worktree, stopReason: "end_turn", nonce: thisRunNonce);
        WriteTranscript("competitor.jsonl", worktree, stopReason: "end_turn", nonce: "competing-nonce-3333");
        // Force the competitor to be the NEWEST file so a recency-only locator would pick it.
        File.SetLastWriteTimeUtc(TranscriptPath("competitor.jsonl"), DateTime.UtcNow.AddMinutes(5));
        File.SetLastWriteTimeUtc(TranscriptPath("this-run.jsonl"), DateTime.UtcNow);

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var matched = await signal.WaitForTurnAsync(worktree, thisRunNonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);

        Assert.Equal(TranscriptPath("this-run.jsonl"), matched);
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
        var wait = signal.WaitForTurnAsync(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
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
        await signal.WaitForTurnAsync(symlinkWorktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
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
            UserLine(worktree, Nonce),
            AssistantLine(worktree, "end_turn"),
            "{ broken"));

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await signal.WaitForTurnAsync(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
    }

    [Fact]
    public void DescribeCandidates_ReportsPerCandidateCwdMtimeEndTurnAndNonce()
    {
        var worktree = Path.Combine(_configHome, "work tree");
        Directory.CreateDirectory(worktree);
        var resolved = ClaudeRealPath.Resolve(worktree);
        WriteTranscript("matching.jsonl", resolved, stopReason: "end_turn");
        WriteTranscript("other.jsonl", Path.Combine(_configHome, "elsewhere"), stopReason: "tool_use");

        var signal = new TranscriptTurnSignal(_configHome, _poll);
        var report = signal.DescribeCandidates(worktree, Nonce, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Contains("config_home=", report);
        Assert.Contains("run_nonce=" + Nonce, report);
        Assert.Contains("matching.jsonl", report);
        Assert.Contains("other.jsonl", report);
        Assert.Contains("cwd_matched=True", report);
        Assert.Contains("assistant_end_turn=True", report);
        Assert.Contains("nonce_matched=True", report);
        Assert.Contains("worktree_resolved=", report);
    }

    [Fact]
    public void DescribeCandidates_MissingProjectsRoot_NeverThrows()
    {
        var signal = new TranscriptTurnSignal(_configHome, _poll);
        // _configHome has no projects/ subdir yet.
        var report = signal.DescribeCandidates(Path.Combine(_configHome, "nope"), Nonce, DateTimeOffset.UtcNow);
        Assert.Contains("projects_root_exists=false", report);
    }

    private void WriteTranscript(string fileName, string cwd, string stopReason, string nonce = Nonce)
    {
        var dir = Path.Combine(_configHome, "projects", "encoded-name");
        Directory.CreateDirectory(dir);
        // Two lines, mirroring a real claude transcript: the user prompt line carries the
        // run nonce (verbatim, as claude persists it), the assistant line carries the
        // turn-end. The locator scans every line, so cwd + end_turn + nonce can be spread.
        File.WriteAllText(Path.Combine(dir, fileName),
            UserLine(cwd, nonce) + "\n" + AssistantLine(cwd, stopReason) + "\n");
    }

    private string TranscriptPath(string fileName) =>
        Path.Combine(_configHome, "projects", "encoded-name", fileName);

    // A user-prompt line whose content embeds the nonce exactly as the transport's
    // BuildInitialPrompt writes it, so the raw-substring nonce match exercises reality.
    private static string UserLine(string cwd, string nonce) =>
        "{\"type\":\"user\",\"cwd\":" + Json(cwd) +
        ",\"message\":{\"role\":\"user\",\"content\":" +
        Json("Read .build/brief.md, execute it completely.\n(latticeflow run token, ignore: " + nonce + ")") + "}}";

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
