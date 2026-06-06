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

    private static readonly ExitCodeEntry s_exit0  = new(0, "Success");
    private static readonly ExitCodeEntry s_exit1  = new(1, "Phase or command failure");
    private static readonly ExitCodeEntry s_exit2  = new(2, "Config error or bad arguments");
    private static readonly ExitCodeEntry s_exit3  = new(3, "Missing secret (env var not set)");
    private static readonly ExitCodeEntry s_exit4  = new(4, "Phase infrastructure failure");

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

        // Work items
        r.Register(New());
        r.Register(List());
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
        Name:    "plan",
        Group:   CommandGroup.Pipeline,
        Summary: "Run the plan phase for one or more tickets",
        Usage:   "plan <ticket-id> [ticket-id ...] [--agent <name>] [--from-brief] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override (must match a [workers.<name>] key in config)", false),
            new("--from-brief",   "Promote the ticket in place; equivalent to [plan] mode = \"promote\" in config.toml", false),
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
        Name:    "implement",
        Group:   CommandGroup.Pipeline,
        Summary: "Run the implement phase for one or more tickets",
        Usage:   "implement <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]",
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
        Name:    "review",
        Group:   CommandGroup.Pipeline,
        Summary: "Run the review phase for one or more tickets",
        Usage:   "review <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet] [--summary-json]",
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
        Name:    "ship",
        Group:   CommandGroup.Pipeline,
        Summary: "Ship one or more reviewed tickets",
        Usage:   "ship <ticket-id> [ticket-id ...] [--no-auto-merge] [--no-push] [--debug] [--summary-json]",
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
        Name:    "chain",
        Group:   CommandGroup.Pipeline,
        Summary: "Run the full chain for one or more tickets",
        Usage:   "chain <ticket-id> [ticket-id ...] [--batch-implement <ticket-id,...>] [--dry-run] [--max-depth <n>] [--agent <name>] [--agent-plan <name>] [--agent-implement <name>] [--agent-review <name>] [--from-brief] [--no-auto-resolve] [--no-auto-merge] [--continue-past-failure] [--debug] [--summary-json]",
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
        Name:    "rework",
        Group:   CommandGroup.Pipeline,
        Summary: "Re-implement a Rework-verdict ticket",
        Usage:   "rework <ticket-id> [--feedback \"...\"] [--debug]",
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
        Name:    "decompose",
        Group:   CommandGroup.Pipeline,
        Summary: "Decompose a ticket into shippable sub-tickets",
        Usage:   "decompose <ticket-id> [--agent <name>] [--debug|--quiet] [--summary-json]",
        Options:
        [
            new("--agent <name>", "Worker agent override", false),
            new("--debug",        "Stream worker output and capture session artifacts", false),
            new("--quiet",        "Suppress the progress digest", false),
            new("--summary-json", "Emit phase completion summary as JSON", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2, s_exit3, s_exit4],
        Examples:  []
    );

    // ------------------------------------------------------------------
    // Work-item commands
    // ------------------------------------------------------------------

    private static CommandHelp New() => new(
        Name:    "new",
        Group:   CommandGroup.WorkItems,
        Summary: "Create a new ticket",
        Usage:
            "new <body-path> [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--debug]\n" +
            "new <text>      [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--review] [--debug|--quiet]\n" +
            "new -           [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--review] [--debug|--quiet]\n" +
            "new --print-template",
        Options:
        [
            new("--title \"...\"",  "Override the ticket title",                                              false),
            new("--type \"...\"",   "Set the work item type",                                                 false),
            new("--label \"...\"",  "Add a label (may be repeated)",                                          false),
            new("--review",         "Draft mode only: open an interactive review loop before filing",         false),
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
            new("new --print-template", "Print the file-mode body template and exit"),
        ]
    );

    private static CommandHelp List() => new(
        Name:    "list",
        Group:   CommandGroup.WorkItems,
        Summary: "List tickets with optional filters",
        Usage:   "list [--state <name>] [--parent <id>] [--type <name>]",
        Options:
        [
            new("--state <name>",  "Filter by state name",                  false),
            new("--parent <id>",   "Filter by parent ticket ID",            false),
            new("--type <name>",   "Filter by work item type",              false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp Amend() => new(
        Name:    "amend",
        Group:   CommandGroup.WorkItems,
        Summary: "Amend an existing ticket",
        Usage:   "amend <ticket-id> (--size S|M|L | --note \"...\" | --description <path|-> | --ac <path|->) [...]",
        Options:
        [
            new("--size S|M|L",         "Update the size label",                               false),
            new("--note \"...\"",       "Append a context note to the description",            false),
            new("--description <path>", "Replace the description from a file or stdin (-)",    false),
            new("--ac <path>",          "Replace the acceptance criteria from a file or stdin (-)", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp Close() => new(
        Name:    "close",
        Group:   CommandGroup.WorkItems,
        Summary: "Close a ticket",
        Usage:   "close <ticket-id> <reason> [--no-cascade]",
        Options:
        [
            new("--no-cascade", "Do not close non-terminal child tickets", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp Defer() => new(
        Name:    "defer",
        Group:   CommandGroup.WorkItems,
        Summary: "Defer a ticket",
        Usage:   "defer <ticket-id> <reason> [--no-cascade]",
        Options:
        [
            new("--no-cascade", "Do not defer non-terminal child tickets", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp Reopen() => new(
        Name:    "reopen",
        Group:   CommandGroup.WorkItems,
        Summary: "Reopen a closed or deferred ticket",
        Usage:   "reopen <ticket-id> [reason]",
        Options:  [],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    // ------------------------------------------------------------------
    // Configure commands
    // ------------------------------------------------------------------

    private static CommandHelp Init() => new(
        Name:    "init",
        Group:   CommandGroup.Configure,
        Summary: "Write .build/config.toml from the built-in template",
        Usage:   "init [--force] [--print-template] [--plane-url URL] [--workspace SLUG] [--project-id UUID] [--token TOKEN | --token-env VAR]",
        Options:
        [
            new("--force",            "Overwrite an existing config file",                               false),
            new("--print-template",   "Print the template to stdout without writing a file",             false),
            new("--plane-url URL",    "Set the Plane base URL",                                          false),
            new("--workspace SLUG",   "Set the workspace slug",                                          false),
            new("--project-id UUID",  "Set the project UUID",                                            false),
            new("--token TOKEN",      "Set the Plane API token value directly",                          false),
            new("--token-env VAR",    "Set the env-var name that holds the Plane API token",             false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp SetTarget() => new(
        Name:    "settarget",
        Group:   CommandGroup.Configure,
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
        Examples:  []
    );

    private static CommandHelp Setup() => new(
        Name:    "setup",
        Group:   CommandGroup.Configure,
        Summary: "Make a project workflow-ready: git init + .gitignore, and provision the Plane project (states + labels)",
        Usage:   "setup [--check]",
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
        Name:    "user-guide",
        Group:   CommandGroup.Configure,
        Summary: "Write the operator user guide",
        Usage:   "user-guide [--force] [--print-template]",
        Options:
        [
            new("--force",          "Overwrite an existing guide file",              false),
            new("--print-template", "Print the guide to stdout without writing a file", false),
        ],
        ExitCodes: [s_exit0, s_exit1, s_exit2],
        Examples:  []
    );

    private static CommandHelp OpDoc() => new(
        Name:    "op-doc",
        Group:   CommandGroup.Configure,
        Summary: "Print/write the op-doc spec, or generate a new op-doc skeleton",
        Usage:   "op-doc spec [--print] [--write] [--force]",
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
        Name:    "scaffold",
        Group:   CommandGroup.Configure,
        Summary: "Scaffold an op-doc into Plane",
        Usage:   "scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--no-profile] [--force-profile] [--debug]",
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
        Examples:  []
    );
}
