# Operation: brief-template-externalization

Move the worker brief content out of C# string interpolation into externalized markdown templates loaded at build time via `EmbeddedResource`. Add a `[project]` section to `.build/config.toml` carrying stack metadata and optional freeform notes that the plan brief can reference. Enrich `plan.md` with the investigation behaviors, output structure, and discipline passes that the prior comparison run (SURCC-1 vs SURLF-1) identified as the functional equivalence gap.

## Why this exists

Two related problems surfaced from the first dogfood comparison.

First, the new pipeline's planning output is substantively thinner than the old pipeline's. Old `/ticket-investigate` produced an Investigation section with file-and-line specifics, a Proposed Solution with rationale, an Implementation Plan with Design decisions / Escalation rules / Out of scope / Agent size sections, plus self-applied Subtract and Rubber-duck passes that catch executability defects before they ship. The new pipeline produces a checklist that restates the ticket's acceptance criteria as steps. The cost ratio (~13x cheaper) does not survive the quality comparison; the new system is partly cheaper because it is doing meaningfully less work.

Root cause is the brief, not the architecture. `PlanBriefBuilder.Build` currently asks the worker to "plan the work for this ticket: produce an implementation plan plus risk and size assessment." It does not ask for investigation behaviors, design rationale, escalation rules, or self-checks. The old slash-command prompt corpus did, by default.

Second, the briefs live as C# string interpolation inside `*.BriefBuilder.cs` source files. Editing a prompt requires editing C# code and recompiling. That makes iteration slow and prompt-only diffs hard to read. As the briefs grow (this op-doc roughly 10x's the plan brief content), the situation gets worse.

Both problems share a fix: externalize the brief templates as `.md` files, load them at build time via embedded resources, substitute typed variables at runtime. Refactor first (no behavior change), then enrich content (intentional behavior change against the new editable surface).

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Template externalization | - | M |
| B    | Plan brief enrichment | A | M |

Plan A is the mechanical refactor: introduce a small `TemplateLoader` and substitution helper, move existing brief content from C# strings into `.md` files registered as `EmbeddedResource`, restructure each brief builder to load and substitute. No behavior change; the produced `Brief.Instruction` is byte-equivalent to today. Plan B is the intentional behavior change: extend `.build/config.toml` with a `[project]` section, wire a `ProjectContext` record through to the brief builders, and rewrite `plan.md` with the enriched investigation behaviors and output structure. Briefs are sequential within each plan and across plans.

## Plan A: Template externalization

### Goal

Two pieces of new infrastructure (`TemplateLoader` helper + `Substitute` extension) and one mechanical refactor migrating all three brief builders (Plan, Implement, Review) from interpolated C# strings to externalized `.md` templates loaded via `EmbeddedResource`. The output of each brief builder is byte-equivalent to today; only the storage location changes.

Brief sequence: B01 introduces the helpers. B02 migrates the three builders and adds the templates. Sequential.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | template-loader | `TemplateLoader` static class + `Substitute` extension method in `ThroughlineBuild.Briefs` | - | src/ThroughlineBuild.Briefs/TemplateLoader.cs, src/ThroughlineBuild.Briefs/TemplateExtensions.cs, tests/ThroughlineBuild.Briefs.Tests/TemplateLoaderTests.cs, tests/ThroughlineBuild.Briefs.Tests/SubstituteTests.cs |
| 02 | externalize-templates | Move existing brief content from C# strings into `.md` files registered as `EmbeddedResource`; refactor the three brief builders to load and substitute | 01 | src/ThroughlineBuild.Briefs/Templates/plan.md, src/ThroughlineBuild.Briefs/Templates/implement.md, src/ThroughlineBuild.Briefs/Templates/review.md, src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs, src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs, src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs, src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj, tests/ThroughlineBuild.Briefs.Tests/PlanBriefBuilderTests.cs, tests/ThroughlineBuild.Briefs.Tests/ImplementBriefBuilderTests.cs, tests/ThroughlineBuild.Briefs.Tests/ReviewBriefBuilderTests.cs |

### Briefs - detail

#### Brief 01: template-loader

Goal: A small `TemplateLoader` static class that reads a named template from the assembly's embedded resources, plus a `Substitute` extension method that replaces `{{key}}` placeholders with values from a `Dictionary<string, string>`. Both AOT-compatible. Both unit-tested.

Inputs:
- `System.Reflection` (for `Assembly.GetManifestResourceStream`)
- The `ThroughlineBuild.Briefs` project (currently contains the three builders)

