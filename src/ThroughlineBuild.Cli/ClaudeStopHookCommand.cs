using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Cli;

internal static class ClaudeStopHookCommand
{
    internal static bool IsMatch(string[] args) =>
        args.Length >= 2 && args[0] == "internal" && args[1] == "claude-stop-hook";

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string? runDirectory = null;
        string? runId = null;
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {args[i]}.");
                return (int)ClaudeStopHookExitCode.InvalidArguments;
            }

            switch (args[i])
            {
                case "--run-dir": runDirectory = args[i + 1]; break;
                case "--run-id": runId = args[i + 1]; break;
                default:
                    Console.Error.WriteLine($"Unknown claude-stop-hook option: {args[i]}.");
                    return (int)ClaudeStopHookExitCode.InvalidArguments;
            }
        }

        if (runDirectory is null || runId is null)
        {
            Console.Error.WriteLine("claude-stop-hook requires --run-dir and --run-id.");
            return (int)ClaudeStopHookExitCode.InvalidArguments;
        }

        return await ClaudeStopHookBridge.RunAsync(
            runDirectory, runId, Console.In, Console.Out, Console.Error, cancellationToken);
    }
}
