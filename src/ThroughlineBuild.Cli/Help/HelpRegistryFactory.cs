namespace ThroughlineBuild.Cli;

/// <summary>
/// Builds the canonical <see cref="HelpRegistry"/> populated with all CLI commands.
/// The registry is purely in-memory; building it requires no I/O and is AOT-safe.
/// </summary>
public static class HelpRegistryFactory
{
    // ------------------------------------------------------------------
    // Shared exit-code entries used across multiple commands.
    // ------------------------------------------------------------------

    private static readonly ExitCodeEntry s_exit0 = new(0, "Success");
    private static readonly ExitCodeEntry s_exit1 = new(1, "Phase or command failure");
    private static readonly ExitCodeEntry s_exit2 = new(2, "Config error or bad arguments");
    private static readonly ExitCodeEntry s_exit3 = new(3, "Missing secret (env var not set)");
    private static readonly ExitCodeEntry s_exit4 = new(4, "Phase infrastructure failure");
    private static readonly ExitCodeEntry s_exit5 = new(5, "Operator aborted (typed 'q' at a prompt)");

    // ------------------------------------------------------------------
    // Public factory entry point.
    // ------------------------------------------------------------------

    /// <summary>Creates and returns a fully-populated help registry.</summary>
    public static HelpRegistry Build()
    {
        var r = HelpRegistry.CreateEmpty();

        // Pipeline
        r.Register(Plan());
        r.Register(Implement());
        r.Register(Review());
        r.Register(Ship());
        r.Register(Chain());
        r.Register(Rework());
        r.Register(Decompose());

        // Bring your own conductor
        r.Register(Worktree());
        r.Register(Gate());
        r.Register(Waves());
        r.Register(Candidate());
        r.Register(Sop());

        // Work items
        r.Register(New());
        r.Register(List());
        r.Register(Get());
        r.Register(Comments());
        r.Register(Comment());
        r.Register(Transition());
        r.Register(Relate());
        r.Register(Amend());
        r.Register(Close());
        r.Register(Defer());
        r.Register(Reopen());

        // Configure
        r.Register(Init());
        r.Register(SetTarget());
        r.Register(Setup());
        r.Register(UserGuide());
        r.Register(OpDoc());
        r.Register(Scaffold());

        return r;
    }

    // ------------------------------------------------------------------
    // Pipeline commands
    // ------------------------------------------------------------------