Outputs:
- `src/ThroughlineBuild.Briefs/TemplateLoader.cs` with:

```csharp
public static class TemplateLoader
{
    public static string Load(string templateName);
}
```

  `Load` looks up the template by name (e.g. `"plan.md"`), reads from the assembly's embedded resources via a stable resource-name convention (`ThroughlineBuild.Briefs.Templates.{templateName}` or whatever the csproj produces), and returns the template content as a string. If the template is not found, throws `InvalidOperationException` with a clear message listing what templates ARE available (loaded once at startup and cached).

- `src/ThroughlineBuild.Briefs/TemplateExtensions.cs` with:

```csharp
public static class TemplateExtensions
{
    public static string Substitute(this string template, IReadOnlyDictionary<string, string> variables);
}
```

  `Substitute` replaces every `{{key}}` occurrence in the template with `variables[key]`. If a `{{key}}` appears in the template but is not in the dictionary, throw `InvalidOperationException` naming the missing key. This is deliberate: typos in builder code surface as runtime exceptions in tests, not as malformed briefs delivered to workers.

- `tests/ThroughlineBuild.Briefs.Tests/TemplateLoaderTests.cs` covering:
  - Load a known template (use a small test fixture template registered as embedded resource in the test project)
  - Load with unknown name throws with the expected message listing available templates
  - Cache behavior: two consecutive loads return the same content without re-reading

- `tests/ThroughlineBuild.Briefs.Tests/SubstituteTests.cs` covering:
  - Simple substitution: `"Hello {{name}}"` with `{name: "world"}` returns `"Hello world"`
  - Multiple variables substituted in one pass
  - Same variable appearing multiple times substituted everywhere
  - Variable in template but not in dictionary throws naming the missing key
  - Variable in dictionary but not in template is silently ignored (forward-compat: builders can supply extras safely)
  - Empty-string variable value substitutes as empty (used for optional sections per brief builder convention)
  - Variables containing `{{...}}` markup in their value are NOT re-substituted (no recursive substitution; matches single-pass semantics)

Acceptance:
- [ ] `TemplateLoader.Load(name)` returns the embedded resource content for a known template name
- [ ] `Load` with an unknown template throws `InvalidOperationException` with a message naming the unknown template and listing available ones
- [ ] `Substitute` replaces all `{{key}}` occurrences in one pass
- [ ] Missing key throws; extra key in dictionary is harmless
- [ ] No recursive substitution; values containing `{{...}}` are inserted literally
- [ ] AOT analyzer reports no warnings on these files
- [ ] xUnit tests pass with a fixture template registered as embedded resource in the test project

Notes: The substitution helper is intentionally minimal. No conditionals, no loops, no filters. Builders construct conditional sections in C# (concatenating a heading + body into a single variable when a section should appear, or passing the empty string when it should not). Keeps the substituter ~20 lines, AOT-safe, and free of third-party template-engine dependencies.

Resource-name convention is whatever MSBuild produces for `<EmbeddedResource Include="Templates\plan.md" />`. The conventional name is `ThroughlineBuild.Briefs.Templates.plan.md`. Verify the actual generated name with `assembly.GetManifestResourceNames()` and pin the convention.

OOS:
- Do not add Handlebars, Scriban, or any third-party template engine
- Do not implement conditional sections (`{{#if}}`) or loops (`{{#each}}`) - those go in the builders
- Do not implement nested variable substitution
- Do not implement filters or transforms inside the template syntax
- Do not implement template inheritance or includes
- Do not load templates from disk at runtime - embedded resources only

#### Brief 02: externalize-templates

Goal: Move the existing brief content from C# strings into three `.md` files (`plan.md`, `implement.md`, `review.md`), register them as `EmbeddedResource` in the csproj, and refactor each brief builder to use `TemplateLoader.Load(...).Substitute(...)`. The `Brief.Instruction` each builder produces must be byte-equivalent to today; this is a mechanical refactor.

Inputs:
- The three existing brief builders in `src/ThroughlineBuild.Briefs/`
- `TemplateLoader` and `Substitute` from B01
- The csproj at `src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj`

Outputs:
- `src/ThroughlineBuild.Briefs/Templates/plan.md`, `implement.md`, `review.md` containing the current brief content with `{{variable}}` placeholders where the C# code currently interpolates. Whitespace, line breaks, and content order preserved verbatim.
- Updated csproj with `<EmbeddedResource Include="Templates\*.md" />`
- `PlanBriefBuilder`, `ImplementBriefBuilder`, `ReviewBriefBuilder` each restructured to:
  1. Build a `Dictionary<string, string>` of variables from their typed inputs
  2. Load the appropriate template via `TemplateLoader.Load`
  3. Substitute via the extension method
  4. Return the resulting `Brief` record (other fields unchanged)
