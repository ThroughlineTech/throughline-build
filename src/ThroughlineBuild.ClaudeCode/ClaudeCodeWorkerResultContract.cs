namespace ThroughlineBuild.ClaudeCode;

public static class ClaudeCodeWorkerResultContract
{
    public const string Marker = "WORKER_RESULT";

    public const string Text = """
        When the task is complete, end your final response with this machine-readable block.
        Use status "Ok" for success, "NeedsRework" when caller action is required, "Failed" for an execution failure, or "Escalate" for provider/tooling limits.

        WORKER_RESULT
        {"status":"Ok","summary":"one sentence summary","files_changed":[],"failure_reason":null}
        """;

    public static string EnsurePresent(string instruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        if (instruction.Contains(Marker, StringComparison.Ordinal))
            return instruction;

        return instruction.TrimEnd() + Environment.NewLine + Environment.NewLine + Text;
    }
}
