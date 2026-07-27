# Op-Doc Format Spec

An op-doc is a planning artifact that drives agent orchestration in the Throughline Build ticket workflow. When told "turn this into an op-doc," produce a document that matches this spec exactly. Op-docs live in `docs/op-docs/` and are fed to the scaffold and chain workflows. This guide is the single source for the format, chain execution contract, and canonical example.

**Op-doc vs. runbook:** A runbook is reusable reference for a recurring procedure ("how to rotate the certs"). An op-doc is a one-time, bounded operation with a defined done state ("migrate X to Y - here is what done looks like, here are the briefs"). If you find yourself writing a reusable step-by-step procedure, stop - that is a runbook, not an op-doc.

---

## Document structure (in order)

### 1. Title

```
# Operation: {kebab-case-slug}
```

The exact text `# Operation:` followed by a single lowercase kebab slug is required. The slug is one token matching `^[a-z][a-z0-9-]*$` (lowercase letters, digits, hyphens) - it is the operation's stable id and becomes the `Operation: {slug}` ticket title, so it is not a sentence.

The descriptive, human-readable title does NOT belong on this line - it is the lead paragraph immediately below. Never append title words to the slug:

- Right: `# Operation: batch-implement`
- Wrong: `# Operation: batch-implement cohesive ticket groups`

Do not use `# OP:`, `# Op-Doc:`, or any other prefix - the parser matches this string literally.

### 2. Lead paragraph (no heading)

One tight paragraph immediately after the title. What changes, what problem it solves, and - if there are multiple sub-changes - the key items. This is the executive summary. Write it as prose: "Replace X with Y so that Z." Under 100 words. No bullets. No heading.

### 3. `## Why this exists`

Narrative context: the incident or observation that motivated the work, why the current state is unacceptable, and any strategic timing notes. This is the WHY, not a requirements list. Two to four paragraphs of prose. No bullets. Do not repeat the lead paragraph.

### 4. `## Dispatch order`

A table of every plan in this op-doc with its dependencies and effort:

```
| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Name of plan A | - | M |
| B    | Name of plan B | A | M |
```

Follow the table with one or two sentences explaining the sequencing rationale. For a single-plan op-doc, one row, then "Single plan."

Effort: S (1-3 briefs, low blast radius), M (4-6 briefs or moderate cross-cutting change), L (7+ briefs, architectural change, or high blast radius).

Dependencies are load-bearing. The chain runs tickets one at a time in the order implied by the declared dependencies, and scaffold writes only those declared edges to the ticket backend. Declare every real dependency. An omitted dependency can produce a wrong-order run or cause two briefs to create the same artifact independently.

### 5. `## Plan {letter}: {name}` (one per plan)

Each plan section contains exactly three subsections in order:

**`### Goal`** - One paragraph. The state the system is in after this plan lands. "After this plan, [observable state]." Not a deliverables list.

**`### Briefs`** - Summary table:

```
| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | first-brief-slug | One-line intent | - | src/Path/File.cs, tests/ |
| 02 | second-brief-slug | One-line intent | 01 | src/Path/Other.cs |
```

If two briefs create or modify the same artifact, order them and declare the dependency. Intra-plan dependencies use brief numbers. Cross-plan dependencies are declared with plan letters in the Dispatch order table because cross-plan brief-to-brief dependencies are not expressible. Keep tightly coupled briefs in the same plan when plan-level ordering is too coarse.

**`### Briefs - detail`** - One subsection per brief (see Per-brief rules below).

### 6. `## What done looks like`

**This is the last section in the document, after all Plan sections.** Do not place it before the briefs or between plans.

One prose paragraph (no bullets) describing the operator-observable end state after everything in this op-doc lands. Written from the operator's perspective: "A `build chain` invocation where..." Not a list of what was built. Closes the loop on the lead paragraph's promise.

---

## Per-brief rules

Each brief follows this structure in order:

**`#### Brief {NN}: {slug}`**

**Goal:** One paragraph. What the system does after this brief lands and why it matters. Not a deliverables list.

**Inputs:** What the implementer reads before starting. Name specific files with paths, specific line ranges if known, and specific prior-brief outputs if this brief depends on another. "Investigate the area" is not an input. Prose or short bulleted list.