- Each builder's variable dictionary uses `snake_case` keys (matching event-log Data conventions from op-05)
- Existing builder tests updated to verify the new output is byte-equivalent to a captured snapshot of the prior output; add the snapshot fixture(s) under `tests/ThroughlineBuild.Briefs.Tests/Snapshots/`

Acceptance:
- [ ] `plan.md`, `implement.md`, `review.md` exist under `src/ThroughlineBuild.Briefs/Templates/` and are registered as `EmbeddedResource`
- [ ] Each brief builder's produced `Brief.Instruction` is byte-equivalent to the snapshot captured before the refactor (snapshot fixture comparison; differences fail the test loudly)
- [ ] No interpolated multi-line strings remain in the brief builder `.cs` files; the only strings should be dictionary keys and short labels
- [ ] All variable keys are `snake_case`
- [ ] `dotnet build` succeeds across the solution
- [ ] `dotnet publish -r <rid> -c Release` still produces a working native AOT binary (templates ship inside the binary as embedded resources)
- [ ] All existing brief builder tests pass (they will, since the refactor is byte-preserving)
- [ ] A new test per builder verifies the loaded template is one of the registered embedded resource names (catches resource-name typos)

Notes: Capture the snapshot fixtures BEFORE the refactor by running the existing tests against the existing code and saving the produced `Brief.Instruction` strings to disk. Then refactor, and assert the new output matches the saved snapshot byte-for-byte. This is the "no behavior change" guarantee made auditable. If the snapshot does not match, the refactor introduced a difference (e.g. whitespace, line ending) and needs to be fixed before this brief is acceptable.

If the existing brief builders use any logic that does not translate cleanly to "build variables dict + substitute" (e.g. complex conditional sections), the builder is responsible for producing the conditional content as a string variable (e.g. building a `parent_context_section` that is either an empty string or a fully formatted block) BEFORE substitution. The template stays pure substitution.

OOS:
- Do not change any builder's output content in this brief; refactor only
- Do not enrich plan.md, implement.md, or review.md content; that is Plan B's work for plan.md, and a future op-doc for implement.md and review.md
- Do not introduce template inheritance or shared partials between templates
- Do not move the brief builder files to a different namespace or project
- Do not change the public signature of any brief builder's `Build` method

## Plan B: Plan brief enrichment

### Goal

Add a `[project]` section to `.build/config.toml` carrying stack metadata and an optional `notes_file` reference. Load into a `ProjectContext` record and plumb through to the brief builders. Rewrite `plan.md` with the enriched investigation behaviors, output structure with all sections (Investigation, Proposed Solution, Implementation Plan with Design decisions / Escalation rules / Out of scope / Agent size), and discipline passes (Subtract, Rubber-duck). Add tests verifying the enriched output structure appears in the produced `Brief.Instruction`.

Brief sequence: B03 first (config plumbing must land before the template can reference project variables). B04 next (template content + PlanBriefBuilder variable wiring).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | project-config | Add `[project]` TOML section + `ProjectContext` record + `notes_file` inlining; plumb through to all three brief builders | - | src/ThroughlineBuild.Cli/Config.cs, src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Briefs/ProjectContext.cs, src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs, src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs, src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs, src/ThroughlineBuild.Phases/PlanPhase.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs, .build/config.toml.example, tests/ThroughlineBuild.Cli.Tests/ProjectConfigTests.cs, tests/ThroughlineBuild.Briefs.Tests/ProjectContextTests.cs |
| 04 | enrich-plan-template | Rewrite `plan.md` with investigation behaviors, output structure, discipline passes; update PlanBriefBuilder to supply the new variables | 03 | src/ThroughlineBuild.Briefs/Templates/plan.md, src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs, tests/ThroughlineBuild.Briefs.Tests/PlanBriefBuilderTests.cs, tests/ThroughlineBuild.Briefs.Tests/Snapshots/plan-enriched.txt |

### Briefs - detail

#### Brief 03: project-config

Goal: Extend `.build/config.toml` with a `[project]` section that carries stack metadata (language, framework, package manager, common commands) and an optional `notes_file` reference. Load into a `ProjectContext` record. When `notes_file` is set and the file exists, load its content as `Notes`. Pass the `ProjectContext` through to all three brief builders via an updated `Build(...)` signature.

