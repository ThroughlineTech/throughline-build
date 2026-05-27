# Operation: build-scaffold

Add a `build scaffold <op-doc-path>` command that parses an op-doc markdown file and creates the corresponding Plane ticket tree: one plan-ticket per Plan, one brief-ticket per Brief, with parent-child relationships and appropriate initial labels. Replaces the claude-config `/op-scaffold` equivalent for the new pipeline. Five briefs across two plans.

## Why this exists

The throughline-build workflow is op-doc driven: design work happens in op-docs, then op-docs get scaffolded into Plane as ticket trees that the spine (plan/implement/review/ship) executes against. Today, scaffolding relies on claude-config's `/op-scaffold` slash command. The new pipeline has no equivalent.

Without `build scaffold`, the new pipeline can't ingest op-docs into Plane without falling back to claude-config or manual API calls. Given that op-doc authoring is one of the highest-frequency operations in this workflow, this gap is operationally significant.

The command needs to be careful about validation: an op-doc with malformed structure should be rejected BEFORE any ticket creation begins (avoid partial trees in Plane that need cleanup). Validation-first, creation-second is the load-bearing design choice.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Op-doc parsing and validation | - | M |
| B    | Plane integration and CLI | A | M |

Plan A delivers the parser and validator: read a markdown op-doc, produce a structured representation, validate against the format spec. No Plane integration in Plan A - the parser/validator can be tested in isolation against fixture op-docs. Plan B integrates with Plane (parent-child relationships, batch creation with rollback on partial failure) and adds the CLI command.

## Plan A: Op-doc parsing and validation

### Goal

`OpDocParser` reads a markdown file and produces a typed `OpDoc` record. `OpDocValidator` checks the parsed structure against the format spec (every brief has OOS, every plan has goal, dispatch order matches plans, etc.) and returns validation errors.

Briefs are sequential within this plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | op-doc-types | Typed records representing the op-doc structure: OpDoc, Plan, Brief, DispatchEntry | - | src/ThroughlineBuild.Scaffold/OpDocTypes.cs, tests/ThroughlineBuild.Scaffold.Tests/OpDocTypesTests.cs |
| 02 | op-doc-parser | Markdown parser that reads an op-doc file and produces an OpDoc record | 01 | src/ThroughlineBuild.Scaffold/OpDocParser.cs, tests/ThroughlineBuild.Scaffold.Tests/OpDocParserTests.cs, tests/ThroughlineBuild.Scaffold.Tests/Fixtures/example-op-doc.md |
| 03 | op-doc-validator | Validator that checks parsed OpDoc against the strict format spec and returns errors | 02 | src/ThroughlineBuild.Scaffold/OpDocValidator.cs, src/ThroughlineBuild.Scaffold/ValidationResult.cs, tests/ThroughlineBuild.Scaffold.Tests/OpDocValidatorTests.cs |

### Briefs - detail

#### Brief 01: op-doc-types

Goal: Define the typed records that represent a parsed op-doc's structure. Other briefs build on these.

Inputs:
- Existing op-doc format conventions (documented in the strict format spec the operator uses; representative examples include op-pipeline-compare-harness, op-brief-template-externalization, this op-doc)

