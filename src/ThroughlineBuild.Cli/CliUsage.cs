namespace ThroughlineBuild.Cli;

public static class CliUsage
{
    public const string UsageText = """
build - Throughline Build

Usage:
  build plan <ticket-id> [--debug|--quiet] [--summary-json]       Run the plan phase for a ticket
  build implement <ticket-id> [--debug|--quiet] [--summary-json]  Run the implement phase for a ticket
  build review <ticket-id> [--debug|--quiet] [--summary-json]     Run the review phase for a ticket
  build ship <ticket-id> [--debug] [--summary-json]               Ship a reviewed ticket (local fast-forward merge; no push to remote); --debug accepted but is a no-op (ship has no worker subprocess)
  build amend <ticket-id> [--size S|M|L] [--note "..."]           Amend an existing ticket (at least one flag required)
  build close <ticket-id> <reason>                                Close a ticket (reason required)
  build defer <ticket-id> <reason>                                Defer a ticket (reason required)
  build reopen <ticket-id> [reason]                               Reopen a previously closed or deferred ticket (reason optional)
  build --help                                                    Show this help

Flags:
  --debug          Stream worker stdout/stderr to the orchestrator console in real time, and capture all worker
                   artifacts to .build/sessions/<session-id>/. Stdout lines are prefixed "worker> "; stderr
                   lines are prefixed "worker! ". Use 2>&1 | tee log.txt to capture both streams.
                   Writes: worker-stdin.txt, worker-stdout.txt, worker-stderr.txt, envelope-result.txt (or parse-error.txt on failure), worker-result.json
                   Replaces the default progress digest (mutually exclusive). No-op for ship (ship has no worker subprocess).
  --quiet          Suppress the default progress digest. Use for scripted/batch runs that want the pre-TLB-122
                   silent behavior. Mutually exclusive with --debug (which replaces the digest with raw stream).
  --summary-json   Emit the per-phase completion summary as a JSON object on stdout instead of the
                   default human-readable text block. Useful for piping into jq or downstream tooling.

Progress digest (default behavior for plan/implement/review):
  Without --debug or --quiet, the orchestrator prints a one-line digest per worker stream event to stderr
  (e.g. tool_use Read foo.cs, tool_use Bash git status, result ok 23888 out / 317k cache-read).
  Each line carries a [m:ss] offset from worker start. Default-on to a TTY stderr; auto-suppressed when
  stderr is redirected (2>err.log or piped) to keep CI/script logs clean. Set BUILD_PROGRESS=1 to force
  digest on even when stderr is redirected.

Summary contract:
  Each phase (plan / implement / review / ship) prints a deterministic completion summary block
  to stdout on both success and failure paths. The block is generated without any LLM call from
  the in-memory event stream, the phase result, Plane queries, and local git. Redirection works
  cleanly: `build plan TLB-N 2>/dev/null > summary.txt`. The --quiet flag (when introduced) will
  suppress the summary too.

Exit codes:
  0  Success
  1  Phase or command failure
  2  Config error or unknown verb
  3  Missing secret (env var not set)
  4  Phase infrastructure failure (review verifier crash, ship worktree missing, git unavailable)
""";
}
