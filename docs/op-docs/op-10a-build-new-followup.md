# Operation: build-new-draft

Extend `build new` with an agent-drafting path so operators can run `build new "developer widget that bars baz"` and get a structured ticket filed directly, the way `/tn` worked. A small worker subprocess (DraftPhase + Templates/draft.md) takes the free-form text and produces a template-formatted ticket body, which the existing NewPhase then files via CreateTicketAsync. Default behavior is fire-and-forget: drafter runs, ticket gets filed. Operators who want to inspect before filing use `--review` for an interactive accept/edit/regenerate loop. Four briefs across two plans. Builds on top of shipped build-new without modifying its file-mode behavior.

## Why this exists

The shipped build-new is correct but its UX has friction the old `/tn` command didn't: operator runs `build new --print-template`, copies it, fills it out, runs `build new <body-file>`. Four steps for a four-second thought. The dogfood pain is real: terse ticket ideas need to land in Plane fast or operators won't file them at all.

A small drafting worker closes the gap. Cost math favors it: the worker is small (cheap-class model, brief is ~200-300 tokens instruction + operator text in, output ~500 tokens, pennies even on Sonnet), and the better-structured ticket it produces reduces the plan phase's downstream cost because plan no longer has to reason about an unstructured terse description. Net cost probably decreases despite the extra call.

Dan's explicit decision: default to fire-and-forget. The `/tn` experience was solid in practice and prompt tuning handles drift. Operators who want a safety net use `--review` opt-in.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | DraftPhase + drafter template | - | M |
| B    | CLI integration: argument disambiguation + review loop | A | M |

## Plan A: DraftPhase + drafter template

### Goal

DraftPhase takes free-form operator text, invokes a worker subprocess with Templates/draft.md, parses the result into a markdown ticket body matching the build-new input format. Returns the body for downstream filing. Templates/draft.md instructs the worker to preserve operator intent without inventing scope.

Briefs are sequential because B02's template needs to be testable against B01's parser expectations.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | draft-phase | DraftPhase class invokes worker with operator text, parses WORKER_RESULT into ticket body markdown | - | src/ThroughlineBuild.Phases/DraftPhase.cs, src/ThroughlineBuild.Phases/DraftPhaseOptions.cs, src/ThroughlineBuild.Contracts/Models/DraftResult.cs, src/ThroughlineBuild.Briefs/DraftBriefBuilder.cs, tests/ThroughlineBuild.Phases.Tests/DraftPhaseTests.cs |
| 02 | draft-template | Templates/draft.md brief content instructing the drafter worker on structure, constraints, output format | 01 | src/ThroughlineBuild.Briefs/Templates/draft.md, tests/ThroughlineBuild.Briefs.Tests/Snapshots/draft-brief.txt |

### Briefs - detail

#### Brief 01: draft-phase

Goal: DraftPhase class that takes free-form text, invokes a worker subprocess via IWorkerAgent, parses the WORKER_RESULT envelope's body field, returns a DraftResult with the rendered ticket body markdown.

Inputs:
- Free-form operator text (string)
- Existing IWorkerAgent for subprocess invocation
- Existing WORKER_RESULT envelope and parser infrastructure

Outputs:
- `DraftPhaseOptions` record:
  ```csharp
  public sealed record DraftPhaseOptions(
      string OperatorText,
      bool Debug
  );
  ```
- `DraftResult` record:
  ```csharp
  public sealed record DraftResult(
      DraftOutcome Outcome,
      string? BodyMarkdown,  // full template-formatted body when Outcome=Ok
      string? FailureReason  // populated when Outcome != Ok
  );
  ```
- `DraftOutcome` enum: `Ok | EmptyInput | WorkerFailed | InvalidWorkerOutput`
- `DraftBriefBuilder` class with `Build(string operatorText) -> string` that:
  - Loads Templates/draft.md
  - Substitutes `{{operator_text}}` variable with the operator's input verbatim
  - Returns the rendered brief
