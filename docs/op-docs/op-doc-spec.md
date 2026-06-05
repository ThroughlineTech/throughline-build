# Op-Doc Format Spec

An op-doc is a planning artifact that drives agent orchestration in the latticeflow ticket workflow. When told "turn this into an op-doc," produce a document that matches this spec exactly. Op-docs live in `docs/op-docs/` and are fed to `/op-scaffold` and `/op-run`.

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

**`### Briefs - detail`** - One subsection per brief (see Per-brief rules below).

### 6. `## What done looks like`

**This is the last section in the document, after all Plan sections.** Do not place it before the briefs or between plans.

One prose paragraph (no bullets) describing the operator-observable end state after everything in this op-doc lands. Written from the operator's perspective: "A `build chain` invocation where..." Not a list of what was built. Closes the loop on the lead paragraph's promise.

---

## Per-brief rules

Each brief follows this structure in order:

**`#### Brief {NN}: {slug}`**

**Goal:** One paragraph. What the system does after this brief lands and why it matters. Not a deliverables list.

**Inputs:** What the implementer reads before starting. Name specific files with paths, specific line ranges if known, and specific prior-brief outputs if this brief depends on another. Prose or short bulleted list.

**Outputs:** Bulleted list of concrete artifacts: new types, modified behaviors, CLI flags, doc sections, tests. Each bullet is specific - names the file or the exact behavior change. Not abstract ("better error handling"). Concrete ("A post-condition assertion in ShipPhase that emits a clear failure if HEAD is detached after ff-merge").

**Acceptance:** Checkboxes. Each is independently verifiable - an operator can confirm each box without running the full suite. If the project has a release gate that a green local test run does not exercise - a production build, an ahead-of-time/native compile, a type-check, a bundle step, a packaged-import smoke - include a checkbox for it on any brief that could plausibly break it (new serialized types, new dependencies, generated code). See the project-gate convention under Style rules.

**Notes:** Design rationale, constraints the implementer must respect, tradeoffs already decided. Written in full paragraphs. Does not repeat what Outputs already states. Does not say "do X" - says "the reason X was chosen over Y is..." or "this constraint exists because..."

**OOS:** Short-phrase list of things explicitly not in this brief. Reference the plan/brief that owns each deferred item where applicable.

---

## Style rules

- No em-dashes anywhere. Use plain hyphens (`-`).
- File paths in brief tables and Inputs are specific: `src/ThroughlineBuild.Phases/ShipPhase.cs`, not `src/ (various)`.
- Deps column: `-` for no deps, brief number(s) for intra-plan deps, plan letter for cross-plan deps.
- Brief slugs: lowercase kebab-case, 3-6 words.
- Plan letters: A, B, C. Brief numbers: 01, 02, 03 (continuous across plans).
- Project release gate: most stacks have a verification step that a passing local test suite does not catch - the build/compile/type-check/bundle/package step that only fails outside the unit run. Name that gate once for the target project, then add a `<gate> succeeds` checkbox to Acceptance for any brief that could break it. The gate is stack-specific, not universal: a C# Native AOT project (such as the latticeflow repo this spec lives in) uses `AOT publish succeeds` for any brief that registers new types in a source-gen JSON context; a TypeScript project might use `tsc --noEmit passes` or `production build succeeds`; a Python project `the packaged entrypoint imports cleanly`. Pick the gate that matches the stack, or omit this checkbox entirely if the project has no gate beyond its tests.
- The lead paragraph is complete prose, not a sentence fragment.
- "Why this exists" and "What done looks like" are prose paragraphs, not bullets.
- Goal sections (plan-level and brief-level) are one paragraph each.
- Notes sections do not contain bullet lists - prose only.

---

## Skeleton (annotated)

The canonical complete example ships beside this spec as `op-doc-example.md`. The skeleton
below is an annotated shape reference, not a second source of truth.

The example below is one concrete op-doc for a C# project. Treat the stack-specific bits (`.cs` paths, `AOT publish succeeds`, the source-gen JSON constraint) as illustrations of the conventions, not as requirements for your stack - substitute your own paths and release gate.

