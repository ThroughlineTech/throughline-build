namespace ThroughlineBuild.Cli;

public static class CliUsage
{
    public const string UsageText = """
build - Throughline Build

Usage:
  build plan <ticket-id> [--debug]                        Run the plan phase for a ticket
  build implement <ticket-id> [--debug]                   Run the implement phase for a ticket
  build review <ticket-id> [--debug]                      Run the review phase for a ticket
  build ship <ticket-id> [--debug]                        Ship a reviewed ticket (local fast-forward merge; no push to remote); --debug accepted but is a no-op (ship has no worker subprocess)
  build amend <ticket-id> [--size S|M|L] [--note "..."]   Amend an existing ticket (at least one flag required)
  build close <ticket-id> <reason>                        Close a ticket (reason required)
  build defer <ticket-id> <reason>                        Defer a ticket (reason required)
  build reopen <ticket-id> [reason]                       Reopen a previously closed or deferred ticket (reason optional)
  build --help                                            Show this help

Flags:
  --debug   Stream worker stdout/stderr to the orchestrator console in real time, and capture all worker
            artifacts to .build/sessions/<session-id>/. Stdout lines are prefixed "worker> "; stderr
            lines are prefixed "worker! ". Use 2>&1 | tee log.txt to capture both streams.
            Writes: worker-stdin.txt, worker-stdout.txt, worker-stderr.txt, envelope-result.txt (or parse-error.txt on failure), worker-result.json
            No-op for ship (ship has no worker subprocess).

Exit codes:
  0  Success
  1  Phase or command failure
  2  Config error or unknown verb
  3  Missing secret (env var not set)
  4  Phase infrastructure failure (review verifier crash, ship worktree missing, git unavailable)
""";
}