- `DraftPhase.RunAsync(DraftPhaseOptions, CancellationToken)` flow:
  1. Validate operator text is non-empty after trimming; return DraftResult with Outcome=EmptyInput if blank
  2. Build the drafter brief via DraftBriefBuilder
  3. Invoke worker via IWorkerAgent with WorkerOptions configured for this phase (Timeout, DebugCaptureDirectory if Debug=true, Size=Small once sizing lands; for now uses default)
  4. If worker returns non-Ok status: return DraftResult with Outcome=WorkerFailed and FailureReason from worker
  5. Parse the WORKER_RESULT envelope's `body_markdown` field (expected to contain the full template-formatted ticket body)
  6. Validate the body_markdown contains required template sections (title, Type, Description, Acceptance criteria, Out of scope, Notes - or at minimum a title and description); if validation fails return DraftResult with Outcome=InvalidWorkerOutput and FailureReason listing what was missing
  7. Return DraftResult with Outcome=Ok and BodyMarkdown
- Tests:
  - Happy path: drafter returns well-formed body, DraftPhase returns Ok with body
  - Empty input: returns EmptyInput without invoking worker
  - Worker fails: returns WorkerFailed with reason
  - Worker output missing required sections: returns InvalidWorkerOutput
  - Operator text containing newlines, quotes, special chars passes through verbatim to the brief

Acceptance:
- [ ] DraftPhase class exists with RunAsync returning DraftResult
- [ ] Empty input refused without worker invocation
- [ ] Worker failure surfaces as WorkerFailed outcome with FailureReason
- [ ] Invalid worker output (missing required sections) surfaces as InvalidWorkerOutput
- [ ] DraftBriefBuilder loads template and substitutes operator text verbatim
- [ ] Tests pass for all enumerated cases

Notes: DraftPhase is intentionally thin - it just plumbs operator text → worker → body markdown. The drafting smarts live in the template (B02), not in code. This keeps the phase stable as the template evolves through prompt tuning.

The decision to have the worker return `body_markdown` (a single markdown string) rather than structured fields keeps the worker→builder contract simple. Worker emits markdown that matches the build-new input format; DraftPhase passes it through; downstream CLI hands it to the existing NewPhase as if the operator had supplied a file. This avoids a structured→markdown rendering step and keeps the output format consistent with what build-new already accepts.

The "required sections" validation in step 6 is loose for v1: just check that title and description exist. Stricter validation (acceptance criteria present and non-empty, types valid, etc.) can layer on if drafter output drifts. Prompt tuning is the first lever.

OOS:
- Do not implement field-by-field structured output from the worker (single body_markdown string is the contract)
- Do not implement automatic retry on worker failure (caller / CLI handles re-invocation)
- Do not implement caching of drafts (each invocation runs the worker)
- Do not file the ticket inside DraftPhase (that's NewPhase's job; DraftPhase produces the body, CLI sequences to NewPhase)
- Do not modify the existing build-new file-mode behavior
- Do not implement worker size selection (uses default; size landing in multi-agent foundation will naturally pick this up)

#### Brief 02: draft-template

Goal: Templates/draft.md instructs the drafter worker to expand free-form operator text into a structured ticket body matching the build-new template, preserving intent without inventing scope.

Inputs:
- Existing build-new input template (the one printed by `build new --print-template`)
- The `{{operator_text}}` variable from B01

Outputs:
- New template file: src/ThroughlineBuild.Briefs/Templates/draft.md
- Template content covers:
  - **Role:** "You are drafting a ticket body from a free-form operator description. Your job is to expand the operator's text into the standard ticket template format without inventing scope the operator didn't state."
  - **Operator text:** included verbatim via `{{operator_text}}`
  - **Template structure to fill:** the exact section structure of the build-new template (Title, Type, Description, Acceptance criteria, Out of scope, Notes)
  - **Constraints:**
    - Preserve operator intent verbatim where possible
    - Do not invent acceptance criteria the operator did not imply
    - Do not invent out-of-scope items unless the operator explicitly mentioned exclusions
    - When operator text is terse, the description should be terse - do not pad
    - When operator text is ambiguous about scope, note the ambiguity in the description or Notes section ("operator did not specify X; suggest clarifying or accepting default Y")
    - Infer Type from text: "bug" for defects/issues; "feature" for new capability; "task" as default
    - Title should be concise and descriptive, ≤80 chars where possible
    - Use single hyphens, no em-dashes (matches house style)
    - Acceptance criteria are observable outcomes, not implementation steps
  - **Output format:** WORKER_RESULT envelope with a `body_markdown` field containing the complete template-formatted ticket body
  - Example showing terse input → expanded output
  - Example showing ambiguous input → output that flags the ambiguity