    private static CommandHelp Plan() => new(
        Name: "plan",
        Group: CommandGroup.Pipeline,
        Summary: "Run the plan phase for one or more tickets",
        Usage: "plan <ticket-id> [ticket-id ...] [--agent <name>] [--from-brief] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override (must match a [workers.<name>] key in config)", false),
            new("--from-brief",   "Explicitly promote the ticket in place instead of running the planning worker", false),
            new("--debug",        "Stream worker output and capture session artifacts", false),
            new("--quiet",        "Suppress the progress digest", false),
            new("--summary-json", "Emit phase completion summary as JSON", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3, s_exit4],
        Examples:
        [
            new("plan TLB-123", "Run the planning worker for one ticket"),
            new("plan TLB-123 --from-brief", "Promote an already-authored brief without worker investigation"),
        ]
    );

    private static CommandHelp Implement() => new(
        Name: "implement",
        Group: CommandGroup.Pipeline,
        Summary: "Run the implement phase for one or more tickets",
        Usage: "implement <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override", false),
            new("--debug",        "Stream worker output and capture session artifacts", false),
            new("--quiet",        "Suppress the progress digest", false),
            new("--summary-json", "Emit phase completion summary as JSON", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3, s_exit4],
        Examples:
        [
            new("implement TLB-123", "Run implementation for one ticket"),
            new("implement TLB-123 --agent codex", "Override the implementation worker agent"),
        ]
    );

    private static CommandHelp Review() => new(
        Name: "review",
        Group: CommandGroup.Pipeline,
        Summary: "Run the review phase for one or more tickets",
        Usage: "review <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override", false),
            new("--debug",        "Stream worker output and capture session artifacts", false),
            new("--quiet",        "Suppress the progress digest", false),
            new("--summary-json", "Emit phase completion summary as JSON", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3, s_exit4],
        Examples:
        [
            new("review TLB-123", "Run review for one ticket"),
            new("review TLB-123 --summary-json", "Emit the review completion summary as JSON"),
        ]
    );

    private static CommandHelp Ship() => new(
        Name: "ship",
        Group: CommandGroup.Pipeline,
        Summary: "Ship one or more reviewed tickets",
        Usage: "ship <ticket-id> [ticket-id ...] [--no-auto-merge] [--no-push] [--debug] [--summary-json]",
        Options:
        [
            new("--no-auto-merge", "Skip the automatic fast-forward merge after rebasing", false),
            new("--no-push",       "Keep ship fully local; skip pushing the target branch to the remote", false),
            new("--debug",         "Accepted for consistency; no-op because ship has no worker subprocess", false),
            new("--summary-json",  "Emit ship completion summary as JSON", false),
        ],
        ExitCodes:
        [
            new(0, "Shipped successfully, or only decruft cleanup failed after merge"),
            new(1, "Ship gate blocked at rebase, conflict scan, or regression checks"),
            new(2, "Config error or bad arguments"),
            new(3, "Missing secret (env var not set)"),
            new(4, "Ship infrastructure failure, including state check, fetch, or fast-forward merge"),
        ],
        Examples:
        [
            new("ship TLB-123", "Ship one reviewed ticket"),
            new("ship TLB-123 --no-push", "Merge locally without pushing the target branch"),
        ]
    );

    private static CommandHelp Chain() => new(
        Name: "chain",
        Group: CommandGroup.Pipeline,
        Summary: "Run the full chain for one or more tickets",
        Usage: "chain <ticket-id> [ticket-id ...] [--batch-implement <ticket-id,...>] [--dry-run] [--max-depth <n>] [--agent <name>] [--agent-plan <name>] [--agent-implement <name>] [--agent-review <name>] [--from-brief] [--no-auto-resolve] [--no-auto-merge] [--continue-past-failure] [--debug] [--summary-json]",
        Options:
        [
            new("--agent <name>",           "Worker agent override for all phases; per-phase flags beat --agent, which beats config", false),
            new("--agent-plan <name>",      "Worker agent override for the plan phase only", false),
            new("--agent-implement <name>", "Worker agent override for the implement phase only", false),
            new("--agent-review <name>",    "Worker agent override for the review phase only", false),
            new("--from-brief",             "Promote tickets in place; equivalent to [plan] mode = \"promote\" in config.toml", false),
            new("--batch-implement <ticket-id,...>", "Opt an ordered direct-child sibling group into one batch for the chain conductor", false),
            new("--dry-run",                   "Print the post-order tree schedule and branch topology without running phases", false),
            new("--max-depth <n>",             "Set the root-based traversal depth cap; 0 is root-only, 1 includes direct children", false),
            new("--no-auto-resolve",        "Do not auto-resolve parent chains before dispatching", false),
            new("--no-auto-merge",          "Skip the automatic fast-forward merge during ship", false),
            new("--continue-past-failure",  "Run descendants of a failed ticket instead of skipping them", false),
            new("--debug",                  "Stream worker output and capture session artifacts", false),
            new("--summary-json",           "Emit phase completion summaries as JSON", false),
        ],
        ExitCodes:
        [
            new(0, "All tickets completed successfully (or obsolete claim ratified)"),
            new(2, "Ticket state does not permit chain"),
            new(3, "Planning failed"),
            new(4, "Implementation failed before review"),
            new(5, "Review returned Fail"),
            new(6, "Review returned Rework more than the cap"),
            new(7, "Ship gate failed"),
        ],
        Examples:
        [
            new("chain TLB-123", "Run the chain for a single ticket"),
            new("chain TLB-123 TLB-124", "Run tickets in dependency order; descendants of failed ancestors are skipped"),
            new("chain TLB-123 --batch-implement TLB-124,TLB-125", "Pass an ordered sibling group to the chain conductor"),
            new("chain TLB-123 --agent codex --agent-review claude-code", "Use a default agent with a review-phase override"),
        ]
    );

    private static CommandHelp Rework() => new(
        Name: "rework",
        Group: CommandGroup.Pipeline,
        Summary: "Re-implement a Rework-verdict ticket",
        Usage: "rework <ticket-id> [--feedback \"...\"] [--debug]",
        Options:
        [
            new("--feedback \"...\"", "Supply reviewer feedback on the command line (overrides event-log lookup)", false),
            new("--debug", "Stream worker output and capture session artifacts", false),
        ],
        ExitCodes:
        [
            new(0, "Rework implement succeeded; ticket transitioned to InReview"),
            new(2, "Ticket is not in InProgress state"),
            new(3, "No Rework verdict found in event log; use --feedback to override"),
            new(4, "Implement phase failed during rework"),
        ],
        Examples:
        [
            new("rework TLB-123", "Rework using the latest Rework verdict from the event log"),
            new("rework TLB-123 --feedback \"Address reviewer notes\"", "Provide feedback explicitly"),
        ]
    );

    private static CommandHelp Decompose() => new(
        Name: "decompose",
        Group: CommandGroup.Pipeline,
        Summary: "Decompose a ticket into shippable sub-tickets",
        Usage: "decompose <ticket-id> [--agent <name>] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override", false),
            new("--debug",        "Stream worker output and capture session artifacts", false),
            new("--quiet",        "Suppress the progress digest", false),
            new("--summary-json", "Emit phase completion summary as JSON", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3, s_exit4],
        Examples: []
    );

    // ------------------------------------------------------------------
    // Bring-your-own-conductor commands
    // ------------------------------------------------------------------

    private static CommandHelp Worktree() => new(
        Name: "worktree",
        Group: CommandGroup.Conductor,
        Summary: "Lease, list, or tear down conductor-owned worktrees",
        Usage:
            "worktree lease --ticket <id> [--slug <slug>] [--base <ref>] [--require-seed <path>] [--json]\n" +
            "worktree teardown (--ticket <id> | --dir <path>) [--require-merged-into <ref>] [--force] [--json]\n" +
            "worktree list [--json]",
        Options:
        [
            new("--ticket <id>", "Ticket identity used to derive and locate the helper branch", false),
            new("--slug <slug>", "Optional readable suffix for the helper branch and directory", false),
            new("--base <ref>", "Base ref for the new helper branch (default: HEAD)", false),
            new("--require-seed <path>", "Fail before creation unless this allowlisted seed exists", false),
            new("--dir <path>", "Manifest-backed worktree directory to tear down", false),
            new("--require-merged-into <ref>", "Before teardown, prove the helper branch is an ancestor of the named ref", false),
            new("--force", "Skip worktree cleanliness checks and permanently discard work", false),
            new("--json", "Emit a versioned JSON envelope", false),
        ],
        ExitCodes:
        [
            new(0, "Success"),
            new(1, "Git, install, or filesystem failure"),
            new(2, "Config error or bad arguments"),
            new(6, "Ticket, branch, or target-path collision"),
            new(7, "Required seed is missing or not allowlisted"),
            new(8, "Containment or manifest validation refusal"),
        ],
        Examples:
        [
            new("worktree lease --ticket TLB-582 --slug worktree-verbs", "Lease an isolated workspace"),
            new("worktree list --json", "Inspect leases and unmanifested directories"),
            new("worktree teardown --ticket TLB-582", "Safely remove a clean lease and merged helper branch"),
        ]
    );

    private static CommandHelp Gate() => new(
        Name: "gate",
        Group: CommandGroup.Conductor,
        Summary: "Run configured review checks in the current working directory",
        Usage: "gate [--ticket <id>] [--role gating|advisory|all] [--require-checks] [--json]",
        Options:
        [
            new("--ticket <id>", "Optional ticket identity included in the result", false),
            new("--role <role>", "Run gating, advisory, or all checks (default: all); setup always runs first", false),
            new("--require-checks", "Fail when the selected check list is empty", false),
            new("--json", "Emit a versioned JSON envelope with typed per-check results", false),
        ],
        ExitCodes:
        [
            new(0, "Every selected setup and gating check passed, or no checks are configured without --require-checks"),
            new(1, "A setup or gating check failed, was inconclusive, or no checks were selected with --require-checks"),
            new(2, "Config error or bad arguments"),
        ],
        Examples:
        [
            new("gate", "Run the complete configured gate in the current tree"),
            new("gate --ticket TLB-583 --role gating --json", "Run setup and gating checks with structured output"),
        ]
    );

    private static CommandHelp Waves() => new(
        Name: "waves",
        Group: CommandGroup.Conductor,
        Summary: "Plan dependency-safe, conflict-aware ticket waves",
        Usage: "waves --input <path|-> [--json]",
        Options:
        [
            new("--input <path|->", "Read a ticket array or wave-plan object from a file or stdin", false),
            new("--json", "Emit the schedule, serialization reasons, and speedup verdict in a versioned envelope", false),
        ],
        ExitCodes:
        [
            new(0, "Wave schedule produced"),
            new(2, "Config, arguments, input JSON, or dependency scope is invalid"),
            new(5, "Selected tickets contain a dependency cycle"),
        ],
        Examples:
        [
            new("waves --input tickets.json", "Print a human-readable schedule"),
            new("waves --input - --json", "Read JSON from stdin and emit a typed envelope"),
        ]
    );

    private static CommandHelp Candidate() => new(
        Name: "candidate",
        Group: CommandGroup.Conductor,
        Summary: "Fingerprint the current candidate worktree",
        Usage: "candidate status --ticket <id> --base <ref> [--json]",
        Options:
        [
            new("--ticket <id>", "Ticket identity echoed in the result and compared with any lease manifest", false),
            new("--base <ref>", "Base commit/ref used for tracked and cached/index diff fingerprints", false),
            new("--json", "Emit a versioned JSON envelope with source-generated candidate status data", false),
        ],
        ExitCodes:
        [
            new(0, "Candidate status produced"),
            new(1, "Git failure, missing base ref, invalid worktree state, or unhashable path"),
            new(2, "Config error or bad arguments"),
        ],
        Examples:
        [
            new("candidate status --ticket TLB-600 --base main --json", "Emit base/head SHAs, diff hashes, touched paths, dirty state, and lease metadata"),
        ],
        Details:
        [
            """
            JSON data fields include:
              ticket, baseRef, baseSha, headSha, branch, workingDirectory,
              trackedDiffHash, cachedDiffHash, untrackedHash, touchedPaths,
              untrackedPaths, lease, and dirtyState.

            The tracked diff hash is the SHA-256 fingerprint of `git diff --binary --full-index --no-ext-diff --no-textconv <base> --`.
            The cached/index diff hash is the SHA-256 fingerprint of `git diff --cached --binary --full-index --no-ext-diff --no-textconv <base> --`.
            The untracked hash is computed from sorted untracked repository-relative paths, Git-style regular-file
            modes, and file-content hashes. Missing base refs, non-git directories, conflicted worktrees, invalid
            lease manifests, unreadable paths, untracked directories, and untracked symlink/reparse-point paths fail
            with a JSON error envelope.
            """
        ]
    );

    private static CommandHelp Sop() => new(
        Name: "sop",
        Group: CommandGroup.Conductor,
        Summary: "Validate the tracked conductor configuration",
        Usage: "sop doctor [--json]",
        Options:
        [
            new("--json", "Emit a versioned JSON envelope with typed doctor findings", false),
        ],
        ExitCodes:
        [
            new(0, "Conductor config and review-check contract are valid"),
            new(1, "Doctor found invalid conductor data or missing/unrunnable review checks"),
            new(2, "Bad arguments"),
        ],
        Examples:
        [
            new("sop doctor --json", "Validate .build/conductor.toml and [[review.checks]] without loading ticketing, workers, or events"),
        ],
        Details:
        [
            """
            `sop doctor` reads tracked .build/conductor.toml independently of .build/config.toml.
            It only looks at .build/config.toml for [[review.checks]], so missing ticketing
            credentials, worker configuration, and event configuration do not block it.

            Review invariants are structured prose. Doctor validates ids, statements, optional
            paths, and optional blocks_done shape only. It does not evaluate whether a statement is true.
            """
        ]
    );

    // ------------------------------------------------------------------
    // Work-item commands
    // ------------------------------------------------------------------

    private static CommandHelp New() => new(
        Name: "new",
        Group: CommandGroup.WorkItems,
        Summary: "Create a new ticket",
        Usage:
            "new <body-path> [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--debug]\n" +
            "new <text>      [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--review] [--debug|--quiet]\n" +
            "new -           [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--review] [--debug|--quiet]\n" +
            "new - --json    (read a strict JSON ticket draft from stdin; emit a JSON envelope)\n" +
            "new --print-template",
        Options:
        [
            new("--title \"...\"",  "Override the ticket title",                                              false),
            new("--type \"...\"",   "Set a backend work-item type when the configured project supports types", false),
            new("--label \"...\"",  "Add a label (may be repeated)",                                          false),
            new("--review",         "Draft mode only: open an interactive review loop before filing",         false),
            new("--json",           "Read a strict JSON draft from stdin and emit a JSON envelope; targets resolve before create", false),
            new("--debug",          "Draft/file mode: stream worker output when drafting and capture artifacts", false),
            new("--quiet",          "Draft mode only: suppress the worker progress digest",                   false),
            new("--print-template", "Print the body template to stdout; ignores other input forms",           false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("new body.md", "If body.md exists, file it as the ticket body"),
            new("new \"fix the onboarding typo\"", "If the first argument is not an existing file, draft from text"),
            new("new - --review", "Read draft input from stdin, then review before filing"),
            new("echo '{\"title\":\"...\"}' | new - --json", "File a structured draft and print {id,uuid,...}"),
            new("new --print-template", "Print the file-mode body template and exit"),
        ],
        Details:
        [
            """
            Strict JSON draft contract:
              Required:
                title: string, non-empty.
              Optional:
                type: string or null. Backend-dependent work-item type assignment; omit it unless the Plane project supports work-item types.
                description: Markdown string or null.
                acceptanceCriteria: one Markdown string or null. Use checklist Markdown such as "- [ ] first criterion"; a JSON array is invalid.
                labels: array of strings or null. Unknown labels are rejected before create.
                parent: ticket id string or null. Resolved before create.
                relations: array of objects or null. Each item is {"kind": string, "targetId": string}; targets resolve before create.
              Unknown fields are rejected.
              Markdown fields are rendered to Plane HTML before create.
              Omitting type sends no explicit type assignment and performs no work-item-type lookup.
              Offline help and --print-template do not probe Plane; type support is optional and backend-dependent.
            """,
            """
            Relation kinds for JSON drafts:
              Accepted kinds: relates_to, duplicate, blocked_by, blocking, start_before, start_after, finish_before, finish_after, implemented_by, implements.
              Normalization matches build relate: spaces and hyphens are accepted in place of underscores.
              A depends on B: {"kind":"blocked_by","targetId":"B"}.
              A blocks B: {"kind":"blocking","targetId":"B"}.
              A duplicates B: {"kind":"duplicate","targetId":"B"}.
              A is related to B: {"kind":"relates_to","targetId":"B"}.
              Plane may display inverse edges from the target side; the create request is written from the new ticket toward targetId.
            """,
            """
            Valid minimal JSON:
              {"title":"Fix README typo"}

            Valid full JSON:
              {"title":"Add retry telemetry","type":"Task","description":"Record retry counts.","acceptanceCriteria":"- [ ] Retry count is emitted\n- [ ] Tests cover failures","labels":["size:s"],"parent":"TLB-10","relations":[{"kind":"blocked_by","targetId":"TLB-9"}]}
            """
        ]
    );

    private static CommandHelp List() => new(
        Name: "list",
        Group: CommandGroup.WorkItems,
        Summary: "List tickets with optional filters",
        Usage: "list [--state <name>] [--parent <id>] [--type <name>] [--json]",
        Options:
        [
            new("--state <name>",  "Filter by state name",                  false),
            new("--parent <id>",   "Filter by parent ticket ID",            false),
            new("--type <name>",   "Filter by work item type",              false),
            new("--json",          "Emit the rows as a versioned JSON envelope instead of a table", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples: []
    );

    private static CommandHelp Get() => new(
        Name: "get",
        Group: CommandGroup.WorkItems,
        Summary: "Read a single ticket",
        Usage: "get <ticket-id> [--json]",
        Options:
        [
            new("--json", "Emit the ticket as a versioned JSON envelope on stdout instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("get TLB-541",        "Print a ticket as human-readable text"),
            new("get TLB-541 --json", "Print the ticket as a JSON envelope for an agent to parse"),
        ]
    );

    private static CommandHelp Comments() => new(
        Name: "comments",
        Group: CommandGroup.WorkItems,
        Summary: "List a ticket's comments",
        Usage: "comments <ticket-id> [--json]",
        Options:
        [
            new("--json", "Emit the comments as a versioned JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("comments TLB-541 --json", "Print a ticket's comments for an agent to read"),
        ]
    );

    private static CommandHelp Comment() => new(
        Name: "comment",
        Group: CommandGroup.WorkItems,
        Summary: "Post a comment on a ticket",
        Usage: "comment <ticket-id> <body|-> [--json]",
        Options:
        [
            new("<body|->", "Comment body as markdown, or '-' to read it from stdin", false),
            new("--json",   "Emit the created comment id as a JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("comment TLB-541 \"investigated; root cause is X\"", "Post a short note"),
            new("build review-notes.md | build comment TLB-541 -", "Post a long body from stdin"),
        ]
    );

    private static CommandHelp Transition() => new(
        Name: "transition",
        Group: CommandGroup.WorkItems,
        Summary: "Move a ticket to a new state",
        Usage: "transition <ticket-id> <state> [--json]",
        Options:
        [
            new("<state>", "Target state: Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled (space/hyphen tolerant)", false),
            new("--json",  "Emit the result as a JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("transition TLB-541 InProgress", "Move a ticket into progress"),
            new("transition TLB-541 \"In Review\" --json", "Move to In Review, machine-readable"),
        ]
    );

    private static CommandHelp Amend() => new(
        Name: "amend",
        Group: CommandGroup.WorkItems,
        Summary: "Amend an existing ticket",
        Usage: "amend <ticket-id> <option> [<option> ...] [--json]",
        Options:
        [
            new("--title \"...\"",      "Replace the ticket title",                              false),
            new("--priority <value>",    "Set urgent, high, medium, low, or none",                 false),
            new("--type <name>",         "Set the Plane issue type by name",                       false),
            new("--label-add <name>",    "Add a label; repeat the option to add multiple labels",  false),
            new("--label-remove <name>", "Remove a label; repeat the option to remove multiple",   false),
            new("--parent <ticket-id>",  "Set the parent after resolving both ticket UUIDs",       false),
            new("--size S|M|L",         "Update the size label",                               false),
            new("--note \"...\"",       "Append a context note to the description",            false),
            new("--description <path>", "Replace the description from a file or stdin (-)",    false),
            new("--ac <path>",          "Replace the acceptance criteria from a file or stdin (-)", false),
            new("--json",               "Emit the result as a JSON envelope instead of text",  false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("amend TLB-563 --title \"Complete ticket amendment\" --priority high", "Update scalar metadata"),
            new("amend TLB-563 --label-add bug --label-add cli --label-remove stale", "Edit labels without replacing unrelated labels"),
            new("amend TLB-563 --parent TLB-500 --json", "Set a parent, machine-readable"),
        ]
    );

    private static CommandHelp Relate() => new(
        Name: "relate",
        Group: CommandGroup.WorkItems,
        Summary: "Create, list, or remove ticket relations",
        Usage:
            "relate <ticket-id> <relation-type> <target-id> [--json]\n" +
            "relate <ticket-id> --list [--json]\n" +
            "relate <ticket-id> --remove <relation-id> [--json]",
        Options:
        [
            new("<relation-type>", "relates_to, duplicate, blocked_by, blocking, start_before, start_after, finish_before, finish_after, implemented_by, or implements; space/hyphen tolerant", false),
            new("--list", "List relations with stable relation ids used by --remove", false),
            new("--remove <relation-id>", "Remove the exact edge returned by --list", false),
            new("--json", "Emit a versioned JSON envelope", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:
        [
            new("relate TLB-10 blocked_by TLB-9 --json", "Create one canonical dependency edge"),
            new("relate TLB-10 --list --json", "Get stable relation ids"),
            new("relate TLB-10 --remove RELATION-UUID --json", "Remove one exact edge"),
        ]
    );

    private static CommandHelp Close() => new(
        Name: "close",
        Group: CommandGroup.WorkItems,
        Summary: "Close a ticket",
        Usage: "close <ticket-id> <reason> [--no-cascade] [--json]",
        Options:
        [
            new("--no-cascade", "Do not close non-terminal child tickets", false),
            new("--json",       "Emit the result as a JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples: []
    );

    private static CommandHelp Defer() => new(
        Name: "defer",
        Group: CommandGroup.WorkItems,
        Summary: "Defer a ticket",
        Usage: "defer <ticket-id> <reason> [--no-cascade] [--json]",
        Options:
        [
            new("--no-cascade", "Do not defer non-terminal child tickets", false),
            new("--json",       "Emit the result as a JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples: []
    );

    private static CommandHelp Reopen() => new(
        Name: "reopen",
        Group: CommandGroup.WorkItems,
        Summary: "Reopen a closed or deferred ticket",
        Usage: "reopen <ticket-id> [reason] [--json]",
        Options:
        [
            new("--json", "Emit the result as a JSON envelope instead of text", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples: []
    );

    // ------------------------------------------------------------------
    // Configure commands
    // ------------------------------------------------------------------

    private static CommandHelp Init() => new(
        Name: "init",
        Group: CommandGroup.Configure,
        Summary: "Write .build/config.toml; at a TTY, interactively create or pick a Plane project (no UUID to paste)",
        Usage: "init [--force] [--print-template] [--no-interactive] [--from FILE] [--plane-url URL] [--workspace SLUG] [--project-id UUID] [--project-name NAME] [--token TOKEN | --token-env VAR]",
        Options:
        [
            new("--force",            "Overwrite an existing config file",                               false),
            new("--print-template",   "Print the template to stdout without writing a file",             false),
            new("--no-interactive",   "Never prompt; write the template from flags only (for automation)", false),
            new("--from FILE",        "Read credentials from a key=value file instead of prompting",     false),
            new("--plane-url URL",    "Set the Plane base URL",                                          false),
            new("--workspace SLUG",   "Set the workspace slug",                                          false),
            new("--project-id UUID",  "Use this project UUID directly (bypasses interactive create-or-pick)", false),
            new("--project-name NAME","Resolve or create the project by name non-interactively",         false),
            new("--token TOKEN",      "Set the Plane API token value directly",                          false),
            new("--token-env VAR",    "Set the env-var name that holds the Plane API token",             false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit5],
        Examples:
        [
            new("init",
                "At a TTY: prompts for base URL, workspace, and token, then offers to create a new project or pick an existing one from a most-recently-used menu (you never paste a UUID), then provisions and commits. Press Ctrl-C to cancel (exit 1) or type 'q' at any prompt to abort (exit 5)."),
            new("init --project-name \"Survey Smoketest\" --plane-url URL --workspace SLUG --token TOKEN",
                "Non-interactive one-shot: resolve or create the project by name, provision, and verify."),
            new("init --no-interactive --plane-url URL --workspace SLUG --token TOKEN",
                "Automation: write the config from flags with no prompts."),
        ]
    );

    private static CommandHelp SetTarget() => new(
        Name: "settarget",
        Group: CommandGroup.Configure,
        Summary: "Set or display the resolved target branch",
        Usage:
            "settarget <branch>\n" +
            "settarget --unset\n" +
            "settarget",
        Options:
        [
            new("--unset", "Remove the target_branch override from config", false),
        ],
        ExitCodes: [s_exit0, s_exit2],
        Examples: []
    );

    private static CommandHelp Setup() => new(
        Name: "setup",
        Group: CommandGroup.Configure,
        Summary: "Make a project workflow-ready: git init + .gitignore, and provision the Plane project (states + labels)",
        Usage: "setup [--check]",
        Options:
        [
            new("--check", "Verify only: report any missing git repo, .gitignore entries, or Plane states/labels and exit 1; mutate nothing", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3],
        Examples:
        [
            new("setup", "Initialize git, top up .gitignore, and create missing Plane states/labels"),
            new("setup --check", "Report readiness gaps without changing anything (CI gate)"),
        ]
    );

    private static CommandHelp UserGuide() => new(
        Name: "user-guide",
        Group: CommandGroup.Configure,
        Summary: "Write the operator user guide",
        Usage: "user-guide [--force] [--print-template]",
        Options:
        [
            new("--force",          "Overwrite an existing guide file",              false),
            new("--print-template", "Print the guide to stdout without writing a file", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples: []
    );

    private static CommandHelp OpDoc() => new(
        Name: "op-doc",
        Group: CommandGroup.Configure,
        Summary: "Print/write the op-doc spec, or generate a new op-doc skeleton",
        Usage: "op-doc spec [--print] [--write] [--force]",
        Options:
        [
            new("--print", "Print the embedded op-doc spec to stdout (default)", false),
            new("--write", "Write the embedded op-doc spec to docs/op-docs/op-doc-spec.md", false),
            new("--force", "Overwrite an existing generated spec file when used with --write", false),
        ],
        ExitCodes: [s_exit0, s_exit2],
        Examples:
        [
            new("op-doc spec", "Print the authoring spec"),
            new("op-doc spec --write --force", "Regenerate docs/op-docs/op-doc-spec.md"),
            new("op-doc new my-feature", "Print a minimal valid op-doc skeleton"),
            new("op-doc new my-feature --write", "Write docs/op-docs/op-my-feature.md"),
        ]
    );

    private static CommandHelp Scaffold() => new(
        Name: "scaffold",
        Group: CommandGroup.Configure,
        Summary: "Scaffold an op-doc into Plane",
        Usage: "scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--no-profile] [--force-profile] [--debug]",
        Options:
        [
            new("--validate-only",    "Parse and validate the op-doc without creating any tickets",          false),
            new("--dry-run",          "Show what would be created without making Plane API calls",           false),
            new("--accept-warnings",  "Proceed even when the op-doc has non-fatal warnings",                 false),
            new("--no-profile",       "Skip deriving review/ship checks from the op-doc into config.toml",   false),
            new("--force-profile",    "Overwrite review/ship checks even if they look hand-customized",      false),
            new("--debug",            "Stream diagnostic output",                                            false),
        ],
        ExitCodes:
        [
            new(0, "All plans and briefs created successfully"),
            new(1, "Unexpected error or complete failure with nothing created"),
            new(2, "Validation error (parse error, structural error, or missing required arg)"),
            new(3, "Partial creation (some tickets created, some failed; operator must inspect)"),
        ],
        Examples: []
    );
}
