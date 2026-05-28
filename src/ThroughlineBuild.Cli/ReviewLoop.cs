using System.Diagnostics;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Outcome of a ReviewLoop.RunAsync invocation.
/// </summary>
public enum ReviewLoopOutcome
{
    /// <summary>Operator accepted the body; FinalBody is set.</summary>
    Accepted,
    /// <summary>Operator quit without filing; FinalBody is null.</summary>
    Aborted
}

/// <summary>
/// Result returned by ReviewLoop.RunAsync.
/// </summary>
/// <param name="Outcome">Whether the operator accepted or aborted.</param>
/// <param name="FinalBody">The accepted body text; null when Outcome=Aborted.</param>
public sealed record ReviewLoopResult(
    ReviewLoopOutcome Outcome,
    string? FinalBody);

/// <summary>
/// Delegate that re-invokes a draft operation with new operator text.
/// Used to inject a real or fake DraftPhase into ReviewLoop for testability.
/// </summary>
public delegate Task<DraftResult> DraftPhaseInvoker(string operatorText, CancellationToken ct);

/// <summary>
/// Delegate that resolves the editor command to use for the 'e' (edit) path.
/// Returns the editor executable (and optional args as a single string) or null if none found.
/// </summary>
public delegate string? EditorResolver();

/// <summary>
/// Delegate that invokes the editor on a temp file path (already written with currentBody)
/// and returns the new file content after editing completes.
/// Default implementation spawns a process via EditorResolver; tests inject a fake.
/// </summary>
public delegate Task<string> FileEditorInvoker(
    string tempFilePath,
    string currentBody,
    EditorResolver editorResolver,
    IConsole console);

/// <summary>
/// Interactive review loop for the --review flag.
/// Presents the current draft body to the operator and prompts:
///   [a]ccept, [e]dit, [r]egenerate, [q]uit
/// Loops until a terminal choice is made.
/// </summary>
public sealed class ReviewLoop
{
    private readonly IConsole _console;
    private readonly DraftPhaseInvoker _draftInvoker;
    private readonly EditorResolver _editorResolver;
    private readonly FileEditorInvoker _fileEditorInvoker;

    /// <summary>
    /// Construct a ReviewLoop with production defaults for the editor invoker.
    /// </summary>
    /// <param name="console">Console abstraction (use SystemConsole.Instance in production).</param>
    /// <param name="draftInvoker">Delegate that re-runs the drafter with a given operator text.</param>
    /// <param name="editorResolver">Delegate that resolves the editor command; may return null.</param>
    public ReviewLoop(IConsole console, DraftPhaseInvoker draftInvoker, EditorResolver editorResolver)
        : this(console, draftInvoker, editorResolver, DefaultFileEditorInvoker)
    {
    }

    /// <summary>
    /// Construct a ReviewLoop with an explicit editor invoker (for testing).
    /// </summary>
    public ReviewLoop(
        IConsole console,
        DraftPhaseInvoker draftInvoker,
        EditorResolver editorResolver,
        FileEditorInvoker fileEditorInvoker)
    {
        _console = console;
        _draftInvoker = draftInvoker;
        _editorResolver = editorResolver;
        _fileEditorInvoker = fileEditorInvoker;
    }

