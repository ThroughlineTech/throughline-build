using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

public sealed record CliBootstrapFailure(
    int ExitCode,
    string JsonErrorCode,
    string HumanPrefix,
    string Message,
    Exception Cause);

public sealed record CliBootstrapResult(CliContext? Context, CliBootstrapFailure? Failure)
{
    public static CliBootstrapResult Success(CliContext context) => new(context, null);

    public static CliBootstrapResult Failed(
        int exitCode,
        string jsonErrorCode,
        string humanPrefix,
        Exception cause) =>
        new(null, new CliBootstrapFailure(exitCode, jsonErrorCode, humanPrefix, cause.Message, cause));
}

public static class CliBootstrap
{
    public static Task<CliBootstrapResult> CreateAsync(
        string rawWorkingDirectory,
        CancellationToken cancellationToken = default,
        bool requireTicketing = true,
        BuildConfigLoadMode configLoadMode = BuildConfigLoadMode.Full) =>
        Task.FromResult(Create(rawWorkingDirectory, requireTicketing, configLoadMode));

    private static CliBootstrapResult Create(
        string rawWorkingDirectory,
        bool requireTicketing,
        BuildConfigLoadMode configLoadMode)
    {
        // The clone's machine-local .build lives in the main worktree, so every verb bootstraps
        // against that root whether it was invoked there or inside a linked worktree.
        var layout = RepositoryLayout.Resolve(rawWorkingDirectory);
        var workingDirectory = layout.MainWorktreeRoot;

        string configPath;
        try
        {
            configPath = layout.FindBuildDataFile("config.toml")
                ?? throw new ConfigException(
                    $"config file not found: searched {rawWorkingDirectory} and the repository " +
                    $"rooted at {workingDirectory} for .build/config.toml");
        }
        catch (ConfigException ex)
        {
            return CliBootstrapResult.Failed(2, CliErrorCodes.ConfigError, "Config error", ex);
        }

        BuildConfig config;
        try
        {
            config = BuildConfigLoader.Load(
                configPath,
                branchExists: branch => SetTargetCommand.DefaultBranchValidator(workingDirectory, branch),
                mode: configLoadMode);
        }
        catch (ConfigException ex)
        {
            return CliBootstrapResult.Failed(2, CliErrorCodes.ConfigError, "Config error", ex);
        }

        if (!requireTicketing)
        {
            return CliBootstrapResult.Success(new CliContext(
                rawWorkingDirectory,
                workingDirectory,
                configPath,
                config));
        }

        BuildSecrets secrets;
        try
        {
            secrets = BuildConfigLoader.ResolveSecrets(config, configPath);
        }
        catch (ConfigException ex)
        {
            return CliBootstrapResult.Failed(3, CliErrorCodes.MissingSecret, "Secret error", ex);
        }

        var httpClient = new HttpClient();
        var ticketing = new PlaneTicketingClient(
            httpClient,
            PlaneOptionsFactory.From(config, secrets));

        return CliBootstrapResult.Success(new CliContext(
            rawWorkingDirectory,
            workingDirectory,
            configPath,
            config,
            secrets,
            httpClient,
            ticketing));
    }
}
