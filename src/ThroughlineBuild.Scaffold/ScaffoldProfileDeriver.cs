using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using WorkerBrief = ThroughlineBuild.Contracts.Models.Brief;

namespace ThroughlineBuild.Scaffold;

public sealed record ProfileDerivationResult(bool Success, ProjectProfile? Profile, string? FailureReason)
{
    public static ProfileDerivationResult Ok(ProjectProfile profile) => new(true, profile, null);
    public static ProfileDerivationResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Asks a worker agent to read an op-doc and emit the project's toolchain profile (language,
/// commands, and the automated checks the review/ship phases must run). The agent is the right tool
/// here: the toolchain lives in the op-doc as prose (Brief 01 acceptance, "What done looks like"),
/// not as a structured field, so prose -> structured config is exactly an LLM job.
///
/// Output contract (shared envelope - see WorkerResultParser): the agent emits the profile as a
/// single fenced block named PROJECT_PROFILE, then a WORKER_RESULT envelope. We read the block
/// straight off <see cref="WorkerResult.Blocks"/> and parse it deterministically.
/// </summary>
public sealed class ScaffoldProfileDeriver
{
    private const string ProfileBlockName = "PROJECT_PROFILE";

    private static readonly IReadOnlyList<string> ReadOnlyTools = new[] { "Read", "Grep", "Glob" };

    private readonly IWorkerAgent _worker;

    public ScaffoldProfileDeriver(IWorkerAgent worker)
    {
        _worker = worker;
    }

    public async Task<ProfileDerivationResult> DeriveAsync(
        string opDocMarkdown,
        string workingDirectory,
        TimeSpan timeout,
        string? debugCaptureDirectory,
        CancellationToken ct)
    {
        var brief = BuildBrief(opDocMarkdown);
        var options = new WorkerOptions(timeout, AllowedTools: ReadOnlyTools,
            DebugCaptureDirectory: debugCaptureDirectory, Size: WorkerSize.Small);

        WorkerResult result;
        try
        {
            result = await _worker.ExecuteAsync(brief, workingDirectory, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ProfileDerivationResult.Fail($"worker invocation threw: {ex.Message}");
        }

        if (result.Status != Status.Ok)
        {
            var reason = result.FailureReason ?? result.Status.ToString();
            return ProfileDerivationResult.Fail($"worker did not complete cleanly: {reason}");
        }

        if (result.Blocks is null || !result.Blocks.TryGetValue(ProfileBlockName, out var json)
            || string.IsNullOrWhiteSpace(json))
        {
            return ProfileDerivationResult.Fail(
                $"worker did not emit a <<<{ProfileBlockName}_START fenced block containing the profile JSON");
        }

        if (!ProjectProfileParser.TryParse(json, out var profile, out var parseError) || profile is null)
        {
            return ProfileDerivationResult.Fail($"profile JSON was invalid: {parseError}");
        }

        return ProfileDerivationResult.Ok(profile);
    }

    private static WorkerBrief BuildBrief(string opDocMarkdown)
    {
        var instruction = ProfilePromptLoader.Load().Replace("{{op_doc_markdown}}", opDocMarkdown);

        return new WorkerBrief(
            TicketId: "scaffold-profile",
            Phase: Phase.Scaffold,
            Instruction: instruction,
            RelevantFiles: Array.Empty<string>(),
            AllowedWrites: Array.Empty<string>(),
            Context: new Dictionary<string, string>());
    }
}
