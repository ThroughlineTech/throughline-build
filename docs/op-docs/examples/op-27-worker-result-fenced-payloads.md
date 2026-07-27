# Operation: worker-result-fenced-payloads

Eliminate the JSON-escape failure mode by changing the WORKER_RESULT contract: large free-text payloads (plan body, implement summary, review critique) move out of JSON-escaped string fields into named fenced blocks emitted before the WORKER_RESULT envelope. The JSON envelope keeps only small scalars (status enum, labels, SHAs) and references the fenced blocks by name. Workers emit markdown inside the blocks; C# renders to HTML via an AOT-safe renderer on the consumer side. Hard-break contract change applied per-phase, matching TLB's established hard-break convention.

## Why this exists

The current contract puts the LLM in the position of hand-escaping every double-quote across thousands of tokens of free-text content, then embedding it inside a strict-JSON string field. Recent failure: a plan run produced 28 unescaped quotes in shell-snippet content (`cd /d "%~dp0"`, `findstr /c:"IPv4"`), broke strict JSON parse at byte 5097, and lost ~7 minutes of worker time. The risk analysis in the brainstorm document established that this failure mode is mechanical, not a fluke - it will recur, compounds multiplicatively under chain runs, and any tolerant-salvage repair introduces silent-corruption risk worse than the loud failure it would replace.

The Gemini and Copilot amplification that the risk analysis flagged as speculative is now testable - all four agents (claude-code, codex, gemini, copilot) are shipped and running real workloads. The implementer should pull recent worker transcripts across all four agents looking for the failure pattern (truncated JSON parse errors, byte-position-mid-string failures) and use that data to size the actual current failure rate per agent. The pattern is mechanical; agents producing more code-heavy or shell-heavy content will hit it more often.

