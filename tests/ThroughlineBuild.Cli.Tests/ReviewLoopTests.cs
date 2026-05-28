using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for ReviewLoop. All console I/O, DraftPhase invocations, and editor launches
/// are injected via fakes. No subprocess infrastructure is required.
/// </summary>
public class ReviewLoopTests
{
    // ------------------------------------------------------------------
    // FakeConsole
    // ------------------------------------------------------------------

    /// <summary>
    /// Drives test I/O deterministically.
    /// KeyChars is a queue of single keystrokes; Lines is a queue of ReadLine responses.
    /// Written/ErrorWritten capture output for assertions.
    /// </summary>
    private sealed class FakeConsole : IConsole
    {
        private readonly Queue<char> _keyChars;
        private readonly Queue<string> _lines;

        public List<string> Written { get; } = new();
        public List<string> ErrorWritten { get; } = new();

        public bool IsInputRedirected { get; init; } = true; // default: non-TTY for determinism

        public FakeConsole(IEnumerable<char>? keyChars = null, IEnumerable<string>? lines = null)
        {
            _keyChars = new Queue<char>(keyChars ?? Enumerable.Empty<char>());
            _lines = new Queue<string>(lines ?? Enumerable.Empty<string>());
        }

        public void WriteLine(string value) => Written.Add(value);
        public void Write(string value) => Written.Add(value);
        public void ErrorWriteLine(string value) => ErrorWritten.Add(value);

        public string? ReadLine()
        {
            if (_lines.TryDequeue(out var line)) return line;
            return string.Empty;
        }

        public char? ReadKeyChar()
        {
            if (_keyChars.TryDequeue(out var ch)) return ch;
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DraftPhaseInvoker NoOpInvoker() =>
        (_, _) => throw new InvalidOperationException("DraftPhaseInvoker should not be called in this test");

    private static DraftPhaseInvoker SuccessInvoker(string body) =>
        (_, _) => Task.FromResult(new DraftResult(DraftOutcome.Ok, body, null));

    private static DraftPhaseInvoker FailureInvoker(string reason) =>
        (_, _) => Task.FromResult(new DraftResult(DraftOutcome.WorkerFailed, null, reason));

    private static EditorResolver NoOpEditorResolver() =>
        () => throw new InvalidOperationException("EditorResolver should not be called in this test");

    private static EditorResolver NullEditorResolver() => () => null;

    /// <summary>
    /// FileEditorInvoker that writes fixedContent to the temp file, simulating an editor save.
    /// </summary>
    private static FileEditorInvoker MockFileEditorInvoker(string fixedContent) =>
        (tempFilePath, currentBody, _, _) =>
        {
            File.WriteAllText(tempFilePath, fixedContent);
            return Task.FromResult(fixedContent);
        };

    private static ReviewLoop MakeLoop(
        FakeConsole console,
        DraftPhaseInvoker? draftInvoker = null,
        EditorResolver? editorResolver = null,
        FileEditorInvoker? fileEditorInvoker = null)
    {
        return new ReviewLoop(
            console,
            draftInvoker ?? NoOpInvoker(),
            editorResolver ?? NoOpEditorResolver(),
            fileEditorInvoker ?? ((_, body, _, _) => Task.FromResult(body)));
    }

    // ------------------------------------------------------------------
    // Accept path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Accept_ReturnsAcceptedWithInitialBody()
    {
        const string initialBody = "# My Ticket\n\nSome description.";
        var console = new FakeConsole(keyChars: new[] { 'a' });
        var loop = MakeLoop(console);

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.Equal(initialBody, result.FinalBody);
    }

    // ------------------------------------------------------------------
    // Quit path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Quit_ReturnsAbortedWithNullBody()
    {
        const string initialBody = "# My Ticket\n\nSome description.";
        var console = new FakeConsole(keyChars: new[] { 'q' });
        var loop = MakeLoop(console);

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Aborted, result.Outcome);
        Assert.Null(result.FinalBody);
    }

    // ------------------------------------------------------------------
    // Edit path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Edit_MockEditor_UpdatesBodyAndContinues()
    {
        const string initialBody = "# Draft 1\n\nOriginal.";
        const string editedBody = "# Draft 1 (edited)\n\nModified by editor.";

        // 'e' to edit (mock editor writes editedBody), then 'a' to accept.
        var console = new FakeConsole(keyChars: new[] { 'e', 'a' });
        var loop = MakeLoop(
            console,
            fileEditorInvoker: MockFileEditorInvoker(editedBody));

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.Equal(editedBody, result.FinalBody);
    }

    [Fact]
    public async Task Edit_NoEditorAvailable_StaysInLoopWithOriginalBody()
    {
        const string initialBody = "# Draft 1\n\nFirst version.";

        // 'e' edit (no editor found), then 'q' quit.
        var console = new FakeConsole(keyChars: new[] { 'e', 'q' });
        // Use actual DefaultFileEditorInvoker path by passing NullEditorResolver + default invoker.
        var loop = new ReviewLoop(console, NoOpInvoker(), NullEditorResolver());

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Aborted, result.Outcome);
        Assert.Contains(console.ErrorWritten, msg => msg.Contains("no editor found"));
    }

    // ------------------------------------------------------------------
    // Regenerate path - success
    // ------------------------------------------------------------------