- Snapshot test of the rendered brief (with `{{operator_text}}` substituted with a fixture) for regression detection

Acceptance:
- [ ] Templates/draft.md exists at the documented path
- [ ] Template instructs the worker on role, constraints, output format with examples
- [ ] Template uses `{{operator_text}}` substitution variable
- [ ] Snapshot test captures the rendered brief with a fixture operator text
- [ ] Snapshot test passes
- [ ] Template's "no em-dashes, single hyphens" instruction is present

Notes: The two examples in the brief (terse and ambiguous) are load-bearing for output consistency. Without them, the drafter has to guess at the operator's expectations; with them, the drafter has concrete patterns to match. Pick examples that exercise the "don't invent scope" and "flag ambiguity" rules.

The constraint about title length (≤80 chars where possible) is soft - the drafter should aim for concise but not fail validation on a 90-char title. If shorter titles matter for Plane UX, they can be tightened in prompt tuning.

The "infer Type" rule uses task as the safe default. If operators want non-task tickets routinely (e.g., a project that tracks bugs separately), they can refine the input text ("Bug: ...") or use `--review` to edit before filing.

The drafter is not given knowledge of:
- The current Plane project state (no list of existing tickets)
- The code repository (no file reads)
- Prior drafts or operator preferences

This is intentional: the drafter is a stateless text-to-structure transformer. State-aware drafting (e.g., "you've filed similar tickets recently, here's a draft consistent with those") is a future feature, not v1.

OOS:
- Do not give the drafter access to repository or Plane API tools
- Do not implement adaptive prompting based on operator history
- Do not implement multi-language support (English template only)
- Do not include examples that violate the OOS rules ("don't invent scope") - examples are reference behavior
- Do not add tone controls (the constraints already establish direct/concise style)

## Plan B: CLI integration

### Goal

`build new` recognizes free-form text input alongside the existing file-mode input. Invokes DraftPhase for text, NewPhase for files. `--review` flag opens an interactive accept/edit/regenerate/quit loop before filing. Stdin input supported via `-` argument. Default behavior is fire-and-forget: drafter produces body, ticket gets filed immediately.

Briefs are sequential.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | cli-argument-disambiguation | build new arg parsing: file path detection, text mode, stdin (-), --print-template precedence | A | src/ThroughlineBuild.Cli/Commands/NewCommand.cs, tests/ThroughlineBuild.Cli.Tests/NewCommandArgumentTests.cs |
| 04 | review-loop | --review flag opens interactive accept/edit/regenerate/quit loop before filing | 03 | src/ThroughlineBuild.Cli/Commands/NewCommand.cs, src/ThroughlineBuild.Cli/ReviewLoop.cs, tests/ThroughlineBuild.Cli.Tests/ReviewLoopTests.cs |

### Briefs - detail

#### Brief 03: cli-argument-disambiguation

Goal: `build new <arg>` detects whether the argument is a file path (file mode, existing) or free-form text (draft mode, new). Handle stdin via `-` argument. Preserve `--print-template`. Default is fire-and-forget filing after draft.

Inputs:
- Existing NewCommand with file-mode handling
- Shipped CreateTicketAsync / NewPhase

Outputs:
- NewCommand argument parsing logic:
  - `build new --print-template` → print template, exit 0 (existing)
  - `build new <arg>`:
    - If `<arg>` is `-`: read body text from stdin → draft mode
    - Else if `<arg>` is a path that exists as a regular file: file mode (existing)
    - Else: treat `<arg>` as free-form text → draft mode
  - `build new` (no arg): print help, exit non-zero
  - `build new <multiple-args>`: treat as joined-by-space free-form text (so `build new fix the readme` works without quoting)
