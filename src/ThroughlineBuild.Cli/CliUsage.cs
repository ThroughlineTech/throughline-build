namespace ThroughlineBuild.Cli;

public static class CliUsage
{
    public const string UsageText = """
build - Throughline Build

Usage:
  build plan <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]       Run the plan phase for a ticket (multiple tickets dispatch sequentially, stops at first failure)
  build implement <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]  Run the implement phase for a ticket (multiple tickets dispatch sequentially, stops at first failure)
  build review <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]  Run the review phase for a ticket (multiple tickets dispatch sequentially, stops at first failure)
  build ship <ticket-id> [ticket-id ...] [--debug] [--summary-json] [--no-auto-merge]               Ship a reviewed ticket; local fast-forward merge, no push to remote; multiple tickets dispatch sequentially, stops at first failure; --debug accepted but is a no-op (ship has no worker subprocess)
  build chain <ticket-id> [ticket-id ...] [--agent <name>] [--agent-plan <name>] [--agent-implement <name>] [--agent-review <name>] [--debug] [--no-auto-resolve] [--no-auto-merge] [--continue-past-failure]  Run the full chain for one or more tickets; multi-ticket dispatch is level-synchronous (dependency-ordered, concurrency bounded by workers.max_concurrency); streams per-phase output to stdout. --continue-past-failure: by default, descendants of a failed ancestor are skipped; use this flag to run them anyway.
  build new <body-path> [--title "..."] [--type "..."] [--label "..."]* [--review] [--debug]  Create a new ticket from a body file (file mode: arg must be an existing file)
  build new <text> [--title "..."] [--type "..."] [--label "..."]* [--review] [--debug]       Create a new ticket from free-form text (draft mode: arg is not an existing file)
  build new - [--title "..."] [--type "..."] [--label "..."]* [--review] [--debug]            Read operator text from stdin, then create a new ticket (draft mode)
  build new --print-template                                                        Print the body template to stdout for redirection into a draft file

  text vs file disambiguation: if the argument is an existing file path, file mode is used unchanged.
  If it looks like a path (contains / or \, or ends in .md/.txt) but no file exists, a stderr notice
  is printed and a brief pause allows Ctrl-C before proceeding in draft mode. Multiple positional args
  are joined with spaces as draft text (e.g. build new fix the readme typo).
  build init [--force] [--print-template] [--plane-url URL] [--workspace SLUG] [--project-id UUID] [--token TOKEN | --token-env VAR]   Write .build/config.toml from the built-in template; --force overwrites an existing file; --print-template prints to stdout; flag values replace the corresponding REQUIRED_ placeholders
  build user-guide [--force] [--print-template]   Write the operator user guide to docs/throughline_build_userguide.md; --force overwrites an existing file; --print-template prints to stdout
  build settarget <branch>     Validate <branch> exists as a local git ref and write target_branch = "<branch>" under [work] in .build/config.toml; exits 2 if branch not found locally (run git checkout -b <branch> first)
  build settarget --unset      Remove target_branch from [work] in .build/config.toml; noop if key is already absent
  build settarget              Print the current resolved target_branch value: shows [work] override if set, or base_branch default from [ship] if not
  build rework <ticket-id> [--feedback "..."] [--debug]                               Re-implement a ticket that returned Rework verdict (single ticket only)
  build decompose <ticket-id> [--agent <name>] [--debug|--quiet] [--summary-json]     Decompose a ticket into independently-shippable sub-tickets
  build scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--debug]  Scaffold an op-doc into Plane (creates plan-tickets and brief-tickets with parent links)
  build list [--state <name>] [--parent <id>] [--type <name>]     List tickets with optional filters
  build amend <ticket-id> [--size S|M|L] [--note "..."] [--description <path|->] [--ac <path|->]  Amend an existing ticket (at least one flag required)
  build close <ticket-id> <reason>                                Close a ticket (reason required)
  build defer <ticket-id> <reason>                                Defer a ticket (reason required)
  build reopen <ticket-id> [reason]                               Reopen a previously closed or deferred ticket (reason optional)
  build --help                                                    Show this help

Flags:
  --agent <name>         Override the worker agent for the invocation. On plan/implement/review, applies to that
                         phase. On chain, applies to all phases unless a per-phase flag is also set.
                         Agent name must be a key in [workers.<name>] in the config file; unknown names
                         produce a clear error. Example: --agent claude-code-fast
  --agent-plan <name>    Override the worker agent for the plan phase only (chain only).
  --agent-implement <name>  Override the worker agent for the implement phase only (chain only).
  --agent-review <name>  Override the worker agent for the review phase only (chain only).
                         Per-phase flag beats --agent beats config. --agent on ship is not supported (no worker).
  --debug          Stream worker stdout/stderr to the orchestrator console in real time, and capture all worker
                   artifacts to .build/sessions/<session-id>/. Stdout lines are prefixed "worker> "; stderr
                   lines are prefixed "worker! ". Use 2>&1 | tee log.txt to capture both streams.
                   Writes: worker-stdin.txt, worker-stdout.txt, worker-stderr.txt, envelope-result.txt (or parse-error.txt on failure), worker-result.json
                   Replaces the default progress digest (mutually exclusive). No-op for ship (ship has no worker subprocess).
  --quiet          Suppress the default progress digest. Use for scripted/batch runs that want the pre-TLB-122
                   silent behavior. Mutually exclusive with --debug (which replaces the digest with raw stream).
  --summary-json   Emit the per-phase completion summary as a JSON object on stdout instead of the
                   default human-readable text block. Useful for piping into jq or downstream tooling.
  --review         (draft mode only) After drafting, open an interactive review loop before filing.
                   Choose [a]ccept to file with the current body, [e]dit to open $EDITOR, [r]egenerate
                   to re-run the drafter (optionally with extra context), or [q]uit to abort (exit 0).
  --error-location When set, prints the C# source filename, method, and line where a parse error or
                   fatal exception originated. Off by default. For parse errors the location is captured
                   at compile time (works in AOT); for exceptions it reads ex.StackTrace (requires
                   debug build or embedded PDB for line numbers).

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

  For 'build chain' verb only (see separate exit code mapping):
  0  ChainOutcome.Completed
  0  ChainOutcome.RatifiedObsolete (obsolete claim verified; prior work satisfies acceptance criteria)
  2  RefusedInitialState (ticket state does not permit chain)
  3  StoppedAtPlan (planning failed)
  4  StoppedAtImplement (implementation failed before review)
  5  StoppedAtReview (review returned Fail)
  6  ReworkCapExceeded (review returned Rework more than the cap)
  7  StoppedAtShip (ship gate failed)

  For 'build rework' verb only (overrides global codes 2 and 4):
  0  Implemented (rework implement succeeded; ticket transitioned to InReview)
  2  TicketNotInProgress (ticket is not in InProgress state)
  3  NoFeedbackAvailable (no Rework verdict found in event log; use --feedback to override)
  4  ImplementFailed (implement phase failed during rework)

  For 'build scaffold' verb only (overrides global codes 2 and 3):
  0  Clean creation (all plans and briefs created successfully)
  2  Validation error (parse errors, structural errors, or missing required arg)
  3  Partial creation (some tickets created, some failed; operator must inspect/clean up)
  1  Unexpected error (exception, cancellation, or complete failure with nothing created)
""";
}
