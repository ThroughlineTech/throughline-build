# Implementation summary - experiment 2: context pre-loading (named-input + project-convention bundle)

Branch: `exp-2-context-preload` (cut from `main` at `9404455`, clean tree). Not merged, not pushed.
Implemented per `02-plan.md`; acceptance from `02-feedback-from-experiment-1.md`.
Standing protocol: `docs/analysis/method/experiment-harness-prompt.md`.

## Commits (oldest -> newest)

| Hash | Message |
|------|---------|
| ecca03c | cli: add convention_files + preload_context to [project] read-back and ProjectContext |
| 44ca0b7 | briefs: add PreloadedContextBuilder (parse named-input paths, inline via injected reader) |
| f2b3dbc | briefs: add preloaded_context_section placeholder to implement templates + Build param |
| e4a8784 | phases: pre-load named-input + convention contents in ImplementPhase from the live worktree |
| 235b673 | scaffold: derive a convention_files bundle (profile schema + parser) |
| 175f42a | cli: render [project].convention_files array in ConfigProfileWriter |
| 0d86f76 | scaffold: guide the deriver to emit a convention bundle (prompt + example) |

Change 1 (named-input pre-loading) = the mechanism in commits 1-4; Change 2 (convention bundle) =
commits 1 (read-back) + 5-7. Kept separable for clean back-out: Change 1 is useful without Change 2;
Change 2 needs Change 1's reader (the foundation commit serves both, so it leads).

NOTE - commit order re-sequenced from the plan (a mechanical decision the plan delegates): the plan's
suggested commit 3 gated on `project.PreloadContext`, a field the plan introduced in commit 5. To keep
every commit green I landed the `ProjectContext` + config read-back foundation FIRST (commit ecca03c),
then the builder, template, and wiring. Net change set is identical to the plan's file-by-file
checklist.

## Files changed

### Production (src/) - 410 insertions, 12 deletions
- `ThroughlineBuild.Briefs/PreloadedContextBuilder.cs` (new, 250 lines) - the stack-free unit: parses
  named-input paths from DescriptionHtml, prepends `ProjectContext.ConventionFiles`, reads each via an
  injected `Func<string,string?>`, dedupes, bounds (per-file head+tail, total, count), renders the
  `## Pre-loaded context` section (or "" when nothing to load).
- `ThroughlineBuild.Briefs/ProjectContext.cs` - `ConventionFiles` (default empty) + `PreloadContext`
  (default true) added as init-only properties (NOT primary-ctor params), so `Empty` and every existing
  construction site are untouched.
- `ThroughlineBuild.Briefs/ImplementBriefBuilder.cs` - new optional `preloadedContextSection = ""` param
  -> `vars["preloaded_context_section"]`.
- `ThroughlineBuild.Briefs/Templates/{claude-code,codex,copilot,gemini}/implement.md` - the
  `{{preloaded_context_section}}` placeholder, on the blank line between the Plan block and the Worktree
  block. Empty -> byte-identical to the prior template (so no snapshot churn). Added to ALL FOUR agent
  templates, not just claude-code, so the mechanism is worker-agnostic.
- `ThroughlineBuild.Phases/ImplementPhase.cs` - `MakeWorktreeReader` (internal, worktree-confined,
  never-throws) + the gated section build at the brief-build site (`project.PreloadContext` ? build : "")
  passed into `ImplementBriefBuilder.Build`.
- `ThroughlineBuild.Scaffold/ProjectProfile.cs` - `ProjectProfile.ConventionFiles` (init property,
  default empty) + `ProfileCheckDto`-sibling `ProjectProfileDto.ConventionFiles` (`List<string>?`) +
  best-effort mapping in `TryParse` (trim, drop blanks, never throw). No new `[JsonSerializable]` needed
  (see decisions).
- `ThroughlineBuild.Cli/Config.cs` - `OptionalBool` helper; `ReadProjectSection` reads `convention_files`
  (list, blanks dropped) and `preload_context` (bool, default true) onto `ProjectContext`; both keys
  added to `KnownProjectKeys`.
- `ThroughlineBuild.Cli/ConfigProfileWriter.cs` - renders `[project].convention_files = [...]` as a TOML
  string array (reusing `TomlStringArray`) in both the append-new-`[project]` and the in-place paths;
  written only when the bundle is non-empty.
- `ThroughlineBuild.Commands/Templates/config.toml.template` - commented `# convention_files` /
  `# preload_context` documentation in `[project]`.
- `ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` - a `convention_files` determine-bullet,
  a "Rules for convention_files" block (stable files + one canonical test example + per-stack examples
  for TS / .NET / Python + the lazy/greenfield note), and `convention_files` in the example
  `PROJECT_PROFILE` block. ASCII, LF.

