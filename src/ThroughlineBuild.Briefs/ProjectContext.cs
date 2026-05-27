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