    /// <summary>
    /// Run the interactive review loop.
    /// </summary>
    /// <param name="initialBody">Draft body text to start the loop with.</param>
    /// <param name="originalText">Original operator text; used as base for regenerate.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ReviewLoopResult> RunAsync(
        string initialBody,
        string originalText,
        CancellationToken ct)
    {
        var currentBody = initialBody;
        string? tempFile = null;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Print current body.
                _console.WriteLine(string.Empty);
                _console.WriteLine("--- draft body ---");
                _console.WriteLine(currentBody);
                _console.WriteLine("------------------");
                _console.Write("[a]ccept, [e]dit, [r]egenerate, [q]uit: ");

                var ch = _console.ReadKeyChar();
                _console.WriteLine(string.Empty); // newline after keystroke echo

                switch (ch)
                {
                    case 'a':
                        return new ReviewLoopResult(ReviewLoopOutcome.Accepted, currentBody);

                    case 'e':
                        // Allocate temp file once; reuse across edit iterations.
                        if (tempFile is null)
                            tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".md");
                        File.WriteAllText(tempFile, currentBody);
                        currentBody = await _fileEditorInvoker(tempFile, currentBody, _editorResolver, _console);
                        break;

                    case 'r':
                        _console.Write("any extra context for the regenerate? [enter to skip]: ");
                        var extraContext = _console.ReadLine() ?? string.Empty;
                        var regenerateText = string.IsNullOrWhiteSpace(extraContext)
                            ? originalText
                            : originalText + "\n" + extraContext.Trim();

                        var regenResult = await _draftInvoker(regenerateText, ct);
                        if (regenResult.Outcome != DraftOutcome.Ok)
                        {
                            _console.ErrorWriteLine($"regenerate failed: {regenResult.FailureReason}");
                            _console.ErrorWriteLine("keeping current body - choose [e]dit or [q]uit to proceed");
                        }
                        else
                        {
                            currentBody = regenResult.BodyMarkdown!;
                        }
                        break;

                    case 'q':
                        return new ReviewLoopResult(ReviewLoopOutcome.Aborted, null);

                    default:
                        _console.ErrorWriteLine("unrecognized choice - enter a, e, r, or q");
                        break;
                }
            }
        }
        finally
        {
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Default file-editor invoker (production)
    // ------------------------------------------------------------------

    private static async Task<string> DefaultFileEditorInvoker(
        string tempFilePath,
        string currentBody,
        EditorResolver editorResolver,
        IConsole console)
    {
        var editor = editorResolver();
        if (editor is null)
        {
            console.ErrorWriteLine("no editor found: set $EDITOR or install vim/nano/code/notepad");
            console.ErrorWriteLine("keeping current body");
            return currentBody;
        }

        var (executable, editorArgs) = SplitEditorCommand(editor, tempFilePath);

        var psi = new ProcessStartInfo(executable, editorArgs)
        {
            UseShellExecute = false
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                console.ErrorWriteLine($"failed to start editor '{executable}'");
                return currentBody;
            }
            await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            console.ErrorWriteLine($"editor error: {ex.Message}");
            return currentBody;
        }
        finally
        {
            proc?.Dispose();
        }

        // Re-read the file regardless of editor exit code (operator-saved content is the authority).
        try
        {
            return File.ReadAllText(tempFilePath);
        }
        catch (Exception ex)
        {
            console.ErrorWriteLine($"failed to read temp file after edit: {ex.Message}");
            return currentBody;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Split "editor [optional-flags]" into (executable, "flags + filepath").
    /// The file path is always appended as the last argument.
    /// </summary>
    private static (string executable, string arguments) SplitEditorCommand(string editor, string filePath)
    {
        var trimmed = editor.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
            return (trimmed, QuoteIfNeeded(filePath));

        var exe = trimmed[..firstSpace];
        var rest = trimmed[(firstSpace + 1)..].Trim();
        var args = string.IsNullOrEmpty(rest)
            ? QuoteIfNeeded(filePath)
            : rest + " " + QuoteIfNeeded(filePath);
        return (exe, args);
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ') ? "\"" + path + "\"" : path;

    // ------------------------------------------------------------------
    // Static factory: default editor resolver
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the default editor resolver: $EDITOR env var first, then platform fallback chain.
    /// </summary>
    public static EditorResolver DefaultEditorResolver() => () =>
    {
        var env = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        // Fallback chain: vim, nano, code --wait; on Windows also notepad.exe.
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "vim", "nano", "code --wait", "notepad.exe" }
            : new[] { "vim", "nano", "code --wait" };

        foreach (var candidate in candidates)
        {
            var exe = candidate.Contains(' ') ? candidate[..candidate.IndexOf(' ')] : candidate;
            if (IsOnPath(exe))
                return candidate;
        }

        return null;
    };

    private static bool IsOnPath(string executable)
    {
        try
        {
            // Use 'where' on Windows, 'which' on Unix.
            var whichCmd = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo(whichCmd, executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
