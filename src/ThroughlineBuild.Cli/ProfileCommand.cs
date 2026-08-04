using System.Text;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Git;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Cli;

public enum ProfileAction
{
    Derive,
    Apply,
    Prompt
}

public sealed record ProfileInvocation(ProfileAction Action, bool Force, string? InputPath);

/// <summary>
/// Implements the standalone profile surface. Apply is intentionally worker-free so an agent
/// session or CI process can deterministically persist JSON produced elsewhere.
/// </summary>
public static class ProfileCommand
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out ProfileInvocation? invocation,
        out string? error)
    {
        invocation = null;
        error = null;

        if (args.Count < 2)
        {
            error = "profile subcommand is required";
            return false;
        }

        var action = args[1] switch
        {
            "derive" => ProfileAction.Derive,
            "apply" => ProfileAction.Apply,
            "prompt" => ProfileAction.Prompt,
            _ => (ProfileAction?)null,
        };
        if (action is null)
        {
            error = $"unknown profile subcommand: {args[1]}";
            return false;
        }

        if (action == ProfileAction.Prompt)
        {
            if (args.Count != 2)
            {
                error = "profile prompt accepts no arguments";
                return false;
            }

            invocation = new ProfileInvocation(ProfileAction.Prompt, false, null);
            return true;
        }

        bool force = false;
        var positional = new List<string>();
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--force")
            {
                if (force)
                {
                    error = "--force may be specified only once";
                    return false;
                }

                force = true;
                continue;
            }

            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unknown option: {args[i]}";
                return false;
            }

            positional.Add(args[i]);
        }

        if (action == ProfileAction.Derive)
        {
            if (positional.Count != 0)
            {
                error = "profile derive accepts no positional arguments";
                return false;
            }

            invocation = new ProfileInvocation(ProfileAction.Derive, force, null);
            return true;
        }

        if (positional.Count != 1)
        {
            error = "profile apply requires exactly one JSON input path or '-'";
            return false;
        }

        invocation = new ProfileInvocation(ProfileAction.Apply, force, positional[0]);
        return true;
    }

    public static int WriteUsage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build profile derive [--force] [--json]");
            error.WriteLine("       build profile apply <file|-> [--force] [--json]");
            error.WriteLine("       build profile prompt [--json]");
        }
        return 2;
    }

    public static int ExecutePrompt(bool json, TextWriter output)
    {
        var prompt = ProfilePromptLoader.LoadRepository();
        if (json)
            CliEnvelopeWriter.WriteProfilePrompt(output, new ProfilePromptView(prompt));
        else
            output.Write(prompt);
        return 0;
    }

    public static async Task<int> ExecuteApplyAsync(
        ProfileInvocation invocation,
        bool json,
        string configPath,
        string inputDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        var profileJson = await ReadInputAsync(invocation.InputPath!, inputDirectory, error, json, output, ct)
            .ConfigureAwait(false);
        if (profileJson is null)
            return 1;

        if (!ProjectProfileParser.TryParse(profileJson, out var profile, out var parseError) || profile is null)
            return Failure(json, output, error, CliErrorCodes.Usage, $"PROJECT_PROFILE JSON was invalid: {parseError}", 2);

        string configText;
        try
        {
            configText = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(json, output, error, CliErrorCodes.ConfigError,
                $"could not read configuration '{configPath}': {ex.Message}", 2);
        }

        var outcome = ConfigProfileWriter.Apply(configText, profile, invocation.Force);
        if (outcome.Changed && outcome.NewText is not null)
        {
            try
            {
                await File.WriteAllTextAsync(configPath, outcome.NewText, new UTF8Encoding(false), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(json, output, error, CliErrorCodes.Failure,
                    $"could not write configuration '{configPath}': {ex.Message}", 1);
            }
        }

        var message = outcome.Changed
            ? outcome.Summary ?? "wrote profile"
            : outcome.SkipReason ?? "config already matched the supplied profile";
        WriteOperation(json, output, "apply", configPath, outcome.Changed, message, canaryVerified: null);
        return 0;
    }

    public static async Task<int> ExecuteDeriveAsync(
        ProfileInvocation invocation,
        bool json,
        string configPath,
        string workingDirectory,
        WorkersConfig workers,
        Func<string, AgentConfig, IWorkerAgent> workerAgentBuilder,
        ProfileGateVerifier verifier,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        string configText;
        try
        {
            configText = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(json, output, error, CliErrorCodes.ConfigError,
                $"could not read configuration '{configPath}': {ex.Message}", 2);
        }

        var skipReason = ConfigProfileWriter.AlreadyConfiguredSkipReason(configText, invocation.Force);
        if (skipReason is not null)
        {
            WriteOperation(json, output, "derive", configPath, false, skipReason, canaryVerified: null);
            return 0;
        }

        if (!workers.Agents.TryGetValue(workers.DefaultAgent, out var agentConfig))
        {
            return Failure(json, output, error, CliErrorCodes.ConfigError,
                $"missing [workers.{workers.DefaultAgent}] sub-table in config", 2);
        }

        var preflight = await ClaudeTransportPreflight.GateAsync(
            workers, [workers.DefaultAgent], error, ct).ConfigureAwait(false);
        if (preflight != 0)
        {
            return Failure(json, output, error, CliErrorCodes.ConfigError,
                "configured worker transport is unsupported on this host", preflight);
        }

        ProfileDerivationResult derivation;
        try
        {
            var worker = workerAgentBuilder(workers.DefaultAgent, agentConfig);
            var deriver = new ScaffoldProfileDeriver(worker);
            var timeout = TimeSpan.FromMinutes(Math.Clamp(workers.TimeoutMinutes, 1, 30));
            derivation = await deriver.DeriveRepositoryAsync(workingDirectory, timeout, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"profile derivation threw: {ex.Message}", 1);
        }

        if (!derivation.Success || derivation.Profile is null)
        {
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"profile derivation failed: {derivation.FailureReason}", 1);
        }

        var verification = await verifier.VerifyAsync(
            derivation.Profile,
            workingDirectory,
            new ProcessGitClient(workingDirectory),
            new AutomatedChecksRunner(),
            ct).ConfigureAwait(false);
        if (!verification.Success)
        {
            return Failure(json, output, error, CliErrorCodes.Failure,
                $"profile derivation failed gate verification: {verification.FailureReason}", 1);
        }

        var outcome = ConfigProfileWriter.Apply(configText, derivation.Profile, invocation.Force);
        if (outcome.Changed && outcome.NewText is not null)
        {
            try
            {
                await File.WriteAllTextAsync(configPath, outcome.NewText, new UTF8Encoding(false), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(json, output, error, CliErrorCodes.Failure,
                    $"derived a profile but could not write configuration '{configPath}': {ex.Message}", 1);
            }
        }

        var message = outcome.Changed
            ? outcome.Summary ?? "wrote derived profile"
            : outcome.SkipReason ?? "config already matched the derived profile";
        WriteOperation(json, output, "derive", configPath, outcome.Changed, message, canaryVerified: true);
        return 0;
    }

    private static async Task<string?> ReadInputAsync(
        string inputPath,
        string inputDirectory,
        TextWriter error,
        bool json,
        TextWriter output,
        CancellationToken ct)
    {
        try
        {
            if (inputPath == "-")
                return await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);

            var path = Path.GetFullPath(inputPath, inputDirectory);
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure(json, output, error, CliErrorCodes.Failure,
                $"could not read PROJECT_PROFILE input '{inputPath}': {ex.Message}", 1);
            return null;
        }
    }

    private static void WriteOperation(
        bool json,
        TextWriter output,
        string operation,
        string configPath,
        bool changed,
        string message,
        bool? canaryVerified)
    {
        if (json)
        {
            CliEnvelopeWriter.WriteProfileOperation(output, new ProfileOperationView(
                operation, Path.GetFullPath(configPath), changed, message, canaryVerified));
            return;
        }

        output.WriteLine($"profile {operation}: {message}");
    }

    private static int Failure(
        bool json,
        TextWriter output,
        TextWriter error,
        string code,
        string message,
        int exitCode)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, code, message);
        else
            error.WriteLine($"Error: {message}");
        return exitCode;
    }
}