Inputs:
- The existing TOML loader at `src/ThroughlineBuild.Cli/Config.cs`
- The existing brief builder `Build` method signatures
- The existing phase classes that call the brief builders (`PlanPhase`, `ImplementPhase`, `ReviewPhase`)

Outputs:
- Updated `.build/config.toml` schema with a new optional `[project]` section:

```toml
[project]
language = "typescript"             # informational; used in the brief
framework = "react-vite"            # informational
package_manager = "npm"             # informational
build_command = "npm run build"     # used by the brief to suggest verification commands
test_command = "npm test"
install_command = "npm install"
dev_command = "npm run dev"
plane_project_url = "https://plane.example.com/workspace/browse/PROJ/"
notes_file = ".build/project.md"    # optional; relative to repo root
```

  All fields are optional. Missing fields are passed to the brief builders as empty strings; the brief template's builder logic decides whether to include an optional section.

- New record `src/ThroughlineBuild.Briefs/ProjectContext.cs`:

```csharp
public record ProjectContext(
    string Language,
    string Framework,
    string PackageManager,
    string BuildCommand,
    string TestCommand,
    string InstallCommand,
    string DevCommand,
    string PlaneProjectUrl,
    string Notes);  // inlined from notes_file if set and file exists; else empty
```

  All string fields default to empty (not null) so brief builders never have to null-check.

