namespace ThroughlineBuild.Cli;

public static class HelpTopicContent
{
    public const string ExitCodes = """
build help exit-codes

Global exit codes:
  0  Success.
  1  Phase or command failure.
  2  Config error, bad arguments, unknown verb, or unknown help topic.
  3  Missing secret. A required environment variable such as the Plane API token is not set.
  4  Phase infrastructure failure. Examples include verifier crash, ship worktree missing, or git unavailable.

Command-specific overrides:

  build chain
    0  Chain completed, obsolete claim was ratified, or parent chain completed.
    1  Unhandled chain command failure before a ChainResult was available, or aggregate multi-ticket failure.
    2  RefusedInitialState, RefusedDirtyTree, ParentHasGrandchildren, missing ticket ID, or ticket not found.
    3  StoppedAtPlan, ParentStoppedEarly, or Skipped.
    4  StoppedAtImplement.
    5  StoppedAtReview.
    6  ReworkCapExceeded.
    7  StoppedAtShip.
    8  GateVacuous.
    9  ReviewUnavailable (verifier blocked by a provider quota/rate-limit/auth error; review never ran).

  build rework
    0  Rework implementation succeeded and the ticket transitioned to InReview.
    1  Unexpected command failure.
    2  Ticket is not in InProgress state, missing ticket ID, multiple ticket IDs, config error, or bad arguments.
    3  No Rework verdict was found in the event log; use --feedback to override.
    4  Implement phase failed during rework.

  build scaffold
    0  All plans and briefs were created successfully.
    1  Unexpected error, cancellation, or complete failure with nothing created.
    2  Validation error: parse error, structural error, missing op-doc path, config error, or bad arguments.
    3  Partial creation: some tickets were created and some failed; the operator must inspect and clean up.

Notes:
  Per-command help blocks show the subset most relevant to that verb. This topic is the consolidated reference for the full global table plus chain, rework, and scaffold overrides.
  This reference documents current behavior only; it does not change any exit-code mapping.
""";

    public const string Config = """
build help config

[plan] schema:
  [plan]
  mode = "promote"

Keys:
  mode  Optional string. Defaults to "promote" when [plan] or mode is absent.

Allowed mode values:
  promote      Bypass worker investigation and promote the existing ticket description as the plan.
  investigate  Spawn the plan worker to investigate the ticket and produce the plan.

Precedence:
  build plan --from-brief and build chain --from-brief override [plan].mode for that invocation.
  Without --from-brief, [plan].mode controls plan behavior.
  If neither --from-brief nor [plan].mode is set, the default is "promote".

Validation:
  mode is case-insensitive.
  Any value other than "investigate" or "promote" is a config error and exits 2.
  Unknown keys under [plan] are ignored with a warning.
""";

    public const string Digest = """
build help digest

Progress digest:
  Without --debug or --quiet, worker-backed commands print a one-line digest per worker stream event to stderr.
  Digest lines summarize activity such as tool_use Read foo.cs, tool_use Bash git status, and terminal result statistics.
  Each line carries a [m:ss] offset from worker start.

Default behavior:
  The digest is default-on when stderr is a TTY.
  The digest is auto-suppressed when stderr is redirected or piped, such as 2>err.log, to keep CI and script logs clean.
  --quiet suppresses the digest.
  --debug replaces the digest with raw worker stdout/stderr streaming and captured session artifacts.

BUILD_PROGRESS override:
  Set BUILD_PROGRESS=1 to force digest output even when stderr is redirected.
  The override does not beat --quiet or --debug.
""";

    public const string Summary = """
build help summary

Summary contract:
  Phase commands print a deterministic completion summary to stdout on both success and failure paths.
  The summary is generated without an LLM call from the in-memory event stream, the phase result, Plane queries, and local git.
  Human-readable summaries are stable enough for redirection and grep workflows, such as build plan TLB-N 2>/dev/null > summary.txt.

Covered phase summaries:
  plan
  implement
  review
  ship
  decompose

--summary-json behavior:
  --summary-json emits the same phase completion summary as a JSON object on stdout instead of the human-readable text block.
  JSON output is intended for jq and downstream tooling.
  JSON serialization uses source-generated metadata so the path remains trim- and AOT-safe.
  On chain, --summary-json applies to each emitted phase summary.
""";
}