Outputs:
- `src/ThroughlineBuild.Scaffold/` - new project. Add it to the solution file.
- `OpDoc` record: `string OperationSlug, string Title, string Why, IReadOnlyList<DispatchEntry> DispatchOrder, IReadOnlyList<Plan> Plans, string WhatDoneLooksLike`
- `DispatchEntry` record: `string PlanId, string Name, string? DependsOn, string Effort` (Effort one of "S", "M", "L"; DependsOn either a plan ID like "A" or null/dash for no deps)
- `Plan` record: `string Id, string Name, string Goal, IReadOnlyList<Brief> Briefs`
- `Brief` record: `string Slug, int Number, string Title, string Goal, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, IReadOnlyList<string> AcceptanceCriteria, string? Notes, IReadOnlyList<string> OutOfScope, string? DependsOn` (the brief's deps from the briefs table; refers to other brief numbers or "-")
- `OpDocParseError` record: `int LineNumber, string Section, string Message`
- All records `init`-only (immutable after construction)
- Tests covering: record construction with valid values; record equality (records get this for free but verify); records survive JSON serialization round-trip if needed downstream

Acceptance:
- [ ] New project `ThroughlineBuild.Scaffold` exists in solution
- [ ] All listed records exist with the documented field shape
- [ ] Records are immutable (init-only properties)
- [ ] Tests pass
- [ ] No dependencies on Plane integration in this brief

Notes: Keep the record shape close to the op-doc's visible structure. Operator who reads an op-doc can mentally map sections to record fields. This pays off in error messages later: a validator complaining about "Plan A's Brief 02 missing OOS" maps directly to a `Plan { Id: "A" }.Briefs[1].OutOfScope.IsEmpty` check.

If the op-doc format evolves (new sections added, different dispatch order conventions), the records change in lockstep. Document the format version this iteration targets in a comment at the top of OpDocTypes.cs.

OOS:
- Do not include any parsing or validation logic in this brief (separate briefs)
- Do not include Plane integration (Plan B)
- Do not implement a format-version migration path (current format only)
- Do not implement export to other formats (not the goal)

#### Brief 02: op-doc-parser

Goal: Markdown parser that reads an op-doc file from disk, walks its structure, and produces a populated OpDoc record. Parse errors are surfaced as OpDocParseError records rather than exceptions where possible (gather-then-report; fail at end of parse with the full list).

Inputs:
- OpDoc and related types from Brief 01
- The strict op-doc format spec (operation slug from H1, Why section after H2, Dispatch order table, Plan sections with Goal/Briefs table/Briefs detail, What done looks like)
- A Markdown library if available (Markdig is the standard for .NET); otherwise hand-roll a section-aware parser since the structure is regular

Outputs:
- `OpDocParser` class with `Parse(string filePath)` static method returning `ParseResult` (a record with `OpDoc? Parsed, IReadOnlyList<OpDocParseError> Errors`)
- Parser walks the markdown sections by heading level:
  - H1 (`# Operation: <slug>`) → operation slug + the paragraph immediately following becomes Title
  - H2 `## Why this exists` → Why content (everything until next H2)
  - H2 `## Dispatch order` → table parsing; each row is a DispatchEntry
  - H2 `## Plan A: <Name>` (repeated per plan) → Plan record
    - H3 `### Goal` → Goal text
    - H3 `### Briefs` → table; each row populates Brief stub records
    - H3 `### Briefs - detail` → individual H4 `#### Brief NN: <slug>` sections fill in the brief details
  - H2 `## What done looks like` → WhatDoneLooksLike content
- Brief detail parsing:
  - "Goal:" paragraph → Goal field
  - "Inputs:" bullet list → Inputs
  - "Outputs:" bullet list → Outputs
  - "Acceptance:" checkbox list → AcceptanceCriteria (strip the `[ ]` prefix)
  - "Notes:" paragraph(s) → Notes
  - "OOS:" bullet list → OutOfScope
- Errors surfaced (with line numbers):
  - Missing H1 operation header
  - Missing required H2 sections (Why, Dispatch order, What done looks like)
  - Plan referenced in dispatch order but no `## Plan X:` section exists
  - Brief in briefs table but no `#### Brief NN:` detail section
  - Brief missing required subsections (Goal, OOS, Acceptance)
  - Dispatch order table missing required columns
- Tests covering:
  - Valid op-doc parses without errors (use the fixture file)
  - Op-doc with missing OOS in one brief produces one error pointing at that brief
  - Op-doc with extra/unknown plans surfaces as warning, not error (forward-compat)
  - Op-doc with malformed dispatch table produces clear errors per problem row
  - Fixture file: `Fixtures/example-op-doc.md` - a known-good op-doc covering the parsing surface (multiple plans, multiple briefs per plan, with all sections populated)

Acceptance:
- [ ] `OpDocParser.Parse(filePath)` returns ParseResult with OpDoc populated for the fixture op-doc
- [ ] Parse errors include line numbers and section context
- [ ] All required sections are recognized
- [ ] Brief detail sections are matched to brief table entries by number
- [ ] Multiple plans and multiple briefs per plan are supported
- [ ] Tests pass against the fixture op-doc

Notes: The parser should be lenient about whitespace, blank lines, and section ordering within reasonable limits, but strict about presence of required sections. Operators write op-docs by hand; small formatting variations should not block parsing.

For the bullet list parsing inside briefs (Inputs, Outputs, OOS), recognize both `-` and `*` bullet markers. Preserve the text after the marker as the bullet's content; strip leading whitespace.

The fixture op-doc should be representative enough that any new op-doc parsing in the wild has a high chance of matching. Use one of the actual op-docs (sanitized or simplified) as the basis.

OOS:
- Do not implement format version detection (current format only)
- Do not implement op-doc generation from records (parser is read-only)
- Do not implement section reordering or auto-fix (validation surfaces errors; operator fixes the source)
- Do not implement parsing of sections we deliberately omitted from op-doc format (Risks, Future, Verification - these are forbidden per the format spec)

#### Brief 03: op-doc-validator

Goal: Beyond parse-level checks, validate semantic constraints: dispatch order plan IDs match Plan sections, brief numbering is sequential, brief deps reference real briefs, no forbidden sections present.

Inputs:
- OpDoc from Brief 01 (populated)
- The strict format spec (the operator's strict-format rules: dispatch order uses plan IDs only, OOS mandatory per brief, no Risks/Future/Verification sections, acceptance as WHAT not HOW, etc.)

Outputs:
- `OpDocValidator` class with `Validate(OpDoc opDoc)` returning `ValidationResult` (a record with `bool IsValid, IReadOnlyList<ValidationError> Errors, IReadOnlyList<ValidationWarning> Warnings`)
- `ValidationError` record: `string Code, string Path, string Message` (Path like `"Plans[A].Briefs[02].OutOfScope"`; Code a stable identifier like `"OOS_MISSING"`)
- `ValidationWarning` record: same shape as ValidationError but non-blocking
- Validation rules implemented:
  - **Errors (block scaffolding):**
    - Every Plan in DispatchOrder has a matching Plan section (by ID)
    - Every Plan section has a non-empty Goal
    - Every Brief has non-empty Slug, Title, Goal
    - Every Brief has non-empty OutOfScope list (mandatory per format)
    - Every Brief has at least one acceptance criterion
    - Brief Numbering is sequential within each Plan (01, 02, 03... no gaps, no duplicates)
    - DispatchEntry.DependsOn references a real Plan ID or is "-" / empty
    - No Plan or Brief uses forbidden section names (Risks, Future, Verification)
    - Operation slug is non-empty and matches the pattern `^[a-z][a-z0-9-]*$`
  - **Warnings (don't block):**
    - Brief notes section is empty
    - Brief OutOfScope has fewer than 3 entries (light coverage)
    - Brief acceptance criteria contain implementation details (heuristic: "implement", "create file", "write code" - these suggest HOW not WHAT; flag for operator review)
    - DispatchOrder Effort field uses non-standard value (anything other than S, M, L)
- `Validate` returns IsValid=true only if Errors list is empty (warnings are tolerated)
- Tests covering:
  - Valid OpDoc passes validation cleanly
  - OpDoc with missing OOS produces error with correct Code and Path
  - OpDoc with forbidden "Risks" section produces error
  - OpDoc with non-sequential brief numbering produces error per gap
  - OpDoc with light OOS coverage produces warning
  - Acceptance criterion with HOW-language produces warning
  - DispatchOrder referencing non-existent Plan produces error

Acceptance:
- [ ] OpDocValidator exists with Validate method
- [ ] All listed error rules implemented and tested
- [ ] All listed warning rules implemented and tested
- [ ] ValidationResult clearly distinguishes errors from warnings
- [ ] Path strings make it easy to locate the problem in the source
- [ ] Tests pass

Notes: Validation is the gate before any Plane API call happens. An op-doc with errors must NOT be partially scaffolded. The validator's job is to give operators a complete list of issues so they can fix everything in one pass.

The HOW-detection heuristic in acceptance criteria is intentionally lightweight (substring match on a few common HOW-words). A more sophisticated check would require NLP and probably isn't worth it; the warning prompts the operator to review, that's enough.

If operator wants to override warnings, the CLI command can accept a `--accept-warnings` flag in Plan B. Errors are not overridable; fix the op-doc.

OOS:
- Do not auto-fix any validation issue
- Do not implement learning from past validation outcomes
- Do not implement custom validation rules per project (one strict spec for all)
- Do not validate the op-doc's CONTENT quality (e.g., whether the design is good) - only structural compliance

## Plan B: Plane integration and CLI

### Goal

Scaffold a validated OpDoc into Plane: create plan tickets, create brief tickets with parent links, apply initial labels. Plus the `build scaffold` CLI command.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | scaffold-phase | ScaffoldPhase orchestrates parse, validate, then create the Plane ticket tree | A | src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs, src/ThroughlineBuild.Contracts/IPlaneTicketing.cs (extended for parent-child), src/ThroughlineBuild.Plane/PlaneTicketingClient.cs (extended), tests/ThroughlineBuild.Scaffold.Tests/ScaffoldPhaseTests.cs |
| 05 | scaffold-cli | `build scaffold` CLI command with dry-run, validate-only, and accept-warnings modes | 04 | src/ThroughlineBuild.Cli/Commands/ScaffoldCommand.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ScaffoldCommandTests.cs |

### Briefs - detail

#### Brief 04: scaffold-phase

Goal: ScaffoldPhase wires the parser, validator, and Plane creation into a single operation. Validation errors abort before any ticket creation. Plane creation is best-effort for resilience but reports per-ticket success/failure clearly.

Inputs:
- OpDocParser and OpDocValidator from Plan A
- `IPlaneTicketing.CreateTicketAsync` (from op-build-new Brief 01) - if not yet shipped, this brief blocks on that one OR re-implements creation directly here as a temporary measure
- The existing IEventLog
- Plane API for parent-child relationship creation (typically a separate PATCH after creation, or a `parent` field in the create payload - depends on Plane's API shape)

Outputs:
- `IPlaneTicketing` gains parent-child support if not already present:
  ```csharp
  Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct);
  ```
- PlaneTicketingClient implements SetParentAsync via the appropriate Plane API call
- `ScaffoldPhase` class with `RunAsync(ScaffoldOptions options, CancellationToken ct)`:
  1. Parse the op-doc file (fail-fast if parser returns errors)
  2. Validate the parsed OpDoc (fail-fast if validator returns errors)
  3. If options.AcceptWarnings is false and warnings exist, report warnings and ask for confirmation (or fail if non-interactive)
  4. If options.DryRun is true, print what WOULD be created and exit without API calls
  5. For each Plan in dispatch order:
     a. Create a plan-ticket via CreateTicketAsync with title `"Plan {Id}: {Name}"`, type `"task"`, descriptionHtml = the Plan's Goal text + a list of briefs in this plan, initialLabels = `["plan-ticket"]`
     b. For each Brief in this plan:
        - Create a brief-ticket with title `"{Slug}"` (or `"Brief {Number}: {Slug}"` - pick one), type `"task"`, descriptionHtml = the brief's full content (Goal + Inputs + Outputs + Acceptance + Notes + OOS, rendered as HTML)
        - Call SetParentAsync to link the brief-ticket to the plan-ticket
  6. Emit events for each creation:
     - `Kind=5` (action) with `Data: {action: "create_ticket", id: <new-id>, role: "plan"|"brief"}`
     - `Kind=5` with `Data: {action: "set_parent", child: <child-id>, parent: <parent-id>}` for each linkage
- `ScaffoldResult` record: `int PlansCreated, int BriefsCreated, IReadOnlyList<string> CreatedTicketIds, IReadOnlyList<ScaffoldFailure> Failures`
- `ScaffoldFailure` record: `string Stage, string Detail` (Stage like "plan_A_create", "brief_A_02_parent_link")
- `ScaffoldOptions` record: `string OpDocPath, bool DryRun, bool AcceptWarnings`
- Failure handling: if a single creation fails mid-scaffolding, log the failure to ScaffoldResult.Failures, continue with subsequent creations (best-effort). Do NOT attempt to roll back already-created tickets - operator handles cleanup if needed (Plane retains the tickets; orphans without parents are just visible in the list view, no data integrity issue).
- Tests covering:
  - Valid op-doc + happy path creates the expected number of tickets with the right parent links
  - Op-doc with validation errors aborts before any creation
  - Op-doc with warnings + AcceptWarnings=false halts at warning report
  - DryRun produces creation preview without API calls
  - Plane creation failure on one brief still results in subsequent briefs being attempted; failure is recorded
  - SetParentAsync failure on one brief is logged but doesn't block subsequent briefs

Acceptance:
- [ ] ScaffoldPhase exists with RunAsync method
- [ ] Validation happens before any Plane API call
- [ ] DryRun mode previews without creating
- [ ] AcceptWarnings flag distinguishes blocking-on-warnings vs proceeding
- [ ] Per-creation events are emitted
- [ ] Failures are recorded in ScaffoldResult, not thrown
- [ ] Tests pass

Notes: The HTML rendering of brief content (Goal + Inputs + Outputs + Acceptance + Notes + OOS) is the key visible output operator sees in Plane. Make it readable: headings for each section, lists for bullets, code formatting where appropriate. A small markdown-to-HTML helper might be needed; if Markdig is already in the project (or if Plane accepts markdown directly), use that.

Plane's API for parent-child links may vary. Check whether the create payload accepts a `parent` field directly (one API call per child) or requires a separate PATCH (two API calls per child). Optimize for the lower-call-count path if both work.

The order of Plans-and-Briefs creation should match the op-doc's dispatch order so that the operator sees the tickets appear in Plane in the expected sequence. This is mostly cosmetic but matches operator expectation.

OOS:
- Do not implement rollback on partial failure (operator cleanup is fine; rollback is fragile)
- Do not implement re-scaffolding (no idempotency check; the operator handles "already scaffolded" by editing the op-doc and running scaffold against a different project, or by deleting tickets first)
- Do not implement op-doc updates that modify already-created tickets (separate command if needed)
- Do not implement label inheritance from Plan to Brief (per-ticket labels are fine; future ticket if needed)

#### Brief 05: scaffold-cli

Goal: `build scaffold <op-doc-path>` runs ScaffoldPhase. Optional flags for validate-only, dry-run, and accept-warnings.

Inputs:
- ScaffoldPhase from Brief 04
- Existing CLI command dispatch pattern

Outputs:
- `ScaffoldCommand` class
- CLI usage:
  ```
  build scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--debug]
  ```
  - `--validate-only`: parse and validate, do not invoke ScaffoldPhase's creation steps; print errors/warnings and exit
  - `--dry-run`: parse, validate, then print what would be created (with would-be titles and parent links) without creating
  - `--accept-warnings`: proceed past validation warnings without prompting
  - `--debug`: forwarded to ScaffoldPhase
- Validation output:
  ```
  Validating /path/to/op-doc.md ...
  Errors:
    [OOS_MISSING] Plans[B].Briefs[02].OutOfScope: brief is missing required OOS section
  Warnings:
    [OOS_LIGHT] Plans[A].Briefs[01]: only 2 OOS items (consider expanding)
  ```
- Dry-run output:
  ```
  Would create 2 plans + 5 briefs:
    [Plan A] "Plan A: <Name>" (plan-ticket)
      ↳ [Brief A.01] "<slug-01>"
      ↳ [Brief A.02] "<slug-02>"
    [Plan B] "Plan B: <Name>" (plan-ticket)
      ↳ [Brief B.01] "<slug-01>"
      ↳ [Brief B.02] "<slug-02>"
      ↳ [Brief B.03] "<slug-03>"
  ```
- Create output:
  ```
  Scaffolding /path/to/op-doc.md ...
  Created plan A: SURLF-50 "Plan A: <Name>"
    Created brief: SURLF-51 "<slug-01>" (parent: SURLF-50)
    Created brief: SURLF-52 "<slug-02>" (parent: SURLF-50)
  Created plan B: SURLF-53 "Plan B: <Name>"
    Created brief: SURLF-54 "<slug-01>" (parent: SURLF-53)
    ...
  Scaffold complete: 2 plans, 5 briefs created
  ```
- Exit codes: 0 for clean creation; 2 for validation errors; 3 for partial creation (some failures); other non-zero for unexpected errors
- Tests covering:
  - Validate-only mode exits cleanly without Plane API calls
  - Dry-run mode previews without API calls
  - Accept-warnings mode proceeds past warnings
  - Validation errors cause non-zero exit
  - Successful scaffold prints expected output

Acceptance:
- [ ] `build scaffold --help` documents the command
- [ ] All flags work (--validate-only, --dry-run, --accept-warnings, --debug)
- [ ] Validation output is readable and points to the source location
- [ ] Dry-run output is accurate
- [ ] Exit codes are documented and tested
- [ ] Tests pass

Notes: The validate-only mode is useful as a pre-commit hook on op-docs: operator can run it locally before committing an op-doc change to catch structural issues. Document this use case in the README.

The dry-run output should make it easy for operator to spot if the parser misread the op-doc (e.g., confused plan boundaries, missed briefs). The indentation showing parent-child relationships is a visual sanity check.

When ScaffoldPhase reports partial creation (some tickets created, some failed), the CLI surfaces all the failures with their stages so operator knows what to clean up or retry.

OOS:
- Do not implement automatic retry on Plane API failures
- Do not implement "scaffold to staging then promote" workflow
- Do not implement diff-against-current-Plane-state (would let operator update an already-scaffolded op-doc; complex and risky for v1)
- Do not implement interactive confirmation prompts beyond the warnings prompt (validate-then-create flow is mostly batch; interactivity is friction)

## What done looks like

`build scaffold path/to/op-doc.md` parses the op-doc, validates structure, and creates the corresponding ticket tree in Plane: one plan-ticket per Plan section, one brief-ticket per Brief (linked to its parent plan-ticket), with appropriate initial labels.

Validation runs before any API call, so an op-doc with structural issues never produces partial scaffolding in Plane. The validate-only mode lets operators check op-doc structure as part of the authoring workflow. Dry-run previews what would be created without touching Plane.

The throughline-build pipeline no longer depends on claude-config for op-doc scaffolding. An end-to-end workflow exists: draft op-doc → `build scaffold` → spine the resulting tickets → outcome shipped.

Combined with `build new` (single-ticket creation) and `build chain` (full-spine execution), the operator has the full toolkit for op-doc-driven development without leaving the new pipeline.