The eliminate route removes the failure surface rather than reducing its frequency. The model writes free-text content into a delimiter-fenced block where escaping is not required at all; the JSON envelope carries only small structured fields where strict validation still earns its keep. The presentation conversion (markdown to HTML for Plane's renderer) moves into our C# code where it is deterministic and testable. This puts a floor under the contract's reliability rather than a ceiling.

The change is wide (parser, renderer, four agent templates per phase, multiple phases) but each piece is small and the result is a contract that does not have the JSON-escape failure mode designed into it. The phase-by-phase migration also lets the plan-phase vertical prove the design end-to-end before the other phases land. Strategic timing: the comparison harness (in progress) will benchmark TLB against claude-config; landing this op-doc before the harness measures TLB on the new contract rather than the failure-prone old one.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Foundation: protocol, parser, markdown renderer | - | M |
| B | Plan-phase migration (the proving vertical) | A | M |
| C | Other phases: implement, review, decompose evaluation | B | M |

A first; B depends on A's protocol and renderer; C depends on B (so the plan-phase migration has proven the pattern end-to-end before other phases adopt it).

## Plan A: Foundation

### Goal

The fenced-block protocol is specified, the parser extracts named fenced blocks alongside the WORKER_RESULT envelope, and an AOT-safe markdown-to-HTML renderer is available for consumers. After this plan, the building blocks for migrating any phase are in place. No phase has migrated yet.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | fenced-block-protocol-spec | Specify the fence-marker shape, block placement, JSON-reference convention, and parser contract | - | docs/worker-result-envelope.md (updated), docs/fenced-blocks-protocol.md (new) |
| 02 | parser-fenced-block-extraction | WorkerResultParser scans for named fenced blocks before the WORKER_RESULT marker and returns them keyed by name | 01 | src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs, tests |
| 03 | markdown-renderer | AOT-safe markdown-to-HTML renderer covering the markdown subset workers emit | - | src/ThroughlineBuild.Markdown/ (or chosen project), tests |

### Briefs - detail

#### Brief 01: fenced-block-protocol-spec

Goal: A written specification for how workers deliver named fenced blocks alongside the WORKER_RESULT envelope, sufficient for the parser brief to implement against and for the template briefs to follow.

Inputs: the current WORKER_RESULT envelope documented in docs/worker-result-envelope.md; the failure case from the brainstorm (5KB plan_html with 28 unescaped content quotes); markdown content that workers actually emit today (paragraphs, headers, lists, code fences, inline code) plus the markdown content shape that future phases will emit.

Outputs:
- The protocol document specifies a symmetric, named fence marker pattern (e.g. `<<<PLAN_BODY_START` and `<<<PLAN_BODY_END`, where PLAN_BODY is the block name and the implementer picks the exact marker syntax meeting the requirements below). Marker requirements: symmetric, named in both markers, does not collide with markdown content (triple-backtick code fences, headers, blockquotes, lists) or HTML markup.
- Block placement: zero or more named fenced blocks before the WORKER_RESULT marker; WORKER_RESULT envelope last; no fenced blocks after the envelope.
- JSON-reference convention: a metadata field naming the block, of the form `<field>_ref: "BLOCK_NAME"`, where the block is required to exist and the parser surfaces an error if the named block is absent.
- Block name conventions: ALL_CAPS_WITH_UNDERSCORES, phase-prefixed (PLAN_BODY, IMPLEMENT_SUMMARY, REVIEW_CRITIQUE), and listed by phase in the spec.
- Failure modes specified: unclosed fence, mismatched fence names, duplicate block names in one output, missing referenced block, block name not matching the spec - each maps to a documented parser error.

Acceptance:
- [ ] The protocol document specifies the marker syntax with its collision-avoidance rationale
- [ ] The block placement rule (before WORKER_RESULT, never after) is documented
- [ ] The JSON-reference convention is documented with at least one example envelope showing a metadata `_ref` field and the corresponding block
- [ ] Block-name conventions are documented with the per-phase block names enumerated
- [ ] Every failure mode (unclosed, mismatched, duplicate, missing-referenced, bad-name) is enumerated with the parser response
- [ ] The spec is referenced from docs/worker-result-envelope.md so a reader of the envelope documentation discovers the fenced-block convention

Notes: The marker syntax choice is judgment-based but constrained - symmetric and named so a mismatched fence in a long output still locates the failure cleanly, and distinct enough from common content patterns that collision is essentially zero. Angle-bracket-prefixed markers like `<<<NAME_START` are one reasonable choice; the implementer can pick another that satisfies the requirements. The metadata `_ref` field carries the block name rather than the content; the parser resolves the reference. This keeps the JSON envelope small even when the referenced block is large.

OOS:
- Parser implementation (B02 owns)
- Markdown renderer (B03 owns)
- Template updates for any phase (Plan B onward)
- Tolerant or salvage parsing for malformed fences - the parser fails loudly per the documented failure modes

#### Brief 02: parser-fenced-block-extraction

Goal: WorkerResultParser scans worker output for named fenced blocks before the WORKER_RESULT marker and returns the extracted blocks alongside the parsed JSON envelope. Per-phase consumers resolve `_ref` fields against the returned block map.

Inputs: the protocol spec from B01; the current WorkerResultParser implementation; the AOT constraint requiring source-gen JSON contexts; existing parser failure-mode tests.

Outputs:
- WorkerResultParser updated to return a structured result containing both the parsed JSON envelope (today's behavior) and a name-keyed map of extracted fenced-block contents.
- The parser scans for fence markers using a deterministic line-by-line approach (no regex backtracking on multi-megabyte outputs).
- Malformed fences (unclosed, mismatched-name, duplicate-name, name violating the convention) produce the documented parser failure with a clear error message naming the offending fence and its location in the output.
- Output containing zero fenced blocks parses cleanly (returns an empty block map alongside the envelope).
- A consumer-side helper resolves `_ref` fields: given an envelope and the block map, returns the block content for a referenced name, or fails with a clear error if the referenced block is missing.
- AOT publish succeeds; no reflection-based parsing introduced.

Acceptance:
- [ ] Worker output containing valid fenced blocks before the WORKER_RESULT marker produces an extracted name-keyed block map and the parsed envelope
- [ ] Output containing no fenced blocks parses with an empty block map; the envelope is unaffected
- [ ] Each documented malformed-fence case produces a failure with the documented error message
- [ ] A consumer that calls the `_ref` helper retrieves the correct block content
- [ ] A `_ref` referencing a missing block produces a clear consumer-facing error
- [ ] AOT publish succeeds across all three release RIDs

Notes: Line-by-line scanning avoids worst-case regex backtracking on long outputs. The block map carries content as `string` (the verbatim bytes between fences); rendering is the consumer's concern via the renderer from B03. Tests cover all failure modes from the protocol spec - the parser is the load-bearing reliability layer for the new contract, and each failure mode is exercised at least once.

OOS:
- Markdown-to-HTML rendering (B03 owns)
- Updating templates to emit fenced blocks (Plan B onward)
- Updating consumers to resolve `_ref` fields (per-phase migration owns)
- Tolerant or recovery parsing for malformed fences

#### Brief 03: markdown-renderer

Goal: An AOT-safe renderer that converts the markdown subset workers emit into HTML acceptable to Plane's description renderer. Available to consumers as a stable surface they can call after retrieving a fenced block's content.

Inputs: the markdown subset workers emit today (paragraphs, headers H1-H6, ordered and unordered lists, code fences with optional language, inline code, bold and italic, links); a sample of real Plane HTML rendering to verify acceptable output shape; AOT constraints requiring no reflection-based registration.

Outputs:
- A renderer exposing `string Render(string markdown)` that produces Plane-compatible HTML for the supported subset.
- Subset supported: paragraphs, ATX headers, unordered and ordered lists (with nesting), fenced code blocks with language hint, inline code, bold and italic emphasis, links. Tables, footnotes, definition lists, and other extensions are out of scope until a phase needs them.
- The renderer is deterministic - the same markdown input produces byte-identical HTML output across runs.
- Output rendered by Plane in a test ticket displays as expected for each supported markdown construct (verified once by hand and pinned by a fixture).
- The renderer is AOT-safe and AOT-tested: the renderer works in a `dotnet publish -r <rid> --self-contained -p:PublishAot=true` build.
- Markdig-with-AOT-config vs. hand-rolled-CommonMark-subset is the implementer's choice based on the trade-off in the notes.

Acceptance:
- [ ] Each markdown construct in the supported subset renders to the expected HTML, verified by fixture tests
- [ ] The renderer's output is byte-identical across two runs on the same input
- [ ] Plane renders the output as expected for at least one fixture per supported construct, verified against a real Plane test ticket
- [ ] AOT publish succeeds and the renderer works in a published binary on at least one platform
- [ ] Unsupported markdown constructs either render gracefully (passed through as literal text) or produce a renderer error - the choice is documented

Notes: Two reasonable implementation paths exist and the trade-off is real. Markdig is the mature library option; it requires AOT configuration that avoids reflection-driven extension registration (the AOT-safe pipeline subset works but the configuration dance is non-trivial). A hand-rolled CommonMark subset implementation is around 500-1000 lines, has zero dependencies, and is fully AOT-safe by construction; it is less feature-complete than Markdig but covers the subset workers actually emit. The implementer picks based on confidence in AOT configuration vs. willingness to maintain a small renderer; either is defensible. Document the choice in the renderer's header comment with the rationale.

OOS:
- Markdown extensions outside the supported subset (tables, footnotes, etc.) - added when a phase needs them
- Bidirectional HTML-to-markdown conversion - only one direction is needed
- A general-purpose markdown viewer / preview UI
- Plane-specific HTML feature flags or theming

## Plan B: Plan-phase migration

### Goal

Plan phase is migrated end-to-end to the new contract: all four agent plan.md templates emit the plan body inside a `PLAN_BODY` fenced block (markdown), the JSON envelope's `metadata` has a `plan_body_ref` field instead of `plan_html`, and PlanPhase resolves the reference, renders markdown to HTML, and splices the result into Plane. After this plan, plan-phase failures from JSON-escape are eliminated.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | plan-templates-fenced-emission | All four agent plan.md templates emit PLAN_BODY fenced block (markdown body) and reference it from JSON | - | src/ThroughlineBuild.Briefs/Templates/{claude-code,codex,gemini,copilot}/plan.md, tests |
| 05 | plan-consumer-migration | PlanPhase resolves plan_body_ref from the block map, renders markdown to HTML, and splices into Plane; old plan_html field is removed | 04 | src/ThroughlineBuild.Phases/PlanPhase.cs, tests |

### Briefs - detail

#### Brief 04: plan-templates-fenced-emission

Goal: Each of the four agent plan.md templates instructs the worker to emit the plan content as markdown inside a `PLAN_BODY` fenced block before the WORKER_RESULT envelope, and emit a corresponding `plan_body_ref` in the JSON metadata. The old `plan_html` field is removed from each template's example envelope.

Inputs: the protocol spec from A01; the current four agent plan.md templates; the Brief 14 agent-tool-name-mapping research for any agent-specific phrasing differences; the failure case from the brainstorm (shell snippets, quote-heavy content) for verifying the new templates handle that content cleanly.

Outputs:
- Each agent's plan.md template instructs the worker to: write the plan body as markdown, place it inside `<<<PLAN_BODY_START` and `<<<PLAN_BODY_END` fences (or the exact marker chosen in A01) before the WORKER_RESULT envelope, and include `"plan_body_ref": "PLAN_BODY"` in the envelope's metadata in place of the removed `plan_html`.
- Each template includes a concrete example showing the full output shape: the fenced block followed by the WORKER_RESULT envelope.
- Each template's instructions are explicit that the content inside the fenced block is markdown (not HTML, not JSON-escaped) and that no escaping is required.
- The shell-snippet failure case from the brainstorm runs through each agent template without producing JSON-escape failures.

Acceptance:
- [ ] Each of the four agent plan.md templates emits the PLAN_BODY fenced block and the plan_body_ref metadata field
- [ ] Each template's example envelope reflects the new shape
- [ ] A real plan run on claude-code with shell-snippet-heavy content produces a parseable output and no JSON-escape failure
- [ ] The same shell-snippet-heavy content produces a parseable output on at least one non-claude agent
- [ ] The old plan_html field is removed from every plan template; no template still emits it

Notes: The shell-snippet test case is the canary. Run it explicitly against each agent template before declaring the brief done - it is the case that broke today and the case the new contract is meant to eliminate. If any agent variant still fails, the failure is structural (likely an agent-specific output quirk) and surfaces back into the protocol spec or the renderer rather than a template-only fix.

OOS:
- Implement, review, decompose template migrations (Plan C owns)
- Renderer changes (A03 owns)
- Parser changes (A02 owns)
- Consumer-side migration of PlanPhase (B05 owns)

#### Brief 05: plan-consumer-migration

Goal: PlanPhase consumes the new contract: reads plan_body_ref from the envelope, resolves it against the parser's block map, renders the markdown content to HTML via the renderer from A03, and splices the rendered HTML into the Plane ticket. The old plan_html consumption path is removed.

Inputs: the parser block-map output from A02; the renderer from A03; the templates from B04 (so the consumer sees the new envelope shape); the existing PlanPhase consumption point at lines 119-124 of PlanPhase.cs.

Outputs:
- PlanPhase reads plan_body_ref from the parsed envelope's metadata.
- PlanPhase resolves the ref against the parser's block map; a missing block surfaces as a clear PlanPhase failure (not silently empty).
- PlanPhase renders the markdown content to HTML via the A03 renderer.
- The rendered HTML is spliced into the Plane ticket description as today.
- The old plan_html consumption code path is removed; nothing in PlanPhase still references plan_html.
- An end-to-end test exercises a full plan run with the shell-snippet failure case and verifies the resulting Plane ticket contains the rendered HTML matching the source markdown.

Acceptance:
- [ ] PlanPhase consumes plan_body_ref and produces a Plane ticket with the rendered HTML body
- [ ] A missing plan_body_ref or missing PLAN_BODY block produces a clear PlanPhase failure
- [ ] The shell-snippet failure case runs end-to-end without a JSON-escape failure and the resulting Plane ticket contains the expected content
- [ ] No reference to the old plan_html field remains in PlanPhase or its tests
- [ ] AOT publish succeeds for the modified plan-phase path

Notes: This brief closes the plan-phase migration loop. After it lands, plan-phase reliability is governed by the new contract end-to-end. The end-to-end test against the shell-snippet case is the proof: if it passes, the eliminate route has paid off for plan phase. The same pattern (templates + consumer migration as a pair per phase) is what Plan C applies to the remaining phases.

OOS:
- Implement, review, decompose consumer migrations (Plan C owns)
- Backward compatibility with old plan_html templates - hard-break, no compat layer
- Reading plan_body_ref-style fields in other phases (each phase migrates atomically)
- Bidirectional Plane HTML-to-markdown ingestion (one direction is sufficient)

## Plan C: Other phases

### Goal

The remaining phases (implement, review, draft, and decompose if applicable) migrate to the same fenced-block contract following the pattern proven by Plan B. After this plan, the JSON-escape failure mode is eliminated across the worker pipeline, not only plan phase.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 06 | implement-phase-migration | Templates + consumer for implement phase migrated to the fenced-block contract | - | src/ThroughlineBuild.Briefs/Templates/{...}/implement.md, src/ThroughlineBuild.Phases/ImplementPhase.cs, tests |
| 07 | review-phase-migration | Templates + consumer for review phase migrated to the fenced-block contract | - | src/ThroughlineBuild.Briefs/Templates/{...}/review.md, src/ThroughlineBuild.Phases/ReviewPhase.cs, tests |
| 08 | draft-phase-migration | Templates + consumer for draft phase migrated to the fenced-block contract | - | src/ThroughlineBuild.Briefs/Templates/{...}/draft.md, src/ThroughlineBuild.Phases/DraftPhase.cs, tests |
| 09 | decompose-evaluation | Evaluate whether decompose needs migration; if yes, migrate; if no, document why | - | src/ThroughlineBuild.Briefs/Templates/{...}/decompose.md (potentially), src/ThroughlineBuild.Phases/DecomposePhase.cs (potentially), docs |

### Briefs - detail

#### Brief 06: implement-phase-migration

Goal: Implement-phase migrates to the fenced-block contract end-to-end: each of the four agent implement.md templates emits any large free-text content as a named fenced block, the JSON metadata references the blocks, and ImplementPhase resolves the references and renders markdown to HTML as needed. The pattern follows Plan B exactly.

Inputs: the proven pattern from Plan B (templates emit fenced blocks, consumer resolves and renders); the current implement.md templates and ImplementPhase consumer; the field set in implement's current envelope (identify which fields are large free-text and need fenced-block treatment).

Outputs:
- Each agent's implement.md template emits the large free-text fields as named fenced blocks (e.g. `IMPLEMENT_SUMMARY` for the summary narrative; further blocks if implement carries other large fields).
- JSON metadata in each template uses `_ref` fields to name the blocks; old large-string fields are removed.
- ImplementPhase resolves the block references, renders markdown to HTML for any field consumed as HTML, and proceeds with the existing consumption.
- The old large-string consumption paths in ImplementPhase are removed.
- An end-to-end test exercises a full implement run with content that previously would have triggered JSON-escape failure.

Acceptance:
- [ ] Each agent's implement.md template emits the identified large fields as named fenced blocks
- [ ] Each template's JSON metadata uses `_ref` fields; no large free-text remains as a JSON-escaped string
- [ ] ImplementPhase consumes the new contract end-to-end and produces the expected ticket update
- [ ] Shell-snippet-heavy or quote-heavy content runs end-to-end without JSON-escape failure on at least claude-code and one other agent
- [ ] No references to the old large-string field names remain in ImplementPhase or its templates

Notes: This brief's first task is identification - look at implement's current envelope and decide which fields are large free-text deserving fenced-block treatment versus which are small enough to stay as JSON-escaped strings. Status enums, SHAs, labels, and short summaries stay in JSON; multi-paragraph narratives, embedded diffs, and any content that could include code snippets move to fenced blocks. Document the decision per field in the brief's implementation; the same decision logic applies to subsequent phase migrations.

OOS:
- Review, draft, or decompose phase migration (B07, B08, B09 own)
- Backward compatibility with the old implement contract
- Adding new envelope fields beyond the migration of existing ones
- Cross-phase aggregation of fenced blocks (each phase owns its own blocks)

#### Brief 07: review-phase-migration

Goal: Review-phase migrates to the fenced-block contract end-to-end. Same pattern as Plans B and the implement migration in B06.

Inputs: the proven pattern; the current review.md templates and ReviewPhase consumer; the field set in review's current envelope.

Outputs:
- Each agent's review.md template emits the large free-text fields (likely `REVIEW_CRITIQUE` and possibly per-file comment blocks) as named fenced blocks.
- JSON metadata uses `_ref` fields; old large-string fields are removed.
- ReviewPhase resolves the block references, renders markdown to HTML as needed, and proceeds with existing consumption logic.
- The old large-string consumption paths in ReviewPhase are removed.
- An end-to-end test exercises a full review run with content that previously would have triggered JSON-escape failure.

Acceptance:
- [ ] Each agent's review.md template emits the identified large fields as named fenced blocks
- [ ] Each template's JSON metadata uses `_ref` fields; no large free-text remains as a JSON-escaped string
- [ ] ReviewPhase consumes the new contract end-to-end and produces the expected ticket and verdict
- [ ] Quote-heavy critique content runs end-to-end without JSON-escape failure on at least claude-code and one other agent
- [ ] No references to the old large-string field names remain in ReviewPhase or its templates

Notes: Review's critique is the most likely large free-text field; per-file or per-acceptance-criterion comments may be additional blocks if review's current envelope structures them that way. The same per-field decision logic from B06 applies: identify what is large free-text vs. small structured data, and migrate the former.

OOS:
- Implement or decompose phase migration (B06, B09 own)
- Backward compatibility with the old review contract
- Changes to the review verdict semantics or the verifier-allowed-tools config

#### Brief 08: draft-phase-migration

Goal: Draft-phase migrates to the fenced-block contract end-to-end. Same pattern as the implement and review migrations: the large free-text fields move to named fenced blocks; the JSON envelope carries `_ref` field names; DraftPhase resolves the references and renders markdown to HTML as needed.

Inputs: the proven pattern from Plan B and the implement and review migrations; the current draft.md templates across the four agents; the DraftPhase consumer; the field set in draft's current envelope (identify which fields are large free-text - the drafted ticket body, the acceptance criteria narrative, and any other multi-paragraph fields).

Outputs:
- Each agent's draft.md template emits the large free-text fields as named fenced blocks. The drafted body is the primary case (e.g. `DRAFT_BODY`); other large fields get their own blocks if present (e.g. `DRAFT_ACCEPTANCE` if acceptance criteria are emitted as a multi-paragraph narrative).
- JSON metadata in each template uses `_ref` fields; old large-string fields are removed.
- DraftPhase resolves the block references, renders markdown to HTML for any field consumed as HTML by the ticket backend, and proceeds with the existing consumption.
- The old large-string consumption paths in DraftPhase are removed.
- An end-to-end test exercises a full draft run with content that previously would have triggered JSON-escape failure (shell snippets, code samples in the drafted body).

Acceptance:
- [ ] Each agent's draft.md template emits the identified large fields as named fenced blocks
- [ ] Each template's JSON metadata uses `_ref` fields; no large free-text remains as a JSON-escaped string
- [ ] DraftPhase consumes the new contract end-to-end and produces the expected drafted ticket
- [ ] Code-heavy or quote-heavy drafted content runs end-to-end without JSON-escape failure on at least claude-code and one other agent
- [ ] No references to the old large-string field names remain in DraftPhase or its templates

Notes: Draft is the verb operators use most often (new tickets are created more frequently than they are decomposed), so the JSON-escape failure rate on draft is the most operator-visible. If existing draft run logs show the failure pattern at any frequency, that is direct motivation for prioritizing this brief within Plan C. The same per-field decision logic from B06 applies: identify what is large free-text deserving fenced-block treatment vs. small structured data that stays as JSON-escaped strings.

OOS:
- Implement, review, or decompose phase migration (B06, B07, B09 own)
- Backward compatibility with the old draft contract
- Changes to draft's CLI surface or its interaction with `build new`
- Worker-driven content rewriting beyond what the existing draft envelope produces

#### Brief 09: decompose-evaluation

Goal: Decide whether decompose's envelope needs the same migration. Decompose's current `child_specs` is an array of structured objects where each child has title, description, acceptance_criteria, size, and scope_boundary; the per-child fields are bounded in size. The question: are those per-child fields large enough or quote-heavy enough to warrant migration, or is the structured-JSON shape tolerable for decompose's payload profile?

Inputs: the current decompose.md templates across the four agents; the DecomposePhase consumer; observed decompose output sizes from real runs (if available) for an empirical sense of per-field length.

Outputs:
- A documented decision: migrate decompose to fenced blocks (and if so, what block-per-field or block-per-child structure), or keep the current child_specs JSON shape (and document why - bounded field sizes, no quote-heavy content patterns in practice).
- If migration is chosen: the four decompose.md templates and DecomposePhase migrate following the Plan B pattern.
- If keeping the current shape is chosen: the rationale is documented in the protocol spec (A01's doc) so future phase additions reference the decision logic when deciding whether they need fenced-block treatment.

Acceptance:
- [ ] The decision is documented with a rationale referencing the per-field size profile and the quote-pattern profile of decompose content
- [ ] If migration: all four decompose.md templates and DecomposePhase migrate end-to-end, with the same acceptance criteria as B06 and B07
- [ ] If no migration: the rationale appears in the protocol spec so it informs future phase additions
- [ ] Either way, the decompose envelope is internally consistent (no half-migration)

Notes: Decompose is the natural case for "structured JSON is the right shape" because child_specs IS an array of small structured objects. If individual fields (description, acceptance_criteria) routinely exceed a few hundred tokens or routinely include code snippets, migration is warranted. If they stay short and prose-like, the JSON-escape risk is low enough that keeping the current shape is defensible. This is a real decision, not a foregone conclusion. The brief's first output is the decision, not the implementation. Decompose has been shipped and run against real parent tickets - the implementer can pull actual child_spec outputs from recent decompose runs to size the per-field length distribution empirically rather than speculate.

OOS:
- Implement, review, or draft phase migration (B06, B07, B08 own)
- Re-architecting decompose's parent-child Plane integration
- Validating decomposition quality or adjusting decompose templates for output quality

## What done looks like

The JSON-escape failure mode is gone from the worker pipeline. Plan, implement, review, and draft phases all emit large free-text payloads as named fenced blocks of markdown before the WORKER_RESULT envelope; the JSON envelope carries only small scalars and `_ref` field names; consumers resolve refs against the parser's block map, render markdown to HTML, and proceed. Decompose either migrates following the same pattern or stays as structured JSON with a documented rationale grounded in observed per-field length distributions from real runs. The shell-snippet failure case that motivated the work - 5KB of plan content with 28 unescaped quotes - runs end-to-end without parse failure on at least claude-code and one other agent. The contract has a reliability floor instead of a ceiling: it does not have the JSON-escape failure mode designed into it, and future phases inherit the same floor when they emit fenced blocks for their large fields.