**Outputs:** Bulleted list of concrete artifacts: new types, modified behaviors, CLI flags, doc sections, tests. Each bullet is specific - names the file or the exact behavior change. Not abstract ("better error handling"). Concrete ("A post-condition assertion in ShipPhase that emits a clear failure if HEAD is detached after ff-merge").

**Acceptance:** Checkboxes. Each is independently verifiable - an operator can confirm each box without running the full suite. If the project has a release gate that a green local test run does not exercise - a production build, an ahead-of-time/native compile, a type-check, a bundle step, a packaged-import smoke - include a checkbox for it on any brief that could plausibly break it (new serialized types, new dependencies, generated code). See the project-gate convention under Style rules.

**Notes:** Design rationale, constraints the implementer must respect, tradeoffs already decided. Written in full paragraphs. Does not repeat what Outputs already states. Does not say "do X" - says "the reason X was chosen over Y is..." or "this constraint exists because..."

**OOS:** Short-phrase list of things explicitly not in this brief. Reference the plan/brief that owns each deferred item where applicable.

---

## Chain execution contract

Scaffolded tickets are promoted to Ready and implemented directly. No plan worker re-investigates the work or fills gaps after scaffolding, so the op-doc itself is the implementation plan. Each brief's Goal, Inputs, Outputs, Acceptance, Notes, and OOS must be sufficient for an implementer working only from that ticket.

The chain executes tickets sequentially from the dependency graph. There is no parallel execution that might accidentally satisfy an undeclared dependency. Scaffold encodes only the declared edges, and the chain reads those edges back as its ordering source. A missing edge is an incorrect execution contract, not a missed optimization.

Each implement brief receives carried-forward context from prior tickets, including touched files and the commit range. A dependent brief may reference an earlier brief's outputs instead of repeating them, but only when the dependency is declared. If cross-plan plan-level ordering is not precise enough, keep the dependent briefs in the same plan.

---

## Style rules

- No em-dashes anywhere. Use plain hyphens (`-`).
- File paths in brief tables and Inputs are specific: `src/ThroughlineBuild.Phases/ShipPhase.cs`, not `src/ (various)`.
- Briefs-table Deps column: `-` for no intra-plan deps or brief number(s) for intra-plan deps. Declare cross-plan dependencies with plan letters in the Dispatch order table.
- Brief slugs: lowercase kebab-case, 3-6 words.
- Plan letters: A, B, C. Brief numbers: 01, 02, 03 (continuous across plans).
- Project release gate: most stacks have a verification step that a passing local test suite does not catch - the build/compile/type-check/bundle/package step that only fails outside the unit run. Name that gate once for the target project, then add a `<gate> succeeds` checkbox to Acceptance for any brief that could break it. The gate is stack-specific, not universal: a C# Native AOT project (such as the Throughline Build repo this spec lives in) uses `AOT publish succeeds` for any brief that registers new types in a source-gen JSON context; a TypeScript project might use `tsc --noEmit passes` or `production build succeeds`; a Python project `the packaged entrypoint imports cleanly`. Pick the gate that matches the stack, or omit this checkbox entirely if the project has no gate beyond its tests.
- The lead paragraph is complete prose, not a sentence fragment.
- "Why this exists" and "What done looks like" are prose paragraphs, not bullets.
- Goal sections (plan-level and brief-level) are one paragraph each.
- Notes sections do not contain bullet lists - prose only.
- Briefs are implementation-ready. Do not assume a later planning pass will discover files, make design decisions, or fill gaps.

---

## Complete example

This canonical example is a valid op-doc for a C# Native AOT project. Its stack-specific paths, source-generated JSON requirement, and `AOT publish succeeds` gate illustrate the conventions; substitute the paths and release gate appropriate to the target project.