### Tests (tests/) - 525 insertions
- `Briefs.Tests/PreloadedContextBuilderTests.cs` (new) - 13 stack-free facts: extraction keeps paths /
  rejects symbols+routes, only scans the Inputs section, second-stack (`.py`/`.go`) extraction, inline +
  not-found marker, convention-first ordering + dedupe, empty -> "", HTML unescape, per-file truncation,
  total-budget omission (visible), file-count cap, parent-escape/rooted paths never reach the reader,
  python-shaped convention bundle.
- `Briefs.Tests/ImplementBriefBuilderTests.cs` - empty section is byte-identical to the original snapshot;
  populated section lands between Plan and Worktree.
- `Phases.Tests/ImplementPhaseWorktreeReaderTests.cs` (new) - reads under root, null on missing, null on
  `..`-escape, null on absolute-outside (filesystem-level containment).
- `Scaffold.Tests/ProjectProfileParserTests.cs` - `convention_files` parsed/trimmed/blanks-dropped;
  absent -> empty not null (AOT reflection switch honored).
- `Cli.Tests/ConfigProfileWriterTests.cs` - round-trips render -> real Config load (incl. a quoted path,
  the escaping case); appended when no `[project]`; empty bundle writes no line.
- `Cli.Tests/ConfigLoaderTests.cs` - `convention_files` parsed/absent/blanks; `preload_context`
  default-true/parses-false; neither warned as unknown.

## Test result

`dotnet test` from repo root: **2178 passed, 0 failed, 0 skipped** across all 19 projects. Baseline
(`main`) was green before any change; ~32 net-new tests added. No Briefs template snapshots needed
re-baselining (the empty placeholder is byte-identical to the prior template - verified by the surviving
`implement-original/-rework/-gate-rework` snapshots, which also confirm LF was preserved).

## Acceptance mapping (against the feedback)

The feedback's measurable claim is "named-input and harness reads should go to ~0" via two engine
changes; the live turn-count is the experiment RUN's job (plan section 9), the deterministic mechanism
is proven here.

- "Pre-load the brief's named inputs, resolved and inlined" -> SATISFIED (deterministic mechanism).
  `ImplementPhase` reads each path the brief's own Inputs read-map names (parsed from `DescriptionHtml`,
  `/`-bearing dotted tokens) from the LIVE worktree and inlines it into a new `## Pre-loaded context`
  section ahead of the worker. Proven by `PreloadedContextBuilderTests` (extraction + inline) and
  `ImplementBriefBuilderTests` (section placement). The worker now has `types.ts`/`repository.ts` etc.
  without re-reading them; the read-to-~0 is measured live in section 9.
- "A derived project-convention bundle ... derived once at scaffold ... carried into every brief" ->
  SATISFIED (deterministic mechanism + deriver guidance). The deriver emits `convention_files`
  (harness/config + one canonical test example) on the same channel as the check profile; it threads
  derive JSON -> `ProjectProfile` -> `config.toml [project]` -> `ProjectContext` -> the preload section,
  read lazily per brief. Proven by the parser/writer/loader/builder tests; the `setupTests.ts`-read-7x
  class is exactly this bundle.
- "Net negative on turns and cache; measurement: named-input + harness reads -> ~0" -> the deterministic
  half is done; the live A/B (branch vs main; plus the `preload_context` flag ablation) using the
  `--debug` turn-class extractor is the run's job (plan section 9). Bounds keep the cache_create cost
  small (per-file + total caps).
- "Stack-agnostic (the #1 goal)" -> SATISFIED + AUDITED. `git diff main..HEAD -- src/**/*.cs` has no
  `language ==` / `if (language ...)` / tool-name compare; the engine keys on path SHAPE, not extension
  or language. The second-stack tests (`.py`/`.go` named inputs; a python-shaped convention bundle) and
  the TS/.NET/Python examples in the derive prompt show the engine carries whatever the data declares.

## Mechanical decisions and deviations (plan left these to the implementer)

- New `ProjectContext.{ConventionFiles,PreloadContext}` and `ProjectProfile.ConventionFiles` are
  init-only PROPERTIES with defaults, not primary-constructor params - minimal blast radius (no existing
  construction site, `Empty`, or test changed).
