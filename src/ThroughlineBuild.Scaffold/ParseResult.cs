namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Result of parsing an op-doc markdown file.
/// Parsed is populated when the document has at least an H1 operation header.
/// Errors contains all structural parse problems found during the parse pass.
/// A non-null Parsed with a non-empty Errors list means a best-effort result was produced.
/// </summary>
public sealed record ParseResult(
    OpDoc? Parsed,
    IReadOnlyList<OpDocParseError> Errors);