<!-- canonical-example-start -->
```markdown
# Operation: cli-build-version-embedding

Add a `--version` flag to the TLB CLI and embed the build version into every structured log event. Two plans cover the work: first embed and expose the version in-process, then wire the CLI flag and event-log consumers to use it.

## Why this exists

When a chain run produces unexpected output, the first diagnostic question is "which build was this?" Currently there is no answer: the binary embeds no version, log lines carry no version metadata, and `analyze-event-log` has no way to group events by build. Bug reports arrive citing a symptom but not a binary, which forces a "reproduce from source at HEAD" step before any diagnosis can begin.

The version embedding also gates a downstream improvement: the comparison harness needs a reliable build identifier to correlate benchmark runs across TLB and the config-based baseline. Landing this before the harness runs means the harness measures TLB on the versioned contract rather than an unidentified binary.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Version foundation | - | M |
| B    | CLI and log integration | A | S |

Plan A establishes the version source and in-process accessor. Plan B depends on that accessor before exposing the CLI flag and event-log behavior.

## Plan A: Version foundation

### Goal

After this plan, the build version is embedded at compile time and readable through a single in-process accessor without requiring ticket-backend config, event logs, or command dispatch.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | version-source | Select and document the compile-time version source | - | Directory.Build.props |
| 02 | version-accessor | Expose the embedded version through an AOT-safe accessor | 01 | src/ThroughlineBuild.Cli/BuildVersion.cs, tests/ThroughlineBuild.Cli.Tests/BuildVersionTests.cs |
| 03 | version-publish-gate | Prove published binaries carry a non-empty version | 02 | tests/ThroughlineBuild.Cli.Tests/BuildVersionPublishTests.cs |

### Briefs - detail

#### Brief 01: version-source

Goal: The repository has one documented compile-time source for the build version so local and CI builds stamp binaries through the same MSBuild path.

Inputs:
- `Directory.Build.props`
- `src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj`
- `.github/workflows/build.yml`

Outputs:
- `Directory.Build.props` contains the selected version property.
- A short comment explains the local-development fallback and CI override path.
- Existing project defaults remain unchanged except for version metadata.

Acceptance:
- [ ] The selected MSBuild property is non-empty in a local build
- [ ] The CI override path is documented next to the property
- [ ] Existing project target frameworks remain unchanged

Notes: The version source belongs in shared MSBuild configuration because the value describes the compiled binary, not runtime state. Keeping the fallback local and deterministic avoids making tests depend on CI-only environment variables.

OOS:
- Semantic versioning policy
- Release tag creation
- CLI output changes

#### Brief 02: version-accessor

Goal: Application code can read the embedded build version from one AOT-safe accessor that returns a non-empty value in tests and published binaries.

Inputs:
- Version property from Brief 01
- `src/ThroughlineBuild.Cli/Program.cs`
- `src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj`

Outputs:
- `src/ThroughlineBuild.Cli/BuildVersion.cs` defines `BuildVersion.Current` from the embedded informational version.
- `tests/ThroughlineBuild.Cli.Tests/BuildVersionTests.cs` covers the non-empty runtime value.
- The accessor avoids runtime file reads and ticket-system calls.

Acceptance:
- [ ] `BuildVersion.Current` is non-empty in the unit test runner
- [ ] The accessor does not read the working tree or config files
- [ ] The accessor works before CLI verb dispatch
- [ ] AOT publish succeeds

Notes: Reading assembly metadata keeps the value tied to the binary being executed. The accessor stays small because Brief 01 owns the version policy.

OOS:
- `build --version` command behavior
- Event-log schema changes
- Analyzer output changes

#### Brief 03: version-publish-gate

Goal: The release publish path produces a binary whose embedded build version is available at runtime.

Inputs:
- `BuildVersion.Current` from Brief 02
- `tests/ThroughlineBuild.Cli.Tests/CliTestHost.cs`
- `.github/workflows/build.yml`

Outputs:
- `tests/ThroughlineBuild.Cli.Tests/BuildVersionPublishTests.cs` verifies the published binary exposes a non-empty version.
- The check documents the release gate used by this project.
- Failures point at version stamping rather than general CLI dispatch.

Acceptance:
- [ ] Release publish produces a binary with a non-empty version
- [ ] The verification names the release gate it exercises
- [ ] AOT publish succeeds

Notes: Unit tests prove the accessor shape, while the publish gate proves the deployed artifact carries the same metadata. Keeping those concerns separate makes failures easier to diagnose.

OOS:
- Multi-RID publish matrix
- Installer packaging
- Release-note generation

## Plan B: CLI and log integration

### Goal

After this plan, operators can ask the CLI for its version without touching external services, and every structured log produced by the CLI carries that same version for later analysis.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | version-flag | Add pre-dispatch `build --version` behavior | - | src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/VersionCommandTests.cs |
| 05 | versioned-event-log | Stamp and surface build versions in event logs | 04 | src/ThroughlineBuild.EventLog/EventRecord.cs, src/tools/analyze-event-log.cs, tests/ThroughlineBuild.EventLog.Tests/EventRecordTests.cs |

### Briefs - detail

#### Brief 04: version-flag

Goal: `build --version` prints the embedded build version and exits before config loading, ticket-backend calls, or normal verb dispatch.

Inputs:
- `BuildVersion.Current` from Plan A
- `src/ThroughlineBuild.Cli/Program.cs`
- `tests/ThroughlineBuild.Cli.Tests/CliTestHost.cs`

Outputs:
- Top-level CLI argument handling recognizes `--version`.
- The command writes `throughline-build {version}` to stdout.
- The path exits successfully before any external-service setup.
- `tests/ThroughlineBuild.Cli.Tests/VersionCommandTests.cs` covers output and pre-dispatch behavior.

Acceptance:
- [ ] `build --version` prints a non-empty version string
- [ ] `build --version` exits before config loading
- [ ] Unknown command handling remains unchanged

Notes: The version flag is a health-check path, so it must be available in minimal environments where ticket-backend authentication and workspace config are absent. Pre-dispatch handling is the important behavioral boundary.

OOS:
- Adding version text to help output
- JSON-formatted version output
- Version comparison logic

#### Brief 05: versioned-event-log

Goal: Structured event logs carry the build version and the analyzer surfaces it in chain summaries.

Inputs:
- `BuildVersion.Current` from Plan A
- `src/ThroughlineBuild.EventLog/EventRecord.cs`
- `src/ThroughlineBuild.EventLog/EventLogJsonContext.cs`
- `src/tools/analyze-event-log.cs`

Outputs:
- `src/ThroughlineBuild.EventLog/EventRecord.cs` includes a build-version field populated at construction time.
- `src/ThroughlineBuild.EventLog/EventLogJsonContext.cs` includes the updated event type in source-generated JSON metadata.
- `src/tools/analyze-event-log.cs` prints the build version from the first event in a chain log.
- `tests/ThroughlineBuild.EventLog.Tests/EventRecordTests.cs` covers new and legacy event payloads.

Acceptance:
- [ ] New event-log entries contain a non-empty build-version field
- [ ] Existing event-log fixtures still deserialize
- [ ] Chain summaries include the build version
- [ ] AOT publish succeeds

Notes: Stamping the base event shape keeps all event kinds consistent and avoids per-event drift. The analyzer reads the first event because one process invocation produces each log file.

OOS:
- Backfilling old event logs
- Cross-run version comparison
- Ticket-backend metadata updates

## What done looks like

An operator running a chain and then inspecting `analyze-event-log` output sees the build version in the chain summary, confirming exactly which binary produced the run. `build --version` works in CI health checks and local shells without reading config or contacting the ticket backend. Bug reports now have a concrete build identifier derivable from any new log.
```
<!-- canonical-example-end -->

---

## Common mistakes to avoid

- Using `# OP:` or any title other than `# Operation: {slug}`.
- Putting a multi-word title on the `# Operation:` line. The slug is a single kebab token; the human-readable title is the lead paragraph (`# Operation: batch-implement`, not `# Operation: batch-implement cohesive ticket groups`).
- Placing "What done looks like" before the briefs or between plans - it is always last.
- Writing Goal sections as bullet lists (they must be paragraphs).
- Putting requirements in "Why this exists" (that section is narrative context, not spec).
- Vague file paths ("src/ThroughlineBuild.Cli/ (output layer)" - name the file).
- Omitting a real dependency. Sequential execution turns a missing edge into a wrong-order run and often duplicated work.
- Re-creating an artifact from an earlier brief instead of depending on that brief.
- Writing a thin brief on the assumption that a later planning pass will flesh it out.
- Omitting OOS sections (they are not optional; scope creep starts here).
- Writing Notes as instructions ("do X") rather than rationale ("X was chosen because Y").
- Making "What done looks like" a summary of the deliverables list (it is an
  operator-observable narrative).
- Forgetting the project's release-gate checkbox (whatever it is for the stack - e.g. `AOT publish succeeds` in a C# Native AOT repo) in Acceptance for any brief that could break it.
