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
                "2b06223d83b7b737b6e5eba672d9d77dcdab21b85d5a9c2a54afcbee2de4aae0",
                RunBacklogClaudeCommandResource,
                ["3c5a332a7504fa59cdd6653fe525a7e9e888bc4b0fe1a42e255f18914d689231"]),
            new(
                ".agents/skills/run-backlog/SKILL.md",
                EmittedPathClass,
                "056ae4f2bae514cdf8628c2b413793e2fefd8ae86b3b55b0968b247387110aec",
                RunBacklogCodexSkillResource,
                ["ab63e2b4e8ded304152970cb1c3e47cbe97570736946db4f72c5d28453133f77"]),
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
                "9fddd7bc4581ea7d758a50585f6b3811a408f222bff6c34f57854b151bb6f2d7",
                CrossImpactClaudeCommandResource,
                ["48bceacc9b28d3f6f8dcdd7776716344936038473f001b545055ec12be23b86a"]),
            new(
                ".agents/skills/cross-impact/SKILL.md",
                EmittedPathClass,
                "375812fb4892684f70a2d5c86452e4ad2219f3f7e9a8e3853ac53b54da6d54e3",
                CrossImpactCodexSkillResource,
                ["ba33b78666fcb9cf0745c6f42dde0d48e60d72830ffe8e45908f6620dfa0c764"]),
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
