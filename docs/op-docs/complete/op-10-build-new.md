# Operation: build-new

Add a `build new` command that creates a Plane ticket from a body file (or stdin). Deterministic, no worker subprocess in v1 - the operator drafts the body using whatever tool they prefer (text editor, Claude Code interactively, the old system's `/ticket-new` for now), then hands the body to `build new` which validates structure and creates the ticket via Plane API. Four briefs across two plans.

## Why this exists

The new pipeline currently has no way to create tickets. Plan, implement, review, ship, amend, close, defer, reopen all operate on existing tickets. Ticket creation falls back to the old system's `/ticket-new` or manual Plane API calls, which means survey-lf is not yet self-sufficient as a workflow target.

Interactive interview-driven ticket creation (the old `/ticket-new` shape) requires persistent chat session semantics that don't fit the new pipeline's stateless worker architecture. v1 punts on the interactive piece: `build new` takes a pre-drafted body, validates it, creates the ticket. The drafting step happens outside the pipeline - operator's choice of tool. v2 could add an interactive draft helper, but v1 closes the operational gap with minimal architecture.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plane creation API and NewPhase | - | M |
| B    | CLI command and body template | A | S |

Plan A adds the missing CreateTicketAsync method to the Plane ticketing client and implements NewPhase as a deterministic orchestrator (no worker subprocess). Plan B wires the CLI command and adds a body template file the operator can use as a starting point.

## Plan A: Plane creation API and NewPhase

### Goal

`IPlaneTicketing.CreateTicketAsync` exists and is implemented for the Plane backend. `NewPhase` reads a body file, validates it, calls CreateTicketAsync, returns the new ticket ID. Both have unit tests.

Briefs are sequential within this plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | create-ticket-api | Add CreateTicketAsync to IPlaneTicketing; implement in PlaneTicketingClient | - | src/ThroughlineBuild.Contracts/IPlaneTicketing.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, tests/ThroughlineBuild.Plane.Tests/CreateTicketTests.cs |
| 02 | new-phase | NewPhase reads body, validates, calls CreateTicketAsync, returns NewResult with ticket ID | 01 | src/ThroughlineBuild.Phases/NewPhase.cs, src/ThroughlineBuild.Contracts/NewResult.cs, tests/ThroughlineBuild.Phases.Tests/NewPhaseTests.cs |

### Briefs - detail

#### Brief 01: create-ticket-api

Goal: Extend the ticketing abstraction with a creation method. The orchestrator passes title, type, description HTML, and optional initial labels; the backend creates the work item and returns the new ticket's identifier and UUID.

Inputs:
- Existing `IPlaneTicketing` interface in `ThroughlineBuild.Contracts`
- Existing `PlaneTicketingClient` implementation in `ThroughlineBuild.Plane`
- Plane's REST API for work item creation (POST to `/workspaces/{slug}/projects/{project_id}/issues/`)

Outputs:
- `IPlaneTicketing` gains a method signature:
  ```csharp
  Task<NewTicketResult> CreateTicketAsync(
      string title,
      string type,
      string descriptionHtml,
      IReadOnlyList<string>? initialLabelNames,
      CancellationToken ct);
  ```
- `NewTicketResult` record (in Contracts) with: `string Id` (the human-readable identifier like "SURLF-42"), `string Uuid` (the Plane work item UUID), `DateTime CreatedAt`
- `PlaneTicketingClient.CreateTicketAsync` implementation:
  - POST to the projects/issues endpoint with payload: `{name, description_html, labels: [uuids resolved from initialLabelNames]}`
  - For initial labels, resolve names to UUIDs via the existing label cache or a fresh `list-labels` call
  - Default state is whatever Plane assigns on creation (typically Backlog); the method does NOT set state explicitly
  - Returns NewTicketResult populated from the API response
- Tests covering:
  - Successful creation returns NewTicketResult with non-empty Id and Uuid
  - Creation with initial labels resolves and applies them
  - Creation with empty/null initial labels works
  - API error response throws a typed exception with diagnostic context
  - Label name that doesn't exist in the workspace throws a clear error (do not silently drop)

Acceptance:
- [ ] `IPlaneTicketing.CreateTicketAsync` signature exists
- [ ] `NewTicketResult` record exists in Contracts
- [ ] `PlaneTicketingClient.CreateTicketAsync` implementation works against a live Plane workspace (manual verification + unit test against a mocked HTTP layer)
- [ ] Initial labels supported; unknown label names produce a clear error
- [ ] Existing IPlaneTicketing consumers compile unchanged (additive method only)
- [ ] Unit tests pass

Notes: The `type` field maps to whatever Plane backend convention is appropriate. If Plane represents type as a label rather than a structured field, treat type as just another initial label (prepended to initialLabelNames). Document the mapping in the method's XML doc comment.

Body content is the source of truth for the description. Do not parse or transform the body in this method - it's stored as-is.

OOS:
- Do not create parent/child relationships in this method (op-scaffold uses this method but adds relationships in a separate call)
- Do not assign the ticket to anyone
- Do not set state explicitly (Plane's default applies)
- Do not run any LLM call (purely API-driven)
- Do not implement creation for non-Plane backends (interface remains pluggable; only Plane impl required)

#### Brief 02: new-phase

Goal: NewPhase orchestrates the deterministic create flow. Reads a body file from disk, validates basic structure (title present, body non-empty, acceptance criteria section present), calls CreateTicketAsync, emits events, returns the new ticket ID.

Inputs:
- `CreateTicketAsync` from Brief 01
- A body file path provided by the CLI (Brief 03)
- The existing event log infrastructure (`IEventLog`)

Outputs:
- `NewResult` record (Contracts): `string Id, string Uuid, IReadOnlyList<string> ValidationWarnings`
- `NewPhase` class with `RunAsync(NewPhaseOptions options, CancellationToken ct)`:
  - Reads body file from options.BodyPath
  - Parses out the title from the body (first line starting with `# ` or a `**Title:**` field)
  - Validates: title present (non-empty), body has at least 50 characters, contains an "Acceptance" or "acceptance criteria" header or marker
  - Validation warnings (not failures): missing Type field, missing OOS section, body shorter than 200 characters - these go into ValidationWarnings but don't block creation
  - If title parsing fails or body is empty: throw `NewPhaseValidationException` with the issue; do NOT proceed to creation
  - Calls `CreateTicketAsync` with parsed title, default type ("task" if not declared in body), full body as descriptionHtml, optional initial labels from options
  - Emits events:
    - `Kind=2` (worker dispatch) with `Data: {worker: "deterministic", role: "creator"}`
    - `Kind=5` (action) with `Data: {action: "create_ticket", id: <new-id>}`
    - `Kind=1` (LlmCall) with all zero fields and a comment field noting "no_llm_call" - or skip the LlmCall event entirely if the schema permits absence
  - Returns NewResult with the created ticket's Id, Uuid, and any validation warnings
- `NewPhaseOptions` record: `string BodyPath, IReadOnlyList<string>? InitialLabels, string? OverrideTitle, string? OverrideType`
- Tests covering:
  - Valid body file with title and acceptance criteria creates the ticket
  - Body without "Acceptance" section produces a validation warning but still creates
  - Empty body throws ValidationException without calling Plane API
  - Missing body file throws clear error
  - OverrideTitle in options wins over title parsed from body
  - Initial labels are passed through to CreateTicketAsync

Acceptance:
- [ ] NewPhase exists with RunAsync method
- [ ] Body file parsing extracts title from first `#` heading or `**Title:**` line
- [ ] Validation distinguishes errors (block creation) from warnings (proceed but report)
- [ ] Events are emitted in the right shape
- [ ] CreateTicketAsync is called with the right inputs
- [ ] Returns NewResult with new ticket Id, Uuid, and warnings
- [ ] Tests pass

Notes: The body parsing is intentionally lenient. Operator-drafted bodies vary in format; the validator catches the most basic structural issues without enforcing a rigid template. Validation warnings let the operator know about likely-missing sections without blocking creation.

The LlmCall event in the schema may not gracefully accept all-zero fields. Two options: extend the schema to mark LlmCall as optional per phase, OR emit a different event kind for deterministic phases. Pick whichever has the smaller blast radius. Document the choice in the brief's result.

If the body file path is relative, resolve it against the orchestrator's working directory (typically the repo root), not the worker's cwd (no worker in this phase, but for consistency with other phases).

OOS:
- Do not transform the body (no markdown rendering, no HTML conversion - pass body as-is to descriptionHtml; Plane handles markdown rendering)
- Do not interview the operator (no interactive flow in v1)
- Do not auto-generate the body from a topic prompt (v2 feature; could spawn a worker for that later)
- Do not implement bulk creation (one ticket per `build new` invocation; multi-ticket creation is op-scaffold's job)
- Do not create parent/child relationships (op-scaffold uses separate API calls for that)
- Do not assign labels for risk or size (those come from the plan phase later)

## Plan B: CLI command and body template

### Goal

`build new` command works end-to-end. Body template helper exists so operators have a known-good starting point.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | new-cli | `build new` CLI command: parse args, invoke NewPhase, print result | A | src/ThroughlineBuild.Cli/Commands/NewCommand.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/NewCommandTests.cs |
| 04 | body-template | Body template markdown file + helper command to print it | 03 | templates/new-ticket-body.md, src/ThroughlineBuild.Cli/Commands/NewCommand.cs (extended) |

### Briefs - detail

#### Brief 03: new-cli

Goal: `build new` command accepts a body file path, optional title override, optional labels, runs NewPhase, prints the new ticket ID and any validation warnings.

Inputs:
- NewPhase from Plan A
- The existing CLI command-dispatch pattern (look at `BuildCommand.cs` or wherever existing commands are wired)

Outputs:
- `NewCommand` class implementing the existing command interface
- CLI usage:
  ```
  build new <body-path> [--title <override>] [--type <override>] [--label <name>]... [--debug]
  ```
  - `<body-path>` required: path to the body markdown file
  - `--title`: override the title parsed from the body file
  - `--type`: override the type (default "task")
  - `--label`: repeatable; initial labels to apply on creation
  - `--debug`: capture session output (consistent with other commands; for build new this captures the validation report and API response)
- `build new --help` documents the command shape
- Output on success:
  ```
  Created SURLF-42 (uuid: <uuid>)
  https://plane.example.com/workspace/browse/SURLF-42/
  ```
  followed by validation warnings (if any) prefixed with `Warning:`
- Output on validation error: clear message indicating what's wrong with the body, exit code non-zero
- Tests covering:
  - `build new <body-path>` creates a ticket and prints expected output
  - `build new <missing-path>` produces clear error and non-zero exit
  - `--title` and `--type` overrides are applied
  - `--label` flags accumulate into the initial labels list
  - Validation warnings are surfaced in stdout

Acceptance:
- [ ] `build new --help` documents the command
- [ ] `build new <body-path>` creates a ticket
- [ ] All flags (title, type, label, debug) work
- [ ] Output format matches the spec
- [ ] Tests pass

Notes: Plane URL construction for the success message uses the workspace URL from `.build/config.toml` `[plane]` section. If the URL is not configured, omit that line rather than printing a malformed URL.

The `--debug` flag for a deterministic phase still produces a session directory (under `.build/sessions/<sid>/`) containing the validation report, the body file contents, and the API request/response (with sensitive fields like API token redacted). This is consistent with the debug pattern for other commands.

OOS:
- Do not implement interactive prompting (`-i` flag is a v2 feature)
- Do not implement body editing (operator uses their editor; this is a fire-and-forget creator)
- Do not implement dry-run (`--dry-run` could be a v2 feature for previewing what would be created)
- Do not implement creation of multiple tickets from a single invocation
- Do not implement file watch / re-run on body file change

#### Brief 04: body-template

Goal: A template markdown file that operators can copy and fill in, ensuring the body has the structure NewPhase validates against. Plus a `build new --print-template` subcommand that emits the template to stdout for easy redirection into a draft file.

Inputs:
- The body validation rules from Brief 02 (title required, acceptance section recommended)

Outputs:
- `templates/new-ticket-body.md` - markdown template with placeholder content:
  ```markdown
  # Title goes here

  **Type:** task | bug | enhancement

  ## Description

  One or two paragraphs describing what this ticket is for and why.

  ## Acceptance criteria

  - First criterion as a checkable bullet
  - Second criterion

  ## Out of scope

  - Things that are explicitly NOT part of this ticket

  ## Notes

  Any additional context, links, references.
  ```
- `build new --print-template` subcommand that emits the template content to stdout
- Operator workflow becomes: `build new --print-template > draft.md`, edit, `build new draft.md`
- README section explaining the template + workflow

Acceptance:
- [ ] Template file exists at `templates/new-ticket-body.md`
- [ ] Template uses sections that NewPhase's validator recognizes (title, acceptance criteria)
- [ ] `build new --print-template` prints the template to stdout
- [ ] Operator workflow (print → edit → create) is documented

Notes: The template should be opinionated enough to produce good tickets but not so rigid it discourages operator creativity. Section headings should match what the plan-phase brief expects when it later investigates the ticket (so plan can find acceptance criteria, out-of-scope items, etc.).

If the template is added as an embedded resource (matching the `Templates/*.md` pattern used by brief templates), the print-template subcommand reads it via TemplateLoader rather than from disk. Choose the pattern that matches what brief templates use; keep consistency.

OOS:
- Do not implement multiple templates (one shape only)
- Do not implement template variants per ticket type
- Do not implement interactive template-filling
- Do not auto-detect required sections per project

## What done looks like

`build new <body-path>` creates a Plane ticket from a markdown body file. The body has minimal validation (title present, acceptance criteria recommended), and the operator gets back a ticket ID and URL. Initial labels can be applied at creation. The body file is the source of truth for the ticket's content; no LLM call happens in the creation path.

Survey-lf and any other project using the new pipeline can now create tickets without falling back to claude-config. The operator's drafting workflow is decoupled from creation: draft in any tool (text editor, Claude Code chat, the old `/ticket-new`, or wherever), then hand the body to `build new`.

Interactive drafting (the old `/ticket-new` shape) remains a v2 enhancement. The architecture leaves room for it: a future NewWorkerPhase could spawn a Claude Code subprocess in an interactive mode that produces a draft body, which then flows through the existing NewPhase. v1 keeps the create path simple and deterministic.