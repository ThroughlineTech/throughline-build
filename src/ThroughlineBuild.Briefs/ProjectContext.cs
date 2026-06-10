namespace ThroughlineBuild.Briefs;

public record ProjectContext(
    string Language,
    string Framework,
    string PackageManager,
    string BuildCommand,
    string TestCommand,
    string InstallCommand,
    string DevCommand,
    string PlaneProjectUrl,
    string Notes,
    string WorkflowTool)
{
    /// <summary>
    /// Stable, project-wide convention files (test harness/config + one canonical test example) that
    /// the deriver chose; inlined into every implement brief by <c>PreloadedContextBuilder</c>. Paths
    /// are relative to the target worktree root. Empty when none derived. Stack-agnostic: just paths -
    /// the deriver picks them per stack, the engine reads whatever they are.
    /// </summary>
    public IReadOnlyList<string> ConventionFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true (the default), the implement phase pre-loads named-input + convention file contents
    /// into the brief. Set <c>[project].preload_context = false</c> to restore the pre-preload brief
    /// (the ablation knob).
    /// </summary>
    public bool PreloadContext { get; init; } = true;

    /// <summary>
    /// When true, S-effort implement briefs get lightweight-planning hygiene (a prompt line plus a
    /// restricted tool set). Default false: an opt-in, unproven lever - off leaves the implement brief
    /// and argv byte-identical to today (the ablation control). Set <c>[project].context_hygiene = true</c>
    /// to enable. Gated to S-effort briefs only in the phase; never M or L.
    /// </summary>
    public bool ContextHygiene { get; init; } = false;

    public static ProjectContext Empty { get; } = new(
        Language: string.Empty,
        Framework: string.Empty,
        PackageManager: string.Empty,
        BuildCommand: string.Empty,
        TestCommand: string.Empty,
        InstallCommand: string.Empty,
        DevCommand: string.Empty,
        PlaneProjectUrl: string.Empty,
        Notes: string.Empty,
        WorkflowTool: "build");
}
