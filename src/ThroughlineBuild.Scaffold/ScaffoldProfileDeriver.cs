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
        CancellationToken ct)
    {
        var brief = BuildBrief(opDocMarkdown);
        var options = new WorkerOptions(timeout, AllowedTools: ReadOnlyTools, Size: WorkerSize.Small);

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
        var instruction =
$@"# Derive the project toolchain profile

You are configuring an automated build pipeline for a brand-new repository. The operation op-doc
below describes what will be built. Your job is to determine the project's toolchain from it and
emit a machine-readable profile.

The op-doc states the toolchain in prose - look especially at the scaffolding brief (usually Brief
01), its Inputs/Outputs/Acceptance criteria, and the ""What done looks like"" section. From those,
determine:

- language (e.g. ""typescript"", ""python"", ""csharp"", ""go"")
- framework / stack (e.g. ""react-vite"", ""django"", ""dotnet"")
- package_manager (e.g. ""npm"", ""pnpm"", ""pip"", ""uv"", ""dotnet"")
- install_command, build_command, test_command, dev_command (the exact shell commands the op-doc
  expects, e.g. ""npm install"", ""npm run build"", ""npm test"", ""npm run dev"")
- review_checks: the automated checks the reviewer should run after each implementation. Normally a
  build check and a test check, expressed as a discrete executable plus an argument array.
- regression_checks: the checks to run before shipping (usually the same as review_checks).

Rules for checks:
- ""executable"" is the BARE tool name, never a shell string and never an OS-specific variant. Use
  ""npm"", not ""npm.cmd"" and not ""npm run build"". The pipeline resolves the OS-specific binary itself.
- ""arguments"" is the argument array, e.g. for ""npm run build"" -> executable ""npm"", arguments
  [""run"", ""build""]. For ""npm test"" -> executable ""npm"", arguments [""test""].
- ""timeout_minutes"" is a sensible per-check ceiling (build ~5, test ~10).
- Do not invent a check the op-doc does not support. If the op-doc only specifies a build and a
  test command, emit exactly those two checks.

## Output

First emit the profile as a single fenced block named PROJECT_PROFILE containing ONLY a JSON object:

<<<PROJECT_PROFILE_START
{{
  ""language"": ""typescript"",
  ""framework"": ""react-vite"",
  ""package_manager"": ""npm"",
  ""install_command"": ""npm install"",
  ""build_command"": ""npm run build"",
  ""test_command"": ""npm test"",
  ""dev_command"": ""npm run dev"",
  ""review_checks"": [
    {{ ""name"": ""build"", ""executable"": ""npm"", ""arguments"": [""run"", ""build""], ""timeout_minutes"": 5 }},
    {{ ""name"": ""test"", ""executable"": ""npm"", ""arguments"": [""test""], ""timeout_minutes"": 10 }}
  ],
  ""regression_checks"": [
    {{ ""name"": ""build"", ""executable"": ""npm"", ""arguments"": [""run"", ""build""], ""timeout_minutes"": 5 }},
    {{ ""name"": ""test"", ""executable"": ""npm"", ""arguments"": [""test""], ""timeout_minutes"": 10 }}
  ]
}}
<<<PROJECT_PROFILE_END

(The block above is an EXAMPLE of the shape; fill it with values derived from THIS op-doc.)

Then emit exactly one WORKER_RESULT envelope:

WORKER_RESULT
{{""status"":""Ok"",""summary"":""Derived project toolchain profile"",""files_changed"":[],""failure_reason"":null,""metadata"":{{}}}}

## Operation op-doc

{opDocMarkdown}
";

        return new WorkerBrief(
            TicketId: "scaffold-profile",
            Phase: Phase.Scaffold,
            Instruction: instruction,
            RelevantFiles: Array.Empty<string>(),
            AllowedWrites: Array.Empty<string>(),
            Context: new Dictionary<string, string>());
    }
}