- `Config.cs` parses the `[project]` TOML section. If `notes_file` is set, attempts to read the file (resolved relative to the config file's directory). If the file is missing, log a warning but proceed with empty Notes (do not fail the run; missing notes is not an error).
- `PlanPhase`, `ImplementPhase`, `ReviewPhase` accept `ProjectContext` (alongside `BuildOptions`) and pass it to their respective brief builders' `Build(...)` signatures
- `PlanBriefBuilder.Build`, `ImplementBriefBuilder.Build`, `ReviewBriefBuilder.Build` gain a `ProjectContext project` parameter (after the existing parameters). The builders add `project_*` keys to the substitution dictionary (e.g. `project_language`, `project_test_command`, `project_notes`). For B03's scope, the templates may or may not reference these keys yet; Plan B's B04 rewrites `plan.md` to actually use them. The implement and review templates can ignore the new variables (extra dict keys are silently ignored per B01's `Substitute` contract).
- `.build/config.toml.example` updated to document the new `[project]` section with comments explaining each field
- `tests/ThroughlineBuild.Cli.Tests/ProjectConfigTests.cs` covering:
  - Config with full `[project]` section loads all fields
  - Config with no `[project]` section produces a `ProjectContext` with all empty strings
  - Config with `notes_file` set and file present loads file content into `Notes`
  - Config with `notes_file` set and file missing produces empty `Notes` and emits a warning (verified via captured stderr or a test-injected logger)
- `tests/ThroughlineBuild.Briefs.Tests/ProjectContextTests.cs` covering each builder accepting the `ProjectContext` parameter without altering its existing output (snapshot match against the Plan A B02 fixtures, since the templates have not yet been changed)

Acceptance:
- [ ] `[project]` section parsed from TOML with all fields optional
- [ ] `ProjectContext` record exists with all-string-fields-default-to-empty contract
- [ ] `notes_file` content inlined when present; missing file produces empty `Notes` + warning
- [ ] All three brief builders' `Build` methods accept a new `ProjectContext` parameter
- [ ] All three phase classes pass `ProjectContext` through to their brief builders
- [ ] `.build/config.toml.example` documents the new section
- [ ] Existing brief builder snapshot tests still pass (the new parameter is supplied but does not affect output until B04 rewrites plan.md)
- [ ] Missing `[project]` section does not break existing configs (backward compatible)

Notes: `notes_file` resolves relative to the config file's directory, not the current working directory. This matches the existing convention for other relative paths in the config. If the path is absolute, use as-is.

The `ProjectContext` record is in `ThroughlineBuild.Briefs` (not `Contracts`) because it is brief-builder-specific. If a future op-doc needs to surface it elsewhere (e.g. the CLI for diagnostic output), it can move to `Contracts` at that time.

OOS:
- Do not rewrite `plan.md`, `implement.md`, or `review.md` content in this brief - that is B04's work for plan.md, and future op-docs for the others
- Do not validate the values in `[project]` (e.g. do not check that `test_command` is a real shell command) - the worker will discover invalid commands when it tries to run them
- Do not implement multiple notes files (one optional `notes_file` only)
- Do not include the contents of files referenced from `notes_file` (no transitive includes)
- Do not implement a `--project` CLI flag for overriding the config-loaded `ProjectContext` (could be a future v1.1 add)

#### Brief 04: enrich-plan-template

Goal: Rewrite `src/ThroughlineBuild.Briefs/Templates/plan.md` with the investigation behaviors, output structure, and discipline passes documented in the comparison-gap analysis. Update `PlanBriefBuilder` to supply the new variables (including the `{{project_notes_section}}` conditional wrap). Add tests verifying the enriched output structure appears in the produced `Brief.Instruction`.

Inputs:
- The current `plan.md` template (post-B02 externalization; byte-equivalent to the original C# string)
- The reference canonical template content from the comparison-gap analysis (the implementing agent reads this from `docs/op-docs/plan-brief-template-draft.md` or wherever the operator has placed it; the content is the canonical specification for what the new `plan.md` should contain)
- The `ProjectContext` record from B03

Outputs:
- Replaced `src/ThroughlineBuild.Briefs/Templates/plan.md` with the enriched content. Substantive structure:
  - Ticket context section (id, title, type, description)
  - Repository context section (main_sha, top_level_entries)
  - Optional project notes section (wrapped in builder-supplied `{{project_notes_section}}` variable; empty string when no notes; full heading + content when notes are present)
  - "Your job" section explaining investigation depth expectations
  - Subsections on understanding project context, deep-diving code, investigation depth by ticket type (bugs, features, refactors), verifying environment state, identifying regression risks
  - Output structure specification (the full HTML template with Investigation / Proposed Solution / Implementation Plan including Relevant files / Steps / Verification / Design decisions / Escalation rules / Out of scope / Agent size)
  - Plane render rule (single-line list content)
  - Discipline passes (Subtract pass, Rubber-duck pass) - both mandatory, both with concrete checks
  - Agent size inference heuristic (S/M/L by file and step counts)
  - Invalid-or-already-fixed discovery semantics (return `Status = Escalate` with `FailureReason`)
  - WORKER_RESULT envelope specification with the bare-marker format from op-05
  - Rules section (investigation only, no code changes, no branches, no marker-comment embedding in plan_html)

- Updated `PlanBriefBuilder.Build` to supply the new variables in the substitution dictionary. New keys (snake_case):
  - `type` (from `Ticket.Type`)
  - `project_notes_section` (conditional: empty string when `ProjectContext.Notes` is empty/whitespace, else a formatted block with a `## Project notes` heading and the notes content)
  - Any additional `project_*` keys the template references (e.g. `project_language`, `project_test_command`, etc., if the template surfaces them directly)
- Updated `tests/ThroughlineBuild.Briefs.Tests/PlanBriefBuilderTests.cs` covering:
  - Produced `Brief.Instruction` contains the expected literal headings: `## Your job`, `### 1. Understand the project context`, `### 5. Identify regression risks`, `## Output structure`, `## Discipline passes`, `### Subtract pass`, `### Rubber-duck pass`, `## Agent size inference`, `## WORKER_RESULT envelope`, `## Rules`
  - Produced `Brief.Instruction` contains the WORKER_RESULT envelope spec naming the four metadata fields (`plan_html`, `risk_label`, `size_label`, `planned_at_sha`)
  - When `ProjectContext.Notes` is empty, the output does NOT contain a `## Project notes` heading (substitute-to-empty path)
  - When `ProjectContext.Notes` is non-empty, the output DOES contain a `## Project notes` heading followed by the notes content
  - A new snapshot fixture at `tests/ThroughlineBuild.Briefs.Tests/Snapshots/plan-enriched.txt` captures the full expected output for a representative ticket+repo+project context; the test asserts byte-equivalence against the snapshot. Snapshot updates require deliberate test failures + re-capture.
- The B02 snapshot fixture (`plan-original.txt` or whatever was captured) is RETAINED for historical reference but is no longer the active comparison target. Optionally move it to a `Snapshots/historical/` subdirectory with a README explaining its purpose.

Acceptance:
- [ ] `plan.md` is replaced with the enriched content
- [ ] Enriched output contains all listed sections (Investigation / Proposed Solution / Implementation Plan with all subsections / Discipline passes / Agent size / WORKER_RESULT envelope / Rules)
- [ ] `{{project_notes_section}}` is supplied by the builder as either empty string or a full formatted block
- [ ] PlanBriefBuilder supplies all variables the template references; missing-key exception (per B01 contract) does not fire
- [ ] New snapshot fixture captures the enriched output; test asserts byte-equivalence
- [ ] Instruction byte length grows roughly 5-10x from the pre-enrichment baseline (rough cap ~3000 tokens; document the new size in the brief's test output)
- [ ] All existing PlanPhase and PlanBriefBuilder tests pass after snapshot updates
- [ ] Running `build plan` against a real ticket produces a `plan_html` in Plane containing the expected enriched sections (manual verification, not automated)

Notes: The canonical template content is the reference document. Port verbatim with `{{variable}}` substitution syntax. Where the reference uses pseudo-variables like `{ticket_id}` or `{S|M|L}`, decide whether each is:
- An actual substitution variable (use `{{ticket_id}}`) - applies to ticket and repo data
- A literal value the worker chooses (use `{S|M|L}`) - applies to placeholders the worker fills in its output

Treat the reference's pseudo-variables as literals unless they appear in the brief builder's known variable set.

The `{{project_notes_section}}` variable's content is constructed in PlanBriefBuilder, not in the template. When `ProjectContext.Notes` is empty or whitespace, the builder supplies `""`. When non-empty, the builder supplies `"## Project notes\n\n" + notes + "\n"`. The template just has `{{project_notes_section}}` on its own line at the appropriate spot.

Do NOT enrich `implement.md` or `review.md` in this brief. Those templates remain byte-equivalent to their Plan A B02 externalized form. A future op-doc enriches them once a comparison run against a real coding ticket surfaces the specific gaps.

Resist the urge to add the claude-config slash-command mechanics (state gate checks, label updates, plane-rest calls, multi-ticket mode, refresh mode, parent-batch mode, sizing-metrics writes). Those are orchestrator concerns handled by `PlanPhase`. The brief tells the worker what to investigate and how to structure the output; the orchestrator handles state transitions and Plane writes.

OOS:
- Do not enrich `implement.md` or `review.md` content (separate future op-doc; this op-doc is plan brief only)
- Do not include in `plan.md` any slash-command mechanics from claude-config's `/ti` (state gate checks, label updates, plane-rest calls, multi-ticket mode, refresh mode, parent-batch mode, sizing-metrics writes) - these are orchestrator concerns handled by PlanPhase
- Do not implement a `{{#if}}` conditional block in the substitution syntax - section-presence is handled in the builder via "empty string vs full section" variable values
- Do not add new metadata fields to the `WORKER_RESULT` envelope beyond what op-05 / op-03 already specify (`plan_html`, `risk_label`, `size_label`, `planned_at_sha`)
- Do not change the `PlanResult` record shape or `PlanPhase` step ordering
- Do not include hardcoded model names (haiku, sonnet, opus, claude-*) in the template - use S, M, L only when referencing the size taxonomy

## What done looks like

After this op-doc lands, the worker brief content lives in three `.md` files under `src/ThroughlineBuild.Briefs/Templates/`, ships embedded in the AOT binary, and is loaded + substituted at runtime via a small typed helper. Editing a prompt is a `.md` file edit followed by a rebuild; no C# string interpolation to navigate.

`.build/config.toml` gains a `[project]` section carrying stack metadata and an optional `notes_file` reference. The brief builders receive a `ProjectContext` record and surface its fields to the templates as `{{project_*}}` substitution variables.

The `plan.md` template asks the worker for substantive investigation behaviors: read project context, deep-dive code with specific file/line citations, verify environment state, identify regression risks, propose with rationale, document design decisions and escalation rules and out-of-scope, run Subtract and Rubber-duck passes before returning. The output structure produces an HTML block with the same shape the old `/ti` slash command produced, modulo the marker comment (which the orchestrator posts separately).

Running `build plan TICKET-X` against a real ticket on the survey-lf side should now produce a `plan_html` in Plane containing Investigation, Proposed Solution, Implementation Plan with Design decisions / Escalation rules / Out of scope / Agent size sections - functionally equivalent to the old `/ti` output, at a fraction of the token cost the old system spent.

The cost-comparison narrative becomes honest: the new system is cheaper at equivalent quality, not cheaper because it is doing less work. The expected post-enrichment ratio is ~5-8x cheaper than the old system, down from the misleading ~13x measured before the gap was closed.

Implement and review brief templates remain in their Plan A B02 byte-equivalent state. Their enrichment lives in a future op-doc scoped against a real coding ticket comparison.