```markdown
# Operation: example-slug

One tight lead paragraph. What changes, what the problem is, what the key mechanism is.
Under 100 words. Prose, no bullets, no heading. Written as "Replace X with Y so that Z."

## Why this exists

First paragraph: the incident or observation that broke this. Specific. Cite the failure
mode, the chain run ID, the error message, whatever is concrete.

Second paragraph: why the current state is structurally unacceptable (not just the one
incident). Why it will recur.

Third paragraph (optional): strategic timing note. Why now.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Foundation | - | M |
| B    | Consumer migration | A | M |

A first; B depends on A's output types.

## Plan A: Foundation

### Goal

After this plan, [the observable state]. One paragraph. Not a deliverables list.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | define-protocol | Specify the contract and document it | - | docs/protocol.md (new), src/Workers.Common/Parser.cs |
| 02 | implement-parser | Parser extracts named blocks alongside JSON envelope | 01 | src/Workers.Common/WorkerResultParser.cs, tests/ |

### Briefs - detail

#### Brief 01: define-protocol

Goal: A written specification for the contract, sufficient for the parser brief to implement
against and for template briefs to follow.

Inputs: The current `WorkerResultParser.cs` at `src/Workers.Common/WorkerResultParser.cs`;
the documented envelope at `docs/worker-result-envelope.md`; the specific failure case from
the incident (cite the concrete artifact).

Outputs:
- A new `docs/protocol.md` specifying the fence-marker shape, block placement, and
  JSON-reference convention.
- Every failure mode enumerated: unclosed fence, mismatched names, duplicate names,
  missing referenced block - each mapped to a documented parser error.
- `docs/worker-result-envelope.md` updated to reference the new protocol doc.

Acceptance:
- [ ] Protocol document specifies marker syntax with collision-avoidance rationale
- [ ] Block placement rule is documented (before WORKER_RESULT, never after)
- [ ] Every failure mode is enumerated with the expected parser response
- [ ] `docs/worker-result-envelope.md` references the new protocol doc

Notes: The marker syntax choice is judgment-based but constrained - symmetric and named so
a mismatched fence in a long output still locates the failure cleanly. Angle-bracket-
prefixed markers like `<<<NAME_START` are one reasonable choice. The spec is the contract
that both the parser brief and the template briefs implement against; completeness here
prevents ambiguity downstream.

OOS:
- Parser implementation (Brief 02 owns)
- Template updates for any agent (Plan B onward)
- Tolerant or salvage parsing for malformed fences

#### Brief 02: implement-parser

Goal: WorkerResultParser scans worker output for named fenced blocks before the
WORKER_RESULT marker and returns the extracted blocks alongside the parsed JSON envelope.

Inputs: The protocol spec from Brief 01; the current `WorkerResultParser.cs` (read
end-to-end); the AOT constraint requiring source-gen JSON contexts; existing parser
failure-mode tests.

Outputs:
- `WorkerResultParser` updated to return a structured result containing both the parsed
  JSON envelope (today's behavior) and a name-keyed map of extracted fenced-block contents.
- The parser scans using a deterministic line-by-line approach (no regex backtracking).
- Malformed fences produce the documented parser failure with a clear error message naming
  the offending fence and its location.
- Output containing zero fenced blocks parses cleanly (returns an empty block map).
- A consumer-side helper that resolves `_ref` fields against the block map, failing with a
  clear error if the referenced block is missing.
- AOT publish succeeds; no reflection-based parsing introduced.

Acceptance:
- [ ] Valid fenced blocks before WORKER_RESULT produce an extracted name-keyed block map
- [ ] Output with no fenced blocks parses with an empty block map; envelope is unaffected
- [ ] Each documented malformed-fence case produces the expected error
- [ ] A `_ref` referencing a missing block produces a clear consumer-facing error
- [ ] AOT publish succeeds

Notes: Line-by-line scanning avoids worst-case regex backtracking on long outputs. The
block map carries content as raw strings; rendering is the consumer's concern. Tests cover
all failure modes from the protocol spec - the parser is the load-bearing reliability
layer and each failure mode is exercised at least once.

OOS:
- Markdown-to-HTML rendering (separate brief)
- Updating templates to emit fenced blocks (Plan B onward)
- Updating consumers to resolve `_ref` fields (per-phase migration owns)
- Tolerant or recovery parsing for malformed fences

## Plan B: Consumer migration

### Goal

After this plan, [the observable state]. One paragraph.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | migrate-plan-phase | PlanPhase resolves the fenced-block ref and renders to HTML | A | src/Phases/PlanPhase.cs, tests/ |

### Briefs - detail

#### Brief 03: migrate-plan-phase

[follows the same structure as Brief 01 and 02 above]

## What done looks like

One prose paragraph from the operator's perspective. "A `build chain` run where..." or
"After landing, an operator who..." Describes observable behavior, not what was built.
No bullets. Closes the loop on the lead paragraph's promise.
```

---

## Common mistakes to avoid

- Using `# OP:` or any title other than `# Operation: {slug}`.
- Putting a multi-word title on the `# Operation:` line. The slug is a single kebab token; the human-readable title is the lead paragraph (`# Operation: batch-implement`, not `# Operation: batch-implement cohesive ticket groups`).
- Placing "What done looks like" before the briefs or between plans - it is always last.
- Writing Goal sections as bullet lists (they must be paragraphs).
- Putting requirements in "Why this exists" (that section is narrative context, not spec).
- Vague file paths ("src/ThroughlineBuild.Cli/ (output layer)" - name the file).
- Omitting OOS sections (they are not optional; scope creep starts here).
- Writing Notes as instructions ("do X") rather than rationale ("X was chosen because Y").
- Making "What done looks like" a summary of the deliverables list (it is an
  operator-observable narrative).
- Forgetting the project's release-gate checkbox (whatever it is for the stack - e.g. `AOT publish succeeds` in a C# Native AOT repo) in Acceptance for any brief that could break it.
