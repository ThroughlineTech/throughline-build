using ThroughlineBuild.EventLog;
using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

public sealed class CliContext : IDisposable
{
    public CliContext(
        string rawWorkingDirectory,
        string workingDirectory,
        string configPath,
        BuildConfig config,
        BuildSecrets secrets,
        HttpClient httpClient,
        PlaneTicketingClient ticketing)
    {
        RawWorkingDirectory = rawWorkingDirectory;
        WorkingDirectory = workingDirectory;
        ConfigPath = configPath;
        Config = config;
        Secrets = secrets;
        HttpClient = httpClient;
        Ticketing = ticketing;
        SessionContext = new SessionContext(
            ProjectId: config.Ticketing.PlaneProjectId,
            ProjectName: string.IsNullOrEmpty(config.Ticketing.PlaneProjectName)
                ? null
                : config.Ticketing.PlaneProjectName,
            WorkspaceSlug: config.Ticketing.PlaneWorkspaceSlug,
            BuildVersion: BuildVersion.Current);
    }

    public string RawWorkingDirectory { get; }
    public string WorkingDirectory { get; }
    public string ConfigPath { get; }
    public BuildConfig Config { get; }
    public BuildSecrets Secrets { get; }
    public HttpClient HttpClient { get; }
    public PlaneTicketingClient Ticketing { get; }
    public SessionContext SessionContext { get; }

    public string ResolveLogDirectory(string raw) =>
        Path.GetFullPath(BuildConfigLoader.ResolveLogDirectory(ConfigPath, raw, WorkingDirectory));

    public void Dispose() => HttpClient.Dispose();
}