    [Fact]
    public async Task Regenerate_Success_UpdatesBodyAndContinues()
    {
        const string initialBody = "# Draft 1\n\nFirst version.";
        const string regeneratedBody = "# Draft 2\n\nRegenerated version.";

        // 'r' to regenerate (no extra context), then 'a' to accept.
        var console = new FakeConsole(
            keyChars: new[] { 'r', 'a' },
            lines: new[] { "" }); // empty extra context

        var loop = MakeLoop(console, draftInvoker: SuccessInvoker(regeneratedBody));

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.Equal(regeneratedBody, result.FinalBody);
    }

    [Fact]
    public async Task Regenerate_WithExtraContext_AppendsContextToOriginalText()
    {
        const string originalText = "original operator text";
        const string extraContext = "also cover the legacy code path";
        const string regeneratedBody = "# New Draft\n\nWith extra context.";

        string? capturedOperatorText = null;
        DraftPhaseInvoker capturingInvoker = (text, _) =>
        {
            capturedOperatorText = text;
            return Task.FromResult(new DraftResult(DraftOutcome.Ok, regeneratedBody, null));
        };

        // 'r' to regenerate, type extra context, then 'a' to accept.
        var console = new FakeConsole(
            keyChars: new[] { 'r', 'a' },
            lines: new[] { extraContext });

        var loop = MakeLoop(console, draftInvoker: capturingInvoker);
        var result = await loop.RunAsync("# Draft 1\n\nFirst.", originalText, CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.NotNull(capturedOperatorText);
        Assert.Contains(originalText, capturedOperatorText);
        Assert.Contains(extraContext, capturedOperatorText);
    }

    [Fact]
    public async Task Regenerate_UsesOriginalTextNotCurrentBody()
    {
        const string originalText = "original operator text";
        const string firstRegenBody = "# Regen 1\n\nFirst regen.";
        const string secondRegenBody = "# Regen 2\n\nSecond regen.";

        int callCount = 0;
        string? lastCalledText = null;
        DraftPhaseInvoker countingInvoker = (text, _) =>
        {
            callCount++;
            lastCalledText = text;
            var body = callCount == 1 ? firstRegenBody : secondRegenBody;
            return Task.FromResult(new DraftResult(DraftOutcome.Ok, body, null));
        };

        // 'r' regenerate (no extra context) -> 'r' again -> 'a' accept
        var console = new FakeConsole(
            keyChars: new[] { 'r', 'r', 'a' },
            lines: new[] { "", "" }); // empty extra context both times

        var loop = MakeLoop(console, draftInvoker: countingInvoker);
        var result = await loop.RunAsync("# Draft 1\n\nFirst.", originalText, CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.Equal(2, callCount);
        // Both regenerate calls used the original text, not the previously-regenerated body.
        Assert.Equal(originalText, lastCalledText);
    }

    // ------------------------------------------------------------------
    // Regenerate path - failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task Regenerate_Failure_StaysInLoopWithOriginalBody()
    {
        const string initialBody = "# Draft 1\n\nFirst version.";

        // 'r' regenerate (fails), then 'q' quit.
        var console = new FakeConsole(
            keyChars: new[] { 'r', 'q' },
            lines: new[] { "" }); // empty extra context

        var loop = MakeLoop(console, draftInvoker: FailureInvoker("worker timeout"));

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Aborted, result.Outcome);
        Assert.Contains(console.ErrorWritten, msg => msg.Contains("regenerate failed"));
    }

    // ------------------------------------------------------------------
    // Unrecognized input
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnrecognizedInput_RepromptsAndContinues()
    {
        const string initialBody = "# Draft 1\n\nDescription.";

        // 'x' unrecognized, then 'a' accept.
        var console = new FakeConsole(keyChars: new[] { 'x', 'a' });
        var loop = MakeLoop(console);

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        Assert.Contains(console.ErrorWritten, msg => msg.Contains("unrecognized"));
    }

    // ------------------------------------------------------------------
    // Multi-iteration: edit -> regenerate -> accept
    // ------------------------------------------------------------------

    [Fact]
    public async Task MultiIteration_EditThenRegenerateThenAccept()
    {
        const string initialBody = "# Draft 1\n\nFirst version.";
        const string editedBody = "# Draft 1 (edited)\n\nEdited.";
        const string regeneratedBody = "# Draft 2\n\nRegenerated.";

        // 'e' edit (mock editor writes editedBody), 'r' regenerate (success), 'a' accept.
        var console = new FakeConsole(
            keyChars: new[] { 'e', 'r', 'a' },
            lines: new[] { "" }); // empty extra context for regenerate

        var loop = MakeLoop(
            console,
            draftInvoker: SuccessInvoker(regeneratedBody),
            fileEditorInvoker: MockFileEditorInvoker(editedBody));

        var result = await loop.RunAsync(initialBody, "original text", CancellationToken.None);

        Assert.Equal(ReviewLoopOutcome.Accepted, result.Outcome);
        // After edit -> regenerate -> accept, final body is the regenerated one.
        Assert.Equal(regeneratedBody, result.FinalBody);
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        const string initialBody = "# Draft 1\n\nDescription.";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var console = new FakeConsole(keyChars: new[] { 'a' });
        var loop = MakeLoop(console);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => loop.RunAsync(initialBody, "original text", cts.Token));
    }
}
