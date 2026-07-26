using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Scaffold;

// op-doc format version: strict, 2026-05-27 (see docs/op-docs/op-doc-spec.md)

public record OpDoc(
    string OperationSlug,
    string Title,
    string Why,
    IReadOnlyList<DispatchEntry> DispatchOrder,
    IReadOnlyList<Plan> Plans,
    string WhatDoneLooksLike);

public record DispatchEntry(
    string PlanId,
    string Name,
    string? DependsOn,
    string Effort);

public record Plan(
    string Id,
    string Name,
    string Goal,
    IReadOnlyList<Brief> Briefs);

public record Brief(
    string Slug,
    int Number,
    string Title,
    string Goal,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> AcceptanceCriteria,
    string? Notes,
    IReadOnlyList<string> OutOfScope,
    string? DependsOn)
{
    // Positive-only file paths the brief declares for context pre-loading (the `Preload:` op-doc
    // label). Author-declared DATA - just paths, never scraped from prose and never language-branched.
    // Empty unless the brief carries a Preload block. Init-only property (not a positional param) so the
    // existing new Brief(...) sites do not churn; set only where the data exists. See experiment-3 plan.
    public IReadOnlyList<string> PreloadFiles { get; init; } = Array.Empty<string>();
}

public record OpDocParseError(
    int LineNumber,
    string Section,
    string Message,
    string? SourceFile = null,
    string? SourceMember = null,
    int SourceLineNumber = 0)
{
    public static OpDocParseError Create(
        int lineNumber,
        string section,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string sourceMember = "",
        [CallerLineNumber] int sourceLineNumber = 0) =>
        new(lineNumber, section, message, sourceFile, sourceMember, sourceLineNumber);
}
