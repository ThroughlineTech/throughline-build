using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
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
    public static async Task<CliBootstrapResult> CreateAsync(
        string rawWorkingDirectory,
        CancellationToken cancellationToken = default,
        bool requireTicketing = true,
        BuildConfigLoadMode configLoadMode = BuildConfigLoadMode.Full)
    {
        var resolverGit = new ProcessGitClient(rawWorkingDirectory);
        var workingDirectory = await MainWorktreeResolver.ResolveAsync(
            resolverGit,
            rawWorkingDirectory,
            cancellationToken).ConfigureAwait(false);

        string configPath;
        try
        {
            configPath = BuildConfigLoader.FindConfigFile(workingDirectory)
                ?? throw new ConfigException(
                    $"config file not found: searched from {workingDirectory} upwards for .build/config.toml");
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
            secrets = BuildConfigLoader.ResolveSecrets(config);
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
