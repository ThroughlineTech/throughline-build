namespace ThroughlineBuild.Contracts.Models;

public static class SopBundleCatalog
{
    public const int SchemaVersion = 1;
    public const string EmittedPathClass = "emitted";
    public const string ScaffoldedPathClass = "scaffolded";

    public const string RunBacklogName = "run-backlog";
    public const string CrossImpactName = "cross-impact";

    public const string RunBacklogTicketTransactionResource =
        "ThroughlineBuild.Cli.Sops.RunBacklog.TicketTransaction.md";
    public const string RunBacklogFanOutSchedulingResource =
        "ThroughlineBuild.Cli.Sops.RunBacklog.FanOutScheduling.md";
    public const string RunBacklogClaudeCommandResource =
        "ThroughlineBuild.Cli.Sops.RunBacklog.ClaudeCommand.md";
    public const string RunBacklogCodexSkillResource =
        "ThroughlineBuild.Cli.Sops.RunBacklog.CodexSkill.md";

    public const string CrossImpactProcedureResource =
        "ThroughlineBuild.Cli.Sops.CrossImpact.Procedure.md";
    public const string CrossImpactClaudeCommandResource =
        "ThroughlineBuild.Cli.Sops.CrossImpact.ClaudeCommand.md";
    public const string CrossImpactCodexSkillResource =
        "ThroughlineBuild.Cli.Sops.CrossImpact.CodexSkill.md";

    public static readonly SopCatalogEntry RunBacklog = new(
        RunBacklogName,
        RunBacklogTicketTransactionResource,
        [RunBacklogFanOutSchedulingResource],
        [
            new(
                ".claude/commands/run-backlog.md",
                EmittedPathClass,
                "3c5a332a7504fa59cdd6653fe525a7e9e888bc4b0fe1a42e255f18914d689231",
                RunBacklogClaudeCommandResource),
            new(
                ".agents/skills/run-backlog/SKILL.md",
                EmittedPathClass,
                "ab63e2b4e8ded304152970cb1c3e47cbe97570736946db4f72c5d28453133f77",
                RunBacklogCodexSkillResource),
            new(
                ".build/conductor.toml",
                ScaffoldedPathClass,
                null,
                null),
        ]);

    public static readonly SopCatalogEntry CrossImpact = new(
        CrossImpactName,
        CrossImpactProcedureResource,
        [],
        [
            new(
                ".claude/commands/cross-impact.md",
                EmittedPathClass,
                "48bceacc9b28d3f6f8dcdd7776716344936038473f001b545055ec12be23b86a",
                CrossImpactClaudeCommandResource),
            new(
                ".agents/skills/cross-impact/SKILL.md",
                EmittedPathClass,
                "ba33b78666fcb9cf0745c6f42dde0d48e60d72830ffe8e45908f6620dfa0c764",
                CrossImpactCodexSkillResource),
            new(
                ".build/conductor.toml",
                ScaffoldedPathClass,
                null,
                null),
        ]);

    public static IReadOnlyList<SopCatalogEntry> All { get; } =
    [
        RunBacklog,
        CrossImpact,
    ];

    public static SopCatalogEntry? Find(string name) =>
        All.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
}

public sealed record SopCatalogEntry(
    string Name,
    string ProcedureResourceName,
    IReadOnlyList<string> SupplementalProcedureResourceNames,
    IReadOnlyList<SopOwnedPath> OwnedPaths);

public sealed record SopOwnedPath(
    string Path,
    string Class,
    string? ExpectedContentHash,
    string? ResourceName,
    IReadOnlyList<string>? PreviousContentHashes = null);
