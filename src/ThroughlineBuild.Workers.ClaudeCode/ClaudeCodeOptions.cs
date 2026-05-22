namespace ThroughlineBuild.Workers.ClaudeCode;

public class ClaudeCodeOptions
{
    public string ExecutablePath { get; init; } = "claude";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}
