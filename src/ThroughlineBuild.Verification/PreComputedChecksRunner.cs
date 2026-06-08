using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Verification;

// Returns a fixed pre-computed set of check results without running any process.
// Used by ChainPhase to forward gate-phase results to ReviewPhase so checks execute
// exactly once per ticket (one build per ticket, the wall-discipline target).
public sealed class PreComputedChecksRunner : AutomatedChecksRunner
{
    private readonly IReadOnlyList<CheckResult> _results;

    public PreComputedChecksRunner(IReadOnlyList<CheckResult> results) : base()
        => _results = results;

    public override Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct)
        => Task.FromResult(_results);
}
