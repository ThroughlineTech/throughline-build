namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Options controlling a ScaffoldPhase.RunAsync invocation.
/// </summary>
/// <param name="OpDocPath">Absolute or relative path to the op-doc markdown file.</param>
/// <param name="DryRun">When true, parse and validate but do not make any Plane API calls.</param>
/// <param name="AcceptWarnings">When true, proceed even if validation warnings are present.</param>
public record ScaffoldOptions(
    string OpDocPath,
    bool DryRun,
    bool AcceptWarnings);
