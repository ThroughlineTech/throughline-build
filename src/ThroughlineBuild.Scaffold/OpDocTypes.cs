using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Scaffold;

// op-doc format version: strict, 2026-05-27 (see docs/op-docs/op-12-build-scaffold.md)

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
    string? DependsOn);

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
