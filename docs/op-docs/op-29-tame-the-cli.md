# Operation: build-help-tiers

Refactor `build`'s help surface from a single monolithic `--help` dump into a tiered, convention-respecting CLI help system: a one-screen grouped command menu at the top level, per-command help showing only that command's options and exit codes, and a `build help <topic>` reference tier for the material that currently bloats the top level (full exit-code table, config schema, progress digest, summary contract). Adds the missing `--version`/`-V` and `-h` conventions and basic stdout/stderr/exit-code discipline along the way.

## Why this exists

Today `build --help` serves four readers in one scroll: the operator scanning what commands exist, the operator who knows the verb and needs its flags, the script author wiring exit codes and `--summary-json`, and the person setting the tool up. Collapsing all four into tier 0 is why the output reads as a wall. The per-command idea the operator already has (`build plan --help` shows plan's options) is the standard git/cargo/docker model; the only thing missing is the tier split itself. This operation builds the rendering contract once, then routes each audience to its own surface. Man-page generation and shell completion are out of scope; the tier split is the fix.

## Dispatch order

| Plan | Name | Depends on | Effort |
| --- | --- | --- | --- |
| A | Help contract, renderers, and routing | - | L |
| B | Version and global conventions | A | S |
| C | Per-command help content | A | M |
| D | Reference topics | A | S |

## Plan A: Help contract, renderers, and routing

### Goal

Establish the foundation every other plan consumes: a data model describing a command's help (name, group, summary, usage, options, exit codes, examples), a registry that collects per-command contributions, two pure renderers (tier-0 grouped menu, tier-1 per-command block), and the routing glue that intercepts `-h`/`--help` and dispatches to the right renderer. Sequencing within the plan: brief 01 lands the model plus a written inventory of where help text currently lives, and that map is the input every later brief and plan relies on for real file paths; briefs 02 and 03 build the two renderers against the model and proceed once 01 lands; brief 04 wires routing and needs both renderers, so it follows 03 and also consumes 02's output. Renderers are pure functions over the model with golden-file tests, so tier-0 and tier-1 output is locked before any command wiring happens.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 01 | help-model-and-inventory | Define the help-contribution model + empty registry; inventory current help emission and produce a file map | - | new: Help/HelpModel.cs, Help/HelpRegistry.cs; docs: notes/help-inventory.md |
| 02 | tier0-renderer | Render the grouped top-level command menu from the registry | 01 | new: Help/Tier0Renderer.cs + golden test |
| 03 | tier1-renderer | Render a single command's help block from its model | 01 | new: Help/Tier1Renderer.cs + golden test |
| 04 | help-routing | Intercept -h/--help (top-level and per-command) and dispatch to the right renderer; no-args prints tier 0 | 03 | modified: CLI entry/dispatch (located in 01) |

### Briefs - detail

#### Brief 01: help-model-and-inventory

Goal: Create the data model that describes one command's help and an empty registry that collects those descriptions, then locate and document where help text is currently produced so later briefs have real file paths.

Inputs: The current monolithic help emitter (a single usage string in the CLI entry path); the existing command dispatch categories (`IWorkflowPhase`, `ITicketCommand`, `IOperationCommand`).

Outputs:
- `Help/HelpModel.cs` (a record carrying name, group, one-line summary, usage, ordered options with descriptions, exit codes, examples; a group enum: Pipeline, WorkItems, Configure)
- `Help/HelpRegistry.cs` (collects contributions, looks one up by command name, enumerates by group)
- `notes/help-inventory.md` (the file map: where the current help string lives, the entry/dispatch file name, and where each command name is matched)

Acceptance:
- [ ] A command's complete help can be expressed as model data with no formatting baked into the model
- [ ] The model distinguishes global options from command-specific options
- [ ] The registry returns one command's contribution by name and all contributions grouped by their group
- [ ] The inventory doc names the actual entry/dispatch file and the current help-string location
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Model only in this brief; do not move any rendering or wiring yet. The inventory's file map is load-bearing for every later brief that touches the entry path, so it must name real files rather than guesses.

OOS:
- The tier-0 and tier-1 renderers
- Routing or any `-h`/`--help` interception
- Populating any command's actual help contribution

#### Brief 02: tier0-renderer

Goal: Render the top-level grouped command menu: group headings, aligned command-name and summary columns, a global-options block, and a static footer pointing at `build help <topic>`.

Inputs: `Help/HelpModel.cs` and `Help/HelpRegistry.cs` from brief 01.

Outputs:
- `Help/Tier0Renderer.cs` producing the menu string from registry contents
- A golden-file test fixing the expected menu output

Acceptance:
- [ ] Output lists every registered command under its group heading (Pipeline, WorkItems, Configure)
- [ ] Output fits a single screen of roughly 50 lines and reads top to bottom without horizontal overflow
- [ ] A global-options section lists -h/--help, -V/--version, --debug, --quiet, --summary-json
- [ ] A footer line points the reader at the reference topics
- [ ] A golden test asserts the rendered menu
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The reference-topics footer is static text owned here, so the reference plan never has to touch tier-0 output and the two plans cannot collide on the same artifact.

OOS:
- Wiring the renderer to any flag or to the dispatcher
- Command-specific option detail
- The reference-topic content the footer points at

#### Brief 03: tier1-renderer

Goal: Render one command's help block: a usage line, an options section listing only that command's options, the exit codes that command can return, and any examples.

Inputs: `Help/HelpModel.cs` and `Help/HelpRegistry.cs` from brief 01.

Outputs:
- `Help/Tier1Renderer.cs` producing the per-command block from a single contribution
- A golden-file test over a representative command

Acceptance:
- [ ] Output shows usage, options, exit codes, and examples sections, omitting any section the command leaves empty
- [ ] Only the command's own options appear, not the full global set repeated
- [ ] Exit codes shown are the codes that command can actually return
- [ ] A golden test asserts the rendered block for one command
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Keep the renderer pure over a single contribution; it should not reach into the registry for sibling commands.

OOS:
- Routing or flag interception
- Per-command exit-code content, which arrives with each command in Plan C
- The reference topics

#### Brief 04: help-routing

Goal: Intercept help requests and dispatch them: top-level `-h`/`--help` and bare `build` to the tier-0 renderer, `build <cmd> -h`/`--help` to the tier-1 renderer for that command.

Inputs: The tier-0 renderer (brief 02), the tier-1 renderer (brief 03), and the CLI entry/dispatch file named in the brief 01 inventory.

Outputs:
- Modified dispatch that recognizes help requests before normal command execution and prints the appropriate tier

Acceptance:
- [ ] `build` with no arguments prints the tier-0 menu and exits 0
- [ ] `build --help` and `build -h` print the tier-0 menu
- [ ] `build <cmd> --help` and `build <cmd> -h` print that command's tier-1 block
- [ ] A help request is recognized regardless of its position among the command's other arguments
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Help must short-circuit before a command validates its other arguments, so `build ship --help` works without a valid ticket id present.

OOS:
- `--version` handling (Plan B)
- Unknown-command handling (Plan B)
- `build help <topic>` (Plan D)
- Shell completion

## Plan B: Version and global conventions

### Goal

Add the small conventions that mark `build` as a proper CLI citizen and currently do not exist: a real `--version`, and disciplined output and exit behavior. Briefs 05 and 06 depend only on the routing and renderers from Plan A and are independent of each other, so order between them does not matter. Version reporting reads from the build artifact so it never drifts from a hardcoded constant. Output discipline establishes that explicitly requested help is success on stdout while usage errors are failures on stderr, and that color (if ever emitted) respects a non-interactive terminal.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 05 | version-flag | Add -V/--version sourced from the build | - | modified: CLI entry/dispatch |
| 06 | output-and-error-discipline | Unknown-command handling, stdout/stderr split, NO_COLOR/non-TTY | - | modified: CLI entry/dispatch |

### Briefs - detail

#### Brief 05: version-flag

Goal: Print the tool version on `--version`/`-V`, sourced from the assembly informational version rather than a literal in the code.

Inputs: The CLI entry/dispatch file (from the brief 01 inventory); the build's version metadata.

Outputs:
- A `--version`/`-V` path that prints a single version line and exits

Acceptance:
- [ ] `build --version` and `build -V` print a version line and exit 0
- [ ] The version value comes from the build artifact, not a hardcoded string duplicated elsewhere
- [ ] Version output is a single line suitable for scripts to capture
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Reading the informational version under AOT can pull in reflection paths, so confirm the AOT publish stays clean; a short git sha alongside the semantic version is welcome only if the build already embeds one.

OOS:
- A `build version` subcommand mirror (optional follow-up)
- Adding a new build step solely to embed a git sha
- Any changelog or release-notes generation

#### Brief 06: output-and-error-discipline

Goal: Make help and error output behave predictably for both humans and scripts: explicitly requested help is success on stdout, usage errors are failures on stderr with a pointer, and color output respects a non-interactive context.

Inputs: The CLI entry/dispatch file; the existing unknown-verb handling (currently exit 2).

Outputs:
- Unknown-command path prints a one-line error plus a pointer to `build --help` on stderr and exits 2
- Explicit help prints to stdout and exits 0
- Any ANSI styling is suppressed when `NO_COLOR` is set or stdout is not a TTY

Acceptance:
- [ ] An unknown command prints a brief error and a "see build --help" pointer to stderr and exits 2
- [ ] Explicitly requested help goes to stdout and exits 0
- [ ] `build --help | cat` and `build plan --help | cat` produce clean output with no escape codes
- [ ] Setting NO_COLOR suppresses any styling
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The existing exit-2-on-unknown-verb behavior is correct and should be preserved; only its message text and stream targeting change.

OOS:
- Reformatting the per-command exit-code tables (Plan C)
- Reference topics (Plan D)
- Adding color output (this brief only suppresses it)

## Plan C: Per-command help content

### Goal

Populate the help contribution for every command so the tier-1 renderer has real data, working group by group. Each command contributes its own usage, options, per-command exit codes, and examples; commands register independently, so briefs 07, 08, and 09 touch disjoint command sets and have no ordering dependency among themselves beyond the shared Plan A foundation. The per-command exit codes are where the current monolithic exit-code tables get redistributed: chain's seven-way table lives in chain's contribution, rework's and scaffold's overrides live in theirs, and the simple verbs carry the global 0/1/2/3 set. The pipeline group is first because it carries the gnarliest options (chain's per-phase agent flags) and the widest exit-code surface, so it exercises the renderer hardest.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 07 | pipeline-verbs-help | Contributions for chain, plan, implement, review, ship, rework | - | per-command files for the pipeline verbs |
| 08 | workitem-verbs-help | Contributions for new, scaffold, decompose, list, amend, close, defer, reopen | - | per-command files for the work-item verbs |
| 09 | configure-verbs-help | Contributions for init, settarget, user-guide | - | per-command files for the configure verbs |

### Briefs - detail

#### Brief 07: pipeline-verbs-help

Goal: Author the help contribution for each pipeline verb so its tier-1 block shows usage, only its own options, the exit codes it can return, and examples.

Inputs: The help model and registry from brief 01; the renderers from Plan A; the current help text for chain, plan, implement, review, ship, rework as the source of truth for options and exit codes.

Outputs:
- A registered help contribution for each of chain, plan, implement, review, ship, rework

Acceptance:
- [ ] `build chain --help` shows the per-phase agent options, the precedence note, chain's own exit-code set, and the dependency-ordering behavior
- [ ] `build plan --help` shows --from-brief and its config-key equivalence
- [ ] `build rework --help` and `build ship --help` show their own exit-code overrides
- [ ] Each verb's block lists only options that apply to it
- [ ] `--debug` being a no-op for ship is noted in ship's block
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: chain, rework, and scaffold override the global exit codes; each per-command block shows only the overridden set for that command, with the full consolidated table left to `build help exit-codes` in Plan D.

OOS:
- Work-item and configure verbs
- The consolidated exit-code reference topic
- Routing or version behavior

#### Brief 08: workitem-verbs-help

Goal: Author the help contribution for each work-item verb.

Inputs: The help model and registry from brief 01; the current help text for new, scaffold, decompose, list, amend, close, defer, reopen.

Outputs:
- A registered help contribution for each of new, scaffold, decompose, list, amend, close, defer, reopen

Acceptance:
- [ ] `build new --help` documents the text-vs-file-vs-stdin disambiguation and the draft-mode flags
- [ ] `build scaffold --help` shows its exit-code overrides and the validate-only and dry-run options
- [ ] `build list --help` shows its filter options
- [ ] amend, close, defer, reopen each show their required arguments
- [ ] Each verb's block lists only options that apply to it
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: new's text-vs-file rule and `--print-template` mode are easy to lose; keep them legible in the block.

OOS:
- Pipeline and configure verbs
- Reference topics
- Routing or version behavior

#### Brief 09: configure-verbs-help

Goal: Author the help contribution for the configure verbs.

Inputs: The help model and registry from brief 01; the current help text for init, settarget, user-guide.

Outputs:
- A registered help contribution for each of init, settarget, user-guide

Acceptance:
- [ ] `build init --help` documents --force, --print-template, and the connection flags
- [ ] `build settarget --help` documents the set, --unset, and bare-print forms
- [ ] `build user-guide --help` documents --force and --print-template
- [ ] Each verb's block lists only options that apply to it
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: settarget has three distinct behaviors depending on argument presence; the block should make all three legible.

OOS:
- Pipeline and work-item verbs
- Reference topics
- Routing or version behavior

## Plan D: Reference topics

### Goal

Build the tier-2 reference surface that the tier-0 footer already points at, and move the bloat off the top level into it. `build help <topic>` dispatches to topic content for exit-codes, config, digest, and summary. The consolidated exit-code table (the full global set plus every per-verb override) and the config schema land first in brief 10 because they are the densest reference; the progress-digest and summary-contract prose follow in brief 11 into the same dispatcher. Within the plan, brief 11 extends the dispatcher created in brief 10, so it depends on 10.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 10 | help-topic-dispatch-and-exit-codes | `build help <topic>` dispatcher + exit-codes and config topics | - | new: Help/Topics/*.cs; modified: CLI entry/dispatch |
| 11 | digest-and-summary-topics | digest and summary topic content | 10 | modified: Help/Topics |

### Briefs - detail

#### Brief 10: help-topic-dispatch-and-exit-codes

Goal: Add a `build help <topic>` command that prints reference content by topic name, and author the exit-codes and config topics.

Inputs: The CLI entry/dispatch file from the brief 01 inventory; the current `--help` sections for exit codes and config keys as source content.

Outputs:
- A topic dispatcher under `Help/Topics`
- `exit-codes` content: the full global table plus chain, rework, and scaffold overrides as one consolidated reference
- `config` content: the `[plan]` mode schema and the flag-vs-key precedence

Acceptance:
- [ ] `build help exit-codes` prints the complete consolidated exit-code reference
- [ ] `build help config` prints the config-key schema
- [ ] An unknown topic prints the list of valid topics and exits non-zero
- [ ] The topic names match the footer printed by the tier-0 renderer
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: This is the home for the per-verb exit-code tables in full; the per-command blocks in Plan C show only their own subset and defer here for the complete picture.

OOS:
- digest and summary topics (brief 11)
- Changing any exit code or config behavior; this relocates documentation only
- Man-page or completion output

#### Brief 11: digest-and-summary-topics

Goal: Author the digest and summary reference topics from the prose currently living in the top-level `--help`.

Inputs: The topic dispatcher from brief 10; the current Progress digest and Summary contract sections of `--help`.

Outputs:
- `digest` content: the digest behavior plus the `BUILD_PROGRESS` override
- `summary` content: the summary contract and the `--summary-json` behavior

Acceptance:
- [ ] `build help digest` prints the progress-digest behavior and the BUILD_PROGRESS override
- [ ] `build help summary` prints the summary contract and the JSON-output behavior
- [ ] Neither block appears in `build --help` output any longer
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Confirm the moved prose is no longer emitted at tier 0; the point of the topic is to unload the top level, so a leftover copy at tier 0 defeats it.

OOS:
- Any new behavior for the digest or summary themselves
- Reformatting the digest output itself
- Per-command help (Plan C)

## What done looks like

`build` and `build --help` open on a single grouped screen: pipeline verbs, work-item verbs, and configure verbs, each a name and a one-line summary, with a short global-options block and a footer pointing at the reference topics. `build <cmd> --help` shows just that command's usage, its own options, the exit codes it can return, and a couple of examples, so chain's per-phase flags and seven-way exit codes are visible without wading past every other verb. `build help exit-codes`, `config`, `digest`, and `summary` hold the dense reference that used to bloat the top level, and that material no longer appears at tier 0. `build --version` and `build -V` report a version sourced from the build; `-h` works everywhere `--help` does; bare `build` is help, not an error; an unknown command is a one-line stderr error with a pointer and exit 2; and piping any help through `cat` yields clean, escape-code-free text. The operator scanning for a command, the operator who forgot a flag, and the script author reading exit codes each land on a surface sized for them.