using ThroughlineBuild.EventLog;
using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

public sealed class CliContext : IDisposable
{
    private readonly HttpClient? _httpClient;
    private readonly PlaneTicketingClient? _ticketing;
    private readonly BuildSecrets? _secrets;

    public CliContext(
        string rawWorkingDirectory,
        string workingDirectory,
        string configPath,
        BuildConfig config,
        BuildSecrets? secrets = null,
        HttpClient? httpClient = null,
        PlaneTicketingClient? ticketing = null)
    {
        RawWorkingDirectory = rawWorkingDirectory;
        WorkingDirectory = workingDirectory;
        ConfigPath = configPath;
        Config = config;
        _secrets = secrets;
        _httpClient = httpClient;
        _ticketing = ticketing;
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
    public BuildSecrets Secrets =>
        _secrets ?? throw new InvalidOperationException("ticketing bootstrap was not requested");
    public HttpClient HttpClient =>
        _httpClient ?? throw new InvalidOperationException("ticketing bootstrap was not requested");
    public PlaneTicketingClient Ticketing =>
        _ticketing ?? throw new InvalidOperationException("ticketing bootstrap was not requested");
    public SessionContext SessionContext { get; }

    public string ResolveLogDirectory(string raw) =>
        Path.GetFullPath(BuildConfigLoader.ResolveLogDirectory(ConfigPath, raw, WorkingDirectory));

    public void Dispose() => _httpClient?.Dispose();
}
