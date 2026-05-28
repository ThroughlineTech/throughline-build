namespace ThroughlineBuild.Cli;

/// <summary>
/// Kind of classification returned by NewVerbArgumentClassifier.Classify.
/// </summary>
public enum NewVerbKind
{
    /// <summary>--print-template flag present; print template to stdout and exit 0.</summary>
    PrintTemplate,
    /// <summary>First positional arg is an existing file path; use existing file-mode flow.</summary>
    FileMode,
    /// <summary>Input is free-form text (single or joined multi-arg); run DraftPhase.</summary>
    DraftMode,
    /// <summary>Arg is "-"; read stdin to EOF, then run DraftPhase.</summary>
    StdinDraftMode,
    /// <summary>No positional args and no --print-template; print help and exit non-zero.</summary>
    HelpExitNonZero
}

/// <summary>
/// Result of NewVerbArgumentClassifier.Classify.
/// </summary>
/// <param name="Kind">The classification kind.</param>
/// <param name="FilePath">Set when Kind == FileMode; the file path to use as body-path.</param>
/// <param name="DraftText">Set when Kind == DraftMode; the free-form operator text.</param>
/// <param name="LooksLikePathButMissing">
/// True when Kind == DraftMode and the single input argument resembles a file path
/// (contains path separators or ends in .md/.txt) but File.Exists returned false.
/// Triggers a stderr notice + brief pause before proceeding.
/// </param>
public sealed record NewVerbClassification(
    NewVerbKind Kind,
    string? FilePath = null,
    string? DraftText = null,
    bool LooksLikePathButMissing = false);

/// <summary>
/// Pure disambiguation helper for the "build new" verb.
/// All IO is injected (fileExists) so every branch is unit-testable without subprocess infrastructure.
/// </summary>
public static class NewVerbArgumentClassifier
{
    // Known body-file extensions that suggest a path was intended.
    private static readonly string[] PathLikeExtensions = { ".md", ".txt" };

    /// <summary>
    /// Classifies the raw args array (after --debug/--quiet/--summary-json have been stripped)
    /// into one of the NewVerbKind cases.
    /// </summary>
    /// <param name="args">
    /// Full args array starting with "new" at index 0.
    /// Flags like --title, --type, --label are ignored for disambiguation purposes;
    /// they are collected later by the caller.
    /// </param>
    /// <param name="fileExists">
    /// Predicate used to check whether a path exists as a regular file.
    /// Defaults to File.Exists when null.
    /// </param>
    public static NewVerbClassification Classify(string[] args, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;

        // args[0] == "new" - collect positional (non-flag) args after that.
        var positional = new List<string>();
        bool hasPrintTemplate = false;

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--print-template")
            {
                hasPrintTemplate = true;
                // not positional
            }
            else if (a == "--review")
            {
                // Known bare bool flag; does not consume the next arg.
            }
            else if (a.StartsWith("--") && i + 1 < args.Length)
            {
                // Known value flags: skip both flag and value.
                // We skip --title, --type, --label so their values don't become draft text.
                i++; // skip value
            }
            else if (a.StartsWith("--"))
            {
                // Unknown bare bool flag; skip.
            }
            else
            {
                positional.Add(a);
            }
        }

        if (hasPrintTemplate)
            return new NewVerbClassification(NewVerbKind.PrintTemplate);

        if (positional.Count == 0)
            return new NewVerbClassification(NewVerbKind.HelpExitNonZero);

        // Single "-" => stdin draft mode.
        if (positional.Count == 1 && positional[0] == "-")
            return new NewVerbClassification(NewVerbKind.StdinDraftMode);

        // Single arg: test for file existence first.
        if (positional.Count == 1)
        {
            var arg = positional[0];
            if (fileExists(arg))
                return new NewVerbClassification(NewVerbKind.FileMode, FilePath: arg);

            // Check if it looks like a path that was intended as a file.
            bool looksLikePath = LooksLikePath(arg);
            return new NewVerbClassification(
                NewVerbKind.DraftMode,
                DraftText: arg,
                LooksLikePathButMissing: looksLikePath);
        }

        // Multiple positional args: join as draft text.
        var joined = string.Join(" ", positional);
        return new NewVerbClassification(NewVerbKind.DraftMode, DraftText: joined);
    }

    /// <summary>
    /// Returns true if the argument looks like it was intended as a file path:
    /// contains a path separator or ends with a known body-file extension.
    /// </summary>
    private static bool LooksLikePath(string arg)
    {
        if (arg.Contains('/') || arg.Contains('\\'))
            return true;
        foreach (var ext in PathLikeExtensions)
        {
            if (arg.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