- Disambiguation safety: when the argument looks like it could have been intended as a file path (contains `/`, `\`, or ends with a known body extension like `.md`/`.txt`) but no file exists at that path, emit a stderr notice: `note: no file at 'foo.md'; treating as draft input. Press Ctrl-C to abort.` Brief pause (~500ms) before proceeding so the operator can abort.
- Draft mode flow:
  1. Run DraftPhase with the operator text
  2. If DraftResult.Outcome != Ok: print FailureReason to stderr, exit non-zero
  3. Pass DraftResult.BodyMarkdown to the existing NewPhase / CreateTicketAsync
  4. Print confirmation: ticket ID, link, brief summary (one line)
- File mode flow: unchanged
- Output on successful draft + file:
  ```
  [drafted] from operator text (3.4s)
  [filed] TLB-148: <title from body>
  https://plane.example.com/.../tickets/TLB-148
  ```
- Tests:
  - File path that exists → file mode
  - Free-form text → draft mode
  - `-` → reads stdin, draft mode
  - Multiple args → joined as draft text
  - Looks-like-path-but-not-found → draft mode with stderr notice
  - --print-template still works
  - No args → help + non-zero exit
  - Draft failure (e.g., DraftPhase returns EmptyInput) → stderr + non-zero exit

Acceptance:
- [ ] File-mode path triggers existing behavior unchanged
- [ ] Text input (single quoted, multi-arg unquoted, or stdin via -) triggers draft mode
- [ ] Looks-like-path-but-not-found case emits stderr notice before proceeding
- [ ] Successful draft + file prints ticket ID and link
- [ ] Failure outcomes from DraftPhase surface with helpful stderr messages
- [ ] --print-template precedence preserved
- [ ] Tests pass

Notes: The "joined-by-space" handling for multi-arg input lets operators skip quoting in interactive use: `build new fix the readme typo on line 42`. POSIX shell glob expansion may interfere if the args contain `*` or `?`, but that's a shell concern - operators can quote when they have special chars.

The "looks like a path but not found" warning is the main footgun mitigation. Without it, a typoed file path silently becomes a (probably nonsensical) draft input. The 500ms pause is short enough not to annoy but long enough for Ctrl-C in interactive use.

For stdin (`-`) mode: read all stdin to EOF, then run DraftPhase with the result. Useful for piping: `cat my-thoughts.txt | build new -`.

OOS:
- Do not implement `--text "..."` or `--draft "..."` explicit-mode flags (disambiguation by argument shape is sufficient)
- Do not implement file-mode bypass for text-looking input (`--force-draft` etc.; if text looks like a file path, the warning + pause is the affordance)
- Do not modify how the body markdown is parsed by NewPhase (the contract is the same; drafter just produces the markdown that NewPhase then files)
- Do not implement output formats other than the human-readable confirmation (no `--json` output for v1)

#### Brief 04: review-loop

Goal: `--review` flag opens an interactive loop after draft completes and before filing. Operator can accept (file), edit ($EDITOR), regenerate (re-run drafter with optional additional context), or quit (abort without filing).

Inputs:
- DraftPhase from Plan A
- Existing CreateTicketAsync
- $EDITOR environment variable (or fallback chain: vim, nano, notepad on Windows)

Outputs:
- `ReviewLoop` class encapsulating the interactive flow:
  ```csharp
  public sealed class ReviewLoop
  {
      public ReviewLoop(DraftPhase draftPhase, IConsole console);
      public Task<ReviewLoopResult> RunAsync(string initialBody, string originalText, CancellationToken ct);
  }

  public sealed record ReviewLoopResult(
      ReviewLoopOutcome Outcome,
      string? FinalBody  // null when Outcome=Aborted
  );

  public enum ReviewLoopOutcome { Accepted, Aborted }
  ```
- Loop logic:
  1. Print the current body to stdout
  2. Prompt: `[a]ccept, [e]dit, [r]egenerate, [q]uit:`
  3. Read single-keystroke input (or full-line for non-tty fallback)
  4. On `a`/`accept`: return ReviewLoopResult(Accepted, currentBody)
  5. On `e`/`edit`: write currentBody to a temp file, invoke $EDITOR on it, re-read the file, update currentBody, loop
  6. On `r`/`regenerate`: prompt for optional additional context ("any extra context for the regenerate? [enter to skip]"), append to original text if provided, re-invoke DraftPhase, update currentBody, loop
  7. On `q`/`quit`: return ReviewLoopResult(Aborted, null)
  8. On unrecognized input: re-prompt
- NewCommand integration:
  - When `--review` is set: after DraftPhase succeeds, instantiate ReviewLoop, run it with the body
  - If ReviewLoopResult.Outcome = Accepted: file with FinalBody
  - If Aborted: print "no ticket filed" and exit 0 (clean abort, not an error)
- $EDITOR resolution: env var if set; else try `vim`, `nano`, `code --wait`, `notepad.exe` (Windows) in order; else error
- Tests:
  - Accept path → final body matches initial draft, Outcome=Accepted
  - Edit path → file written to temp, editor invoked, body re-read after editor exits (use a mock editor for tests)
  - Regenerate path → DraftPhase invoked again with appended context, body updated
  - Quit path → Outcome=Aborted, no file write
  - Multiple iterations: edit then regenerate then accept

Acceptance:
- [ ] --review flag opens the loop after draft completes
- [ ] Accept files the ticket with the current body
- [ ] Edit invokes $EDITOR and updates the body
- [ ] Regenerate re-invokes DraftPhase with optional additional context
- [ ] Quit aborts without filing (clean exit 0)
- [ ] $EDITOR resolution works across platforms (env var, fallback chain)
- [ ] Tests pass for all loop paths

Notes: Single-keystroke input is preferred for interactive flow. On non-tty stdin (e.g., scripted), fall back to full-line input so automation still works.

The "additional context for regenerate" prompt is the operator's mechanism for steering the drafter without leaving the loop. Common case: operator sees the draft is too narrow and types "also cover the legacy code path" - the regenerated draft incorporates that. Less common but valuable.

The temp-file-for-editor pattern uses Path.GetTempFileName() with a .md extension hint so editors with markdown support behave well. The file is deleted after the loop completes regardless of accept/abort.

For Windows: $EDITOR is rare. Most operators on Windows have either set it to vim/nano/code or expect notepad. The fallback chain handles all three.

A regenerate that fails (DraftPhase returns non-Ok) stays in the loop with the original body and prints the failure reason - so the operator can edit instead of being kicked out.

OOS:
- Do not implement diff display between regenerate iterations (operator sees full body each time; diffs are noise for short bodies)
- Do not implement undo/history within the loop (last body wins; if operator wants to recover an earlier version, they regenerate or quit)
- Do not implement automatic re-validation after edit (operator can save garbage; they're responsible for what they file)
- Do not persist loop state across invocations (each --review is fresh)
- Do not implement --review with file-mode input (file mode is already explicit; if operator wants to review a file's body, they read the file directly)

## What done looks like

`build new "developer widget that bars baz"` files a ticket. No template, no editor, no four-step workflow. The drafter expands the terse input into a structured body, NewPhase files it, the operator gets back a ticket ID and link in under 10 seconds.

Operators who want safety can use `build new --review "..."` to inspect and edit before filing. The accept/edit/regenerate/quit loop covers the cases where the drafter goes sideways without requiring a full re-run from the shell.

The existing file-mode and `--print-template` paths are unchanged: operators with complex tickets still write a body file and pass the path; the print-template path is still there for reference.

Architecturally, DraftPhase is a clean addition: a thin orchestrator over IWorkerAgent that produces ticket-body markdown. When multi-agent foundation + sizing land, the drafter naturally becomes Small-sized (Haiku-class) for whichever agent is configured. Until then it uses the default agent. Either way, the operational cost is pennies per draft.

The drafter's quality is a function of Templates/draft.md, not code. Tuning happens by editing the template and re-running. The constraints in the template (preserve intent, don't invent scope, flag ambiguity) are the levers; the examples are the anchors. If the drafter drifts in practice, the template gets tightened.

After this op-doc lands, the `/tn`-equivalent UX is back. Combined with build-rework (when it lands) closing the dead-end on Rework verdicts, the operator workflow approaches what claude-config had - but on the new pipeline's cost basis.