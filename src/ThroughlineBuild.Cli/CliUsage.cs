namespace ThroughlineBuild.Cli;

public static class CliUsage
{
    public const string UsageText = """
build - Throughline Build

Usage:
  build plan <ticket-id>                                  Run the plan phase for a ticket
  build implement <ticket-id>                             Run the implement phase for a ticket
  build review <ticket-id>                                Run the review phase for a ticket
  build amend <ticket-id> [--size S|M|L] [--note "..."]   Amend an existing ticket (at least one flag required)
  build close <ticket-id> <reason>                        Close a ticket (reason required)
  build defer <ticket-id> <reason>                        Defer a ticket (reason required)
  build reopen <ticket-id> [reason]                       Reopen a previously closed or deferred ticket (reason optional)
  build --help                                            Show this help

Exit codes:
  0  Success
  1  Phase or command failure
  2  Config error or unknown verb
  3  Missing secret (env var not set)
  4  Review phase failure (non-verdict)
""";
}