- Placeholder placement makes the empty case byte-identical to the prior template, so the three implement
  snapshots did NOT need re-baselining (better than the plan's "re-baseline the 3 snapshots" expectation).
  Added two `ImplementBriefBuilder` assertion tests (empty-byte-identical + populated-ordering) in place
  of a populated golden file.
- Placeholder added to all four agent templates (claude-code/codex/copilot/gemini), not just the
  experiment's claude-code worker, so the mechanism is worker-agnostic. `Substitute` throws on a missing
  var, so `preloaded_context_section` is always in `vars`; an unused placeholder in a template is fine.
- `List<string>` on `ProjectProfileDto` needed NO new `[JsonSerializable(typeof(List<string>))]` - it is
  already reachable via `ProfileCheckDto.Arguments`. Confirmed by the parser tests passing under the
  reflection-disabled (source-gen) AOT switch.
- `convention_files` is NOT in `ConfigProfileWriter.ProjectKeysOwnedByProfile` (that loop renders scalars
  via `TomlString`); it is handled as a dedicated array-key insert/replace, written only when non-empty
  (no `convention_files = []` noise). The unknown-key whitelist that prevents a warning is the separate
  `Config.KnownProjectKeys`, which DID get both new keys.
- Named-input extraction rule (a refinement of the plan's "contains a separator" heuristic): a `<code>`
  token is a path iff it has `/` AND a dotted final segment AND passes a relative-path validity gate
  (rejects rooted paths, `:` (routes/drives/URLs), `..`-escapes, whitespace, glob/markup chars). The `:`
  + leading-`/` rejections exclude route tokens like `/responses/:responseId` that the bare-separator
  rule would have matched.
- Containment is defense-in-depth: the builder rejects bad paths before the reader; the `ImplementPhase`
  reader ALSO confines reads to the worktree via a `Path.GetFullPath` prefix check (tested directly).
- Preload bounds default to 12 files, 16 KB/file (head+tail), 64 KB total - a mechanical choice per plan
  section 5; the requirement met is "never unbounded, never silent" (truncation/omission/not-found are
  all surfaced).
- Determinism (plan 2.5): the section is built in `ImplementPhase` and passed in; `ReviewPhase`'s
  reconstruction call is unchanged, so it passes the default "" -> empty section -> the verifier's
  reconstructed brief stays byte-identical. No `ReviewPhase` edit.
- Standalone-initial path (plan 2.3): the brief is built before the worktree exists there, so the reader
  returns null for everything and the section is empty - tolerated by design, not an error. The survey
  experiment is chain-based (worktree materialized early), where the lever fires.

## Out of scope (respected, per plan section 6)

No "do not re-read anything in context" general template lever (the one in-section sentence is scoped to
the inlined files only); no op-doc edit (the control stays byte-identical - the engine consumes the
read-map the op-doc already has) and no T10 read-map completion; no structured inputs field on `Ticket`
/ no Plane round-trip change; no `ImplementPhase` reorder to materialize earlier; no config-load-time
inlining (lazy per-brief from the live worktree); `Brief.RelevantFiles`/`Context`, the review/rework
loop, `MaxReworkRounds`, and the gate (experiment 1's surface) untouched; and - the #1 goal - no
stack-specific branch in engine C# (audited).

## Recommendation

FOLD candidate - but the decision belongs to the experiment RUN, not this implementation. The
deterministic mechanism and the engine behavior are fully proven by the unit suite (2178 green, ~32
net-new), and the change is stack-agnostic and bounded. What remains is the live measurement against the
fixed prompt class.

To decide FOLD vs ABANDON, run plan section 9:
1. Mechanism + agnostic (done here): builder/parser/writer/loader/reader tests; `/`-path extraction;
   derive -> config -> ProjectContext -> section thread; second-stack; bounds/not-found/dedupe;
   `preload_context=false` is byte-identical.
2. Live A/B: `exp-2-context-preload` binary vs the `main` control on the SAME op-doc, plus the
   `preload_context` on/off ablation (isolates the read mechanism from the deriver change), using the
   `--debug` turn-class extractor. Predict implement discovery ~78 -> ~30 and the systematic rediscovery
   (named inputs, `setupTests.ts` 7x, config 3-4x, prior-brief tests 6x) -> ~0; cache_create up a little,
   cache_read down (weak proxies).
3. No loop regression: rework count / completion within experiment-1 variance (0 rework, 8/8); review
   unchanged.

RUNNER NOTE (plan 1.1): measuring this needs BOTH the preload change and the `--debug` turn-class
extractor in the run binary. The extractor is now on `main` (commit `d651481`) and this branch is cut
from `main`, so a build of `exp-2-context-preload` carries both - no cherry-pick needed.

Branch left intact at `0d86f76`. No merge, no push, no branch deletion - humans decide. The handoff
ledger's Experiment 2 row (on `main`) should be moved PLANNED -> IMPLEMENTED, green when this lands.
