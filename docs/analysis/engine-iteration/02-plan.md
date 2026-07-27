# Plan - experiment 2: context pre-loading (named-input + project-convention bundle inlined into the implement brief)

Spec for the implementer. Input: `feedback-from-experiment-1.md` in this folder, itself the claude-web
synthesis of the experiment-1 run analyzed with the `--debug` turn-class instrumentation (the
`exp-debug-instrumentation` branch: structured per-turn worker transcript + turn-class extractor).
Read the stack-agnostic constraint (section A) and the architecture-reality section (2) before
touching any code - the feedback's wording is TypeScript/vitest-flavored and assumes the engine
already structures the op-doc read-map, which it does NOT. The fix changes shape because of both.

All file:line citations were read from the source tree on branch `exp-debug-instrumentation` at HEAD
`d651481` (the debug branch is the integration point; the experiment branch is cut from `main` - see
section 1.1). Lines marked "(verify)" were reported by a survey pass and not re-opened line-by-line;
confirm before editing. Where a doc and the code disagree, the code wins.

---

## A. Stack-agnostic constraint (the #1 goal - non-negotiable)

ThroughlineBuild generates and builds target projects of ANY stack: TypeScript, dotnet, Python, Go,
or a series of plain text documents. Its OUTPUT is stack-agnostic. Every change in this experiment
MUST be stack-agnostic too. A fix that only works for a TypeScript target is a defect.

The rule that makes this concrete: stack-specific knowledge lives in DATA (the op-doc the human
writes, and the LLM-derived project profile in the target's `config.toml`), NEVER in the engine
MECHANISM (C# code). The engine provides general mechanisms; the LLM that already derives the
per-stack check commands also derives the per-stack data each mechanism consumes.

The feedback is written in TS terms (`setupTests.ts`, `vite.config.ts`, vitest) because experiment 1
ran on a TS project. Do not transcribe those into engine code. Each becomes a general mechanism +
derived data, with TS as the first concrete instance:

- "read the brief's named input files and inline them" is general - the engine reads whatever paths
  the op-doc's `Inputs:` names, in any stack. The paths are data (in the op-doc); the reader is the
  mechanism.
- "carry setupTests.ts + a canonical test example into every brief" is general - the engine carries
  whatever paths the DERIVER chose (the deriver knows the stack). A dotnet target's bundle is a
  `.csproj` + a sample test class; a Python target's is `conftest.py` + a sample `test_*.py`. The
  engine never names a file; the deriver does. (The engine's OWN repo is dotnet/C# and we optimize it
  hard - "agnostic" constrains what the engine GENERATES, not what it is written in.)

No `if (language == "typescript")` in engine C#, ever. The stack-agnostic test (sections 3.3, 4.3)
is the primary proof no single-stack assumption leaked.

---

## 1. What experiment 2 changes (one sentence each)

The lever is the feedback's "real lever": pre-load a small, stable context bundle into every implement
prompt so the worker stops re-discovering files it could have been handed. Two sources feed ONE new
prompt section:

- Change 1 (named-input pre-loading): at implement-brief build time, parse the file paths the brief's
  own `Inputs:` read-map already names, read their CURRENT contents from the worktree, and inline them
  into the prompt - so the worker conforms to the contract instead of re-reading `types.ts` /
  `repository.ts` turn after turn. (T11 read its 3-4 named files and stopped; handing it those files
  means those Read turns never happen.)
- Change 2 (derived project-convention bundle): teach the deriver to emit a small, stable bundle of
  harness/config paths plus one canonical test example (the SAME derive channel as the check profile),
  carried into every brief and read the same way. This is what kills the `setupTests.ts`-read-7-times
  and `vite.config.ts`-read-3-4-times systematic rediscovery.

Both are instances of one umbrella principle (the inverse of experiment 1's gate-output rule): the
worker's context should carry exactly what the next turn needs BEFORE the turn happens, so the turn
that would have re-fetched it never occurs. That principle is stack-agnostic.

### 1.1 Branch and where this plan lives

- Experiment branch: `exp-2-context-preload`, cut from `main` with a clean tree (per the harness:
  experiments branch off main). This plan + the feedback live on `main` (the durable program record);
  the code change + `implementation-summary.md` live on the branch.
- DEPENDENCY NOTE for the human/runner: the feedback's metrics, and this experiment's MEASUREMENT
  (section 9), depend on the `--debug` turn-class instrumentation currently on `exp-debug-instrumentation`
  (commit `d651481`), which is not yet on `main`. The CODE change in this plan does not depend on that
  branch - it is a clean diff against `main`. But to MEASURE the experiment you need both the preload
  change and the debug extractor in the run binary. Resolve this before the run: either fold
  `exp-debug-instrumentation` into `main` first and cut `exp-2-context-preload` from the result, or cut
  the experiment branch from `main` and the runner builds it with the debug instrumentation cherry-picked
  in. Call this out; do not silently build the experiment branch without the extractor and then report
  turn counts you cannot produce.

---

## 2. Architecture reality (read this first)

The feedback assumes the engine already has structured `Inputs:` it can resolve. It does not. Five
facts shape the design; read them before designing anything.

### 2.1 The worker receives ONLY `brief.Instruction` - the preload must hit the instruction template

The implement worker subprocess is fed exactly one string: `brief.Instruction`, written to
`.build/brief.md` and piped to stdin (`src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:28`
and `:126` (verify): `await process.StandardInput.WriteAsync(brief.Instruction);`). The other `Brief`
fields - `RelevantFiles`, `Context`, `AllowedWrites` (`src/ThroughlineBuild.Contracts/Models/Brief.cs:3-9`)
- are INERT in the implement path: nothing reads them or the files they name. In particular the
`project_*` keys that `ImplementBriefBuilder` packs into `Brief.Context`
(`src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs:70-85`) never reach the worker.

Consequence: the pre-loaded content must be injected into the INSTRUCTION via the implement template,
not via `Brief.Context` or `RelevantFiles`. The existing conditional-section pattern is the mechanism:
`{{review_feedback_section}}` and `{{obsolete_detection_section}}` are built in `ImplementBriefBuilder`
and substituted into `implement.md` (`ImplementBriefBuilder.cs:32-33`, `:47-48`, `:51`). Add a
`{{preloaded_context_section}}` the same way.

### 2.2 The read-map survives only as `DescriptionHtml` prose - parse the named paths out of it

The op-doc's `Inputs:` is parsed into a structured `Scaffold.Brief.Inputs` bullet list by `OpDocParser`
(`src/ThroughlineBuild.Scaffold/OpDocParser.cs`, `ParseBriefDetail` ~`:632-737` (verify); the label
regex recognizes `Goal|Inputs|Outputs|Acceptance|Notes|OOS` ~`:31-33` (verify)). But that structure is
immediately FLATTENED back to prose HTML by `BriefHtmlRenderer.RenderBrief`
(`src/ThroughlineBuild.Scaffold/BriefHtmlRenderer.cs:63-110`) and stored as the ticket's
`DescriptionHtml` (`src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:260-266` (verify)). The `Ticket`
record (`src/ThroughlineBuild.Contracts/Models/Ticket.cs:3-14`) has NO structured inputs field; only
`DescriptionHtml` survives, and it round-trips through Plane as HTML. So at implement-brief build time
the only carrier of the read-map is `ticket.DescriptionHtml`.

This DECIDES the extraction approach. We do NOT add a structured inputs field to `Ticket` (that would
have to survive the Plane round-trip and is a much larger change). Instead we parse the named paths
out of the rendered HTML, which is deterministic because `BriefHtmlRenderer` emits a fixed shape:

```
<h3>Inputs</h3><ul><li><p>From B02 <code>src/data/types.ts</code>: <code>Survey</code>, ...</p></li>...</ul>
```

Every backtick span becomes a `<code>...</code>` (`BriefHtmlRenderer.cs:159-164`); list items are
`<li><p>...</p></li>` (`:112-122`, `:134`). The extraction rule (section 3.2): take the `<code>`
tokens inside the Inputs `<ul>` that look like file PATHS (contain a path separator), not symbols.
`src/data/types.ts` contains `/`; `Survey`, `getSurvey(id)`, `useParams`, `crypto.randomUUID()` do
not. This separator heuristic is stack-agnostic (every stack's relative paths carry `/`), needs no
op-doc change, and cleanly separates the cross-brief file dependencies (which is exactly the
rediscovery the feedback targets) from symbol references. Root-level single-file configs without a
separator (`package.json`, `setupTests.ts`) are NOT named-input matches by design - they are the
convention bundle's job (Change 2), derived once rather than re-named per brief.

### 2.3 Worktree materialization timing - read the live worktree, and only after it exists

The brief reaches the worker after the worktree is materialized (real `git worktree add`, full
checkout) in both paths, but the TIMING relative to brief-build differs:

- Chain (the survey experiment's path): the shared worktree is created EARLY, before the per-ticket
  implement loop (`src/ThroughlineBuild.Phases/ChainPhase.cs` ~`:2020-2031` create, `:564` thread the
  shared path into implement options (verify)). So at brief-build the files the brief names already
  exist on disk.
- Standalone: `ImplementPhase` builds the brief at `src/ThroughlineBuild.Phases/ImplementPhase.cs:239`
  BEFORE creating the worktree at "Step 9" (~`:264-269` (verify)), then runs the worker at ~`:309`
  (verify). So in the standalone initial path the source tree is not yet on disk at brief-build.

Design consequence: the preload reads files lazily and tolerates absence. A path that does not exist
yet (greenfield brief 01; the canonical test example before the brief that creates it; the standalone
initial build) simply contributes no content and is recorded as "not found" (a countable signal, not
an error). The survey experiment is chain-based, so the chain path - where files exist at brief-build
- is where the lever is measured. Do NOT reorder `ImplementPhase` to materialize earlier just to feed
the preload; tolerate-absence is correct and keeps the standalone path untouched.

### 2.4 The derive channel is the convention bundle's home - copy the experiment-1 canary thread

Change 2's bundle rides the exact channel experiment 1 used for the per-check `canary`. The full
thread, with the canary as the template to copy:

- Derive prompt: `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` instructs the
  worker to emit a `PROJECT_PROFILE` JSON (language/framework/commands + `review_checks` /
  `regression_checks`, each `{name, executable, arguments, timeout_minutes, canary?}`). There is NO
  "conventions / setup files / example test" notion today - a new top-level key is added.
- Profile model + parser: `src/ThroughlineBuild.Scaffold/ProjectProfile.cs` - parsed records
  (`ProjectProfile` `:16-25`, `ProfileCheck` `:27-32`), JSON DTOs (`:36-62`), the source-gen AOT
  context `ProfileJsonContext` (`:64-68`), and `ProjectProfileParser.TryParse` (`:78-205`). The canary
  field threads DTO (`ProfileCheckDto.Canary` `:55` + `CanaryFileDto` `:58-62`) -> source-gen
  registration (`:65-67`) -> best-effort map skipping blanks (`TryMapChecks` `:185-198`) -> parsed
  `ProfileCheck.Canary` (`:32`).
- Derive worker: `src/ThroughlineBuild.Scaffold/ScaffoldProfileDeriver.cs` (`DeriveAsync`, reads
  op-doc, runs a Small read-only worker, parses the `PROJECT_PROFILE` block) invoked best-effort from
  `src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs` (every failure warns, cannot fail scaffold).
- Write to config.toml: `src/ThroughlineBuild.Cli/ConfigProfileWriter.cs` (`Apply`; `ApplyProjectKeys`
  -> `[project]` ~`:118-159`; `RenderChecks` ~`:225-241`; canary rendered as a TOML inline-table array
  by `TomlCanaryArray` ~`:247-252` using the newline-safe `TomlBasicString` escaper ~`:269-278`
  (verify)). Owned `[project]` keys list ~`:23-27` (verify).
- Read back: `src/ThroughlineBuild.Cli/Config.cs` parses config.toml with Tomlyn (NOT hand-rolled) and
  builds both the `CheckSpec` list (with canary via `ParseCanary` ~`:472-490`, called from
  `ReadReviewSection`/`ReadShipSection` (verify)) and the `ProjectContext` (`ReadProjectSection`
  `:844-899`). Unknown keys warn, so any new key must be whitelisted: `KnownProjectKeys`
  (`Config.cs:266-270`), `KnownTopLevelSections` (`:197-200`), and the per-check `KnownCheckEntryKeys`
  ~`:246-249` (verify).
- Into the brief: `ReadProjectSection` returns a `ProjectContext`
  (`src/ThroughlineBuild.Briefs/ProjectContext.cs:3-26`) which `Program.cs` threads as `project:` into
  every phase and `ImplementPhase` hands to `ImplementBriefBuilder.Build` (`ImplementPhase.cs:239`).

THE EXISTING "read file contents from `[project]` into the brief" PRECEDENT is `notes_file`
(`Config.cs:863-886`): a `[project]` key names a path, its CONTENTS are slurped into
`ProjectContext.Notes` at config load. Change 2 follows the same shape but stores a LIST of paths and
reads their contents LAZILY at brief-build (not config-load), because at config-load the target source
does not exist yet (greenfield) and because the content should be the live worktree state, not a
config-relative snapshot.

### 2.5 ReviewPhase reconstructs the implement brief - the preload must be implement-only

`ImplementBriefBuilder.Build` is also called by `ReviewPhase` (`src/ThroughlineBuild.Phases/ReviewPhase.cs:153`)
to reconstruct "what the implementer was told" for the verifier. If the preload read live files there,
it would inline POST-implement content (the files were just edited) labeled as the implementer's
pre-read context - misleading and non-deterministic. Design: the preloaded section is built only for
the IMPLEMENT phase and is empty in the review reconstruction (the verifier already has the diff and
the worktree). Mechanically: the section is built in `ImplementPhase` (which owns the worktree and
already does I/O) and passed into `ImplementBriefBuilder.Build` as a prebuilt string param, defaulting
to "" - `ReviewPhase` passes nothing, so its reconstruction is unchanged. This keeps
`ImplementBriefBuilder` I/O-free (it stays in the Briefs layer doing pure string assembly) and puts the
file I/O in the Phases layer where it belongs.

### 2.6 Traps that bite (read before editing)

- Briefs snapshots: `implement.md` is byte-snapshot-tested. Adding `{{preloaded_context_section}}`
  changes `tests/ThroughlineBuild.Briefs.Tests/Snapshots/implement-original.txt`,
  `implement-rework.txt`, and `implement-gate-rework.txt` (the original snapshot is asserted from two
  test files (verify)). The snapshot fixtures build with no worktree reader, so the section is EMPTY
  there - choose the placeholder framing (section 5) so an empty section adds no stray blank lines, and
  re-baseline the three snapshots deliberately. Edit templates as LF (`.gitattributes` pins
  `Templates/**/*.md` to `eol=lf`; a CRLF edit breaks the byte compare).
- AOT: `Cli` is `PublishAot=true`. A new JSON-carried profile field needs its source-gen registration
  in `ProfileJsonContext` (`ProjectProfile.cs:64-68`) - if it introduces a new nested DTO type, add
  `[JsonSerializable(typeof(NewDto))]` and the `List<NewDto>` form, exactly as `CanaryFileDto` /
  `List<CanaryFileDto>` were added. The config.toml READ side uses Tomlyn's runtime model, no JSON
  context needed. Keep `Contracts` I/O-free; the file reader lives in `Phases` (or `Helpers`), not
  `Contracts`.
- Derive prompt is an embedded resource, NOT snapshot-pinned; the Scaffold deriver test asserts op-doc
  substitution survives. Keep the `{{op_doc_markdown}}` placeholder and the `WORKER_RESULT` envelope
  intact; validate any example JSON you add.
- Windows paths: the op-doc uses `/`; the worktree reader must resolve `/`-relative paths under
  `worktreePath` portably (normalize separators) and must never read outside the worktree.

---

## 3. Change 1 - named-input pre-loading

### 3.1 Root cause

The op-doc already names each brief's input files in its `Inputs:` read-map, but the engine does
nothing with them - the worker re-opens them itself, turn by turn. From the experiment-1 turn-class
extraction (the feedback): systematic rediscovery of the cross-brief contract files - `types.ts` and
`repository.ts` read by nearly every later brief, prior-brief test files read 6x - is pure
re-fetch of content the brief already pointed at. Each such Read is a wasted discovery turn carrying a
full cache_read round-trip. The general class: a worker re-derives, from the tree, a contract the brief
already declared. This is stack-agnostic - any worker on any stack re-reads named files if not handed
them.

### 3.2 Design - parse named paths, read live, inline (one unit, injected reader)

A new pure unit, `PreloadedContextBuilder` (in `ThroughlineBuild.Briefs`), with an injected file
reader so it is testable without disk and stack-free:

Signature (shape, not literal):
`PreloadedContextBuilder.Build(string descriptionHtml, ProjectContext project, Func<string,string?> readFile, PreloadOptions opts) -> string` (the section, or "" when nothing to preload).

1. Extract named-input paths from `descriptionHtml`:
   - Isolate the Inputs list: the substring from the `<h3>Inputs</h3>` heading to the next `<h3>` (or
     end). If absent, no named inputs.
   - Within it, take every `<code>...</code>` inner text, HTML-unescape it (reverse
     `BriefHtmlRenderer.EscapeHtml`: `&amp; &lt; &gt; &quot;`), and keep those that look like a file
     path: contains `/` (or `\`), has no whitespace and is not wrapped in parens, and is a relative
     path (reject rooted / `..`-escaping paths for safety). De-duplicate, preserve first-seen order.
   - This is deterministic against `BriefHtmlRenderer`'s output (2.2). It is a parse, not an LLM step.
2. Concatenate with the convention-bundle paths from `project` (Change 2; `project.ConventionFiles`),
   convention bundle FIRST (stable across briefs -> better prompt-cache reuse), named inputs after,
   de-duplicated across both sets.
3. For each path, `readFile(path)` -> content or null. Null (missing / unreadable / outside-worktree)
   contributes a one-line `- <path> (not found)` marker, not a hard error.
4. Bound the output (section 5): per-file cap (head+tail with a truncation marker), total-bundle cap,
   and a max file count - drop-with-a-note past the cap; never silently truncate.
5. Render the section (section 5 framing). Empty input set -> return "" so the placeholder vanishes.

Wiring:
- `ImplementPhase` backs `readFile` with a worktree-rooted reader (`File.ReadAllText` under
  `canonicalWorktreePath`, separator-normalized, with an in-worktree containment check), builds the
  section via `PreloadedContextBuilder.Build(...)`, and passes it into `ImplementBriefBuilder.Build`
  as a new `preloadedContextSection` parameter (default ""). Build it where the worktree is available
  - in the chain path the worktree already exists at `:239`; in the standalone initial path the reader
  returns null for everything (tolerated, 2.3). Simplest correct placement: build the section just
  before constructing the brief, using the same `canonicalWorktreePath`; absence is handled.
- `ImplementBriefBuilder.Build` adds `string preloadedContextSection = ""` (last optional param),
  puts it in `vars["preloaded_context_section"]`, and `implement.md` gains the placeholder
  (section 5). `ReviewPhase.cs:153` keeps calling the old arity -> empty section, reconstruction
  unchanged (2.5).
- Gate: a `[project]` boolean `preload_context` (default TRUE) read into `ProjectContext`
  (section 4.2 read-back). When false, `ImplementPhase` passes "" and behavior is byte-identical to
  pre-change (covered by a regression test). This is the ablation knob for section 9.

Do NOT touch `Brief.RelevantFiles` or `Brief.Context` - they are inert (2.1); routing the preload
through them would not reach the worker.

### 3.3 Tests (Change 1) - prove the mechanism is agnostic, not just TS

- Unit (`PreloadedContextBuilderTests`, the agnostic core, no disk):
  - path extraction: a DescriptionHtml with Inputs naming `src/data/types.ts` and symbols
    (`Survey`, `getSurvey(id)`, `crypto.randomUUID()`) -> only `src/data/types.ts` extracted; symbols
    and paren-bearing tokens rejected.
  - second-stack extraction: Inputs naming `src/app/models.py` and `internal/store/repo.go` ->
    extracted; proves no TS assumption (it is just `/`-bearing tokens).
  - reader integration via a fake `Func`: named path present -> its content inlined under a clear
    header; missing path -> `(not found)` marker, no throw.
  - bounding: an oversized file is head+tail truncated with a marker; total-cap drops extra files with
    a note; per-file count cap respected.
  - dedupe + order: convention paths first, named inputs after, a path in both appears once.
  - HTML unescape: a path/symbol with `&amp;`/`&lt;` round-trips to literal before path-matching.
  - empty: no Inputs section AND no convention files -> returns "" (placeholder vanishes).
- Unit (gate/regression): `preload_context = false` (or empty section) -> `ImplementBriefBuilder.Build`
  emits an instruction byte-identical to the pre-change baseline for that fixture.
- Containment: a reader asked for `../outside` or a rooted path returns null (never escapes the
  worktree); assert the builder emits `(not found)` and reads nothing outside.
- Snapshot: re-baseline `implement-original/-rework/-gate-rework.txt` for the new (empty) placeholder;
  add one snapshot OR assertion showing a NON-empty preloaded section for a fixture with a fake reader
  + a named input, so the populated shape is pinned, not only the empty one.

### 3.4 Acceptance mapping (Change 1)

- "named-input reads go to ~0" -> the engine inlines the brief's named-input file contents before the
  worker runs, so the worker has no reason to re-open them. Proven deterministically by the builder
  tests (extraction + inline) and the populated snapshot; measured live by the redundant-read rate and
  discovery-turn count in section 9.
- "stack-agnostic" -> the second-stack extraction test (`.py`/`.go` paths) and the absence of any tool
  or language branch in the builder; the paths are data from the op-doc, the reader is the mechanism.

---

## 4. Change 2 - derived project-convention bundle

### 4.1 Root cause

Some files are needed by nearly every brief but are not a cross-brief CONTRACT the way `types.ts` is -
the test harness/config (`setupTests.ts` read 7x, `vite.config.ts` 3-4x) and the test idiom (the
"mirror this test's setup" pattern). Naming them in every brief's `Inputs:` is the wrong fix (it is
the constant-pasted-into-8-briefs anti-pattern the feedback opens with). The right fix is to derive
the bundle ONCE (the deriver knows the stack) and carry it into every brief through the same channel
as the check profile. General class: a stable per-project convention set the worker re-discovers each
brief.

### 4.2 Design - emit a convention bundle on the derive channel, read it lazily into the preload

DATA, agnostic - the engine never names a file; the deriver does.

- Profile schema (DATA): add `convention_files` (a JSON array of relative path strings) to the derived
  profile. Thread it exactly like canary:
  - `ProjectProfileDto.ConventionFiles` (`[JsonPropertyName("convention_files")] List<string>?`) in
    `ProjectProfile.cs:36-47`; map (trim, drop blanks, never throw) into a new
    `ProjectProfile.ConventionFiles` (`IReadOnlyList<string>`, default empty) in `TryParse` near
    `:137-146`. No new nested DTO type needed (it is a `List<string>`), so the only source-gen
    addition is `[JsonSerializable(typeof(List<string>))]` IF not already reachable in
    `ProfileJsonContext` (`:64-68`; `List<string>` may already be registered via the args lists -
    confirm, add only if missing).
- Derive prompt (DATA, ASCII/LF): in `derive-profile-prompt.md`, add a `convention_files` rule and a
  stack example. General wording: "List up to N stable, project-wide convention files a worker should
  see in EVERY implementation brief - the test harness/setup, the build/test config, and ONE canonical
  test example to anchor the test idiom. Paths relative to the project root. Choose files that are
  stable across the build (config/harness), not files that change every brief. Omit if the project has
  none." Stack notes (examples, not an exhaustive list): TS/vitest -> `src/setupTests.ts`,
  `vite.config.ts`, one representative `*.test.tsx`; dotnet -> `Directory.Build.props`, a sample
  `*.csproj`, one `*Tests.cs`; Python/pytest -> `conftest.py`, `pyproject.toml`, one `test_*.py`.
  Cap the count (e.g. <= 4) so the bundle stays small. Update the `PROJECT_PROFILE` example block to
  show a `convention_files` array. (Greenfield reality: these paths may not exist at derive time; that
  is fine - they are read lazily per brief when they exist, see below. The deriver declares the paths
  the scaffold WILL produce, taken from the op-doc's File lists / scaffold brief.)
- Write: render `convention_files` as a `[project]` TOML array of strings in
  `ConfigProfileWriter.ApplyProjectKeys` (~`:118-159`) and add the key to the owned-`[project]`-keys
  list (~`:23-27`). A plain string array (`convention_files = ["a", "b"]`) - reuse `TomlBasicString`
  for each element; no inline-table needed.
- Config template: add a commented `# convention_files = [...]` placeholder to the `[project]` block
  of `src/ThroughlineBuild.Commands/Templates/config.toml.template` (~`:186-204` (verify)) so the
  derived key has a documented home (left empty; filled at `build scaffold`).
- Read back: in `Config.ReadProjectSection` (`Config.cs:844-899`), read `convention_files` (an
  optional string array; guard casts, skip blanks) and `preload_context` (optional bool, default
  true), add BOTH to `ProjectContext` (new fields `ConventionFiles: IReadOnlyList<string>` default
  empty, `PreloadContext: bool` default true - update `ProjectContext.cs:3-26` and `Empty`), and add
  `"convention_files"` and `"preload_context"` to `KnownProjectKeys` (`Config.cs:266-270`) so they do
  not warn as unknown.
- Consume: `PreloadedContextBuilder` already takes `project` (3.2); it reads `project.ConventionFiles`
  and prepends them to the path set. The lazy worktree reader (3.2) reads each path's CURRENT content
  when the brief is built - so the test-example path contributes nothing for the brief that creates it
  and real content for every brief after. No new read path; it reuses Change 1's reader.

### 4.3 Tests (Change 2)

- Unit (`ProjectProfileParserTests`): `convention_files` parses to the model (trim, drop blanks);
  absent -> empty list, not null; a non-string / blank element is skipped, never throws (AOT
  reflection switch honored as the existing parser tests do).
- Unit (`ConfigProfileWriterTests`): a profile with `convention_files` renders a `[project]`
  `convention_files = [...]` array that survives a real `Config` load back into
  `ProjectContext.ConventionFiles`; a path containing a quote is escaped (load-bearing escape test,
  mirroring the canary escape test).
- Unit (`ConfigLoaderTests`): `convention_files` absent -> empty; present -> parsed; not warned as
  unknown. `preload_context` defaults true (absent/present), parses false, not warned.
- Unit (builder, agnostic): `PreloadedContextBuilder` with `project.ConventionFiles = ["conftest.py",
  "tests/test_example.py"]` and a fake reader -> both inlined under the convention header, before the
  named inputs; a convention path also named in Inputs appears once (dedupe). Proves the bundle is
  just paths + the same reader, no stack branch.
- Regression: a profile/config with NO `convention_files` and `preload_context=true` but a brief whose
  Inputs name files -> only the named inputs preload (bundle empty); behavior matches Change 1 alone.

### 4.4 Acceptance mapping (Change 2)

- "setupTests.ts / config / prior-brief test reads go to ~0" -> the deriver emits them as
  `convention_files`, the engine inlines them into every brief, the worker has them without reading.
  Proven deterministically by the parser/writer/loader/builder tests; measured live in section 9 (the
  named systematic rediscoveries are exactly these files).
- "stack-agnostic" -> the bundle is a derived path list; the dotnet/python examples in the derive
  prompt and the python-shaped builder test show the engine carries whatever the deriver chose, with no
  language branch in C#.

---

## 5. The pre-loaded-context section (umbrella) - one bounded, deduped block

Both changes feed ONE section, injected via `{{preloaded_context_section}}` in
`src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md`. Placement: a natural seam is between
`## Plan (from ticket description)` (ends ~`:20`) and `## Worktree and branch` (`:21`) - the worker
reads the plan, then the files the plan points at, then the worktree facts. Framing rules so an empty
section is inert (snapshot-friendly):

- When the section is empty, the placeholder substitutes to "" with NO surrounding blank lines added
  by the template (put the leading newline INSIDE the built section string, as
  `BuildReviewFeedbackSection` does with its leading `\n` - `ImplementBriefBuilder.cs:115`). So an
  empty preload leaves `implement.md` byte-identical except for the now-empty placeholder line.
- When non-empty, render:
  - a short header (`## Pre-loaded context`) and one sentence telling the worker these files are
    already in context and SHOULD NOT be re-read ("The files below are current as of this brief; do not
    re-open them unless you intend to edit them") - this directly suppresses the re-read the lever
    targets, WITHOUT being the separate "do not re-read anything in context" template lever (that one
    is out of scope, section 6 - this sentence is scoped to the inlined files only).
  - convention files first (stable -> cache-friendly), then named inputs; each as a labeled fenced
    block: a `<path>` header line then a fence with the (bounded) content; missing files as a single
    `- <path> (not found)` line.
- Bounds (all stack-agnostic; reuse the `Bound`/`Tail`-style helpers already in
  `ImplementBriefBuilder.cs:163-169` / `AutomatedChecksRunner.Tail`): per-file cap (e.g. ~400 lines or
  ~16 KB, head+tail with a `... [truncated: N lines]` marker), total-bundle cap (e.g. ~64 KB - drop
  remaining files with a `- <path> (omitted: bundle size cap)` note), max file count. Numbers are the
  implementer's mechanical choice; the REQUIREMENT is: never unbounded, never silent. Emit the
  truncation/omission/not-found facts so section 9 can count them.

The header text is static prose with no stack reference; the only stack-specific things (the paths and
their contents) are data.

---

## 6. Out of scope / non-goals (do not do these in experiment 2)

Per the feedback's explicit sequencing notes - bundling any of these confounds the measurement:

- The "do not re-read files already in context" GENERAL template instruction (the TodoWrite / "other"
  turn-class lever). The feedback is explicit: that instruction also suppresses discovery re-reads, so
  running it alongside the preload makes the discovery delta unattributable. It is its own experiment
  (the ~27% "other" share earns its own run). The one in-scope sentence (section 5) is scoped ONLY to
  the specific inlined files, not a blanket "never re-read anything" rule.
- The op-doc-only read-map run and T10's read-map completion (the feedback's "runnable now, no engine
  work" lever). The op-doc is the CONTROL for this experiment - keep `workloads/survey-experiment-2.md`
  byte-identical across the OFF/ON A/B (section 9). Do NOT edit the op-doc to feed the preload; the
  engine consumes the read-map the op-doc ALREADY has (2.2). If you find you must change the op-doc to
  make the lever fire, that is a finding - report it, do not silently edit the control.
- Adding a structured inputs field to `Ticket` / changing the Plane round-trip. We parse the named
  paths out of `DescriptionHtml` (2.2); a structured field is a larger, separate change.
- Reordering `ImplementPhase` to materialize the worktree earlier (2.3) - tolerate-absence instead.
- Inlining file contents at config-load via the `notes_file` path - the bundle is read LAZILY at
  brief-build from the live worktree (2.4), not at config load.
- Touching `Brief.RelevantFiles` / `Brief.Context` semantics, the review/rework loop, `MaxReworkRounds`,
  or the gate (experiment 1's surface). The loop is not the lever here.
- Any stack-specific branch in engine C# (no `if (language ...)`, no tool-name compare). Stack
  specifics live in the op-doc (named paths) and the derived profile (convention paths) only.

---

## 7. Risks, gotchas, build discipline

- Stack-agnostic check (apply to every change): would this work for a dotnet/Python/text-doc target?
  Path extraction keys on `/`, not on `.ts`; the convention bundle is a derived path list; both readers
  are content-blind. If a change needs to know the stack, push it into the op-doc or the derive prompt.
- Determinism: the preload reads LIVE files, so the implement brief is no longer a pure function of the
  ticket. Keep it deterministic where it matters: (a) review reconstruction passes an empty section
  (2.5); (b) snapshot tests use a fake reader, not disk; (c) the live nondeterminism is confined to the
  implement worker's own prompt, which is fine (it is per-run by design).
- Snapshots: re-baseline the three implement snapshots deliberately (2.6); edit `implement.md` as LF.
- AOT/JsonSerializerContext: register `List<string>` in `ProfileJsonContext` only if not already
  reachable (4.2). Keep `Contracts` I/O-free; the worktree reader lives in `Phases`.
- Bounding is mandatory (section 5) - an unbounded inline of a large generated file (a lockfile, a big
  snapshot) would blow the prompt and invert the lever (more cache_create than the reads it saves).
- Windows/containment: resolve `/`-relative paths under the worktree, normalize separators, and refuse
  to read outside the worktree root (reject rooted paths and `..` escapes); a path that resolves
  outside -> treat as not-found.
- The `notes_file` warnings go to stderr and never fail load (`Config.cs:877-885`) - match that
  posture: a convention/named file that cannot be read warns/marks-not-found, never fails the brief.
- ASCII only; `topic: ...` commits; no AI branding; do not merge or push. Verify "(verify)" line
  numbers before editing (written against `d651481`; `ChainPhase.cs`/`Config.cs`/`ConfigProfileWriter.cs`
  are large and churn).

---

## 8. Implementation order and commit plan (suggested)

Work on sub-branch `exp-2-context-preload` cut from `main` (see 1.1 re: the debug-branch dependency for
MEASUREMENT, not for the code). Suggested commits, each leaving `dotnet test` green:

1. `briefs: add PreloadedContextBuilder (parse named-input paths, inline via injected reader)` - the
   pure unit + agnostic tests (extraction, dedupe, bounding, not-found, second-stack). No wiring yet.
2. `briefs: add preloaded_context_section placeholder to implement.md + Build param` - template edit,
   new optional `preloadedContextSection` param, re-baseline the 3 snapshots + a populated-section
   snapshot. `ReviewPhase` unchanged (empty section).
3. `phases: build the preloaded section in ImplementPhase from the live worktree` - worktree-rooted
   reader (containment-checked), wire `PreloadedContextBuilder` -> `ImplementBriefBuilder.Build`,
   gated on `project.PreloadContext`. Regression test: gate off -> byte-identical brief.
4. `scaffold: derive a convention_files bundle (profile schema + parser)` - `ProjectProfileDto`
   field, source-gen, parser mapping, `ProjectProfile.ConventionFiles`; parser tests.
5. `cli: write + read convention_files and preload_context in [project]` - `ConfigProfileWriter`
   render + owned-keys; `Config.ReadProjectSection` read-back + `KnownProjectKeys`; `ProjectContext`
   fields; config.toml.template comment; writer/loader tests.
6. `scaffold: guide the deriver to emit a convention bundle (prompt + example)` - `derive-profile-prompt.md`
   rule + stack examples + updated `PROJECT_PROFILE` example (ASCII/LF).

Change 1 = commits 1-3; Change 2 = commits 4-6. Keep them separable for clean independent back-out
(Change 1 is useful without Change 2; Change 2 needs Change 1's reader).

---

## 9. How we measure this experiment

Against the fixed prompt class (`workloads/survey-experiment-2.md`, held byte-identical across arms), using
the `--debug` turn-class extractor (section 1.1 dependency) and `build-run-analysis-prompt.md`,
contrasted with experiment 1's Run 1 baseline (`findings/experiment-1-analysis.md`).

Deterministic (no full run needed):
1. Mechanism + agnostic: the builder tests pass; named-input extraction pulls `/`-bearing paths and
   rejects symbols; the bundle threads derive -> config -> ProjectContext -> section; second-stack
   (`.py`/`.go`) paths preload identically; bounding/not-found/dedupe hold; gate-off is byte-identical.

Live A/B (the real lever - isolate it two ways):
2. Headline: `exp-2-context-preload` binary vs the `main` control binary on the SAME op-doc, same
   model, same scaffold class. The debug extractor keys events by build sha + op-doc sha, so turn
   counts are comparable per brief.
3. Ablation (isolates the READ mechanism from the deriver change): on the experiment binary, derive
   ONCE (so `convention_files` is fixed in the target config), then run `preload_context=true` vs
   `false`. Same binary, same target, same model -> the only difference is whether the engine inlines.

Falsifiable predictions (from the feedback, against experiment 1's Run 1):
- Implement discovery turns: ~78 -> ~30 (preload ON). The hard prediction is the SYSTEMATIC
  rediscovery going to ~0: named-input reads (`types.ts`, `repository.ts`), `setupTests.ts` (was 7x),
  config (was 3-4x), prior-brief test files (was 6x). Globs are a softer prediction (partly the
  worker's orientation reflex).
- Redundant-read rate (the extractor's reads-of-files-already-in-prompt): -> ~0 for the inlined set.
  This is the cleanest single number for "is front-loaded context consumed or re-read anyway."
- cache_create ticks UP a little (bigger prompt once), cache_read ticks DOWN (fewer turns). These are
  WEAK proxies (chain-efficiency-evidence.md) - report, do not over-read. The turn-class delta is the
  signal; cost is the side effect.
- named-but-not-read (extractor): files we preload but the worker never needed -> trim from the bundle
  next round. read-but-not-named: files the worker read that we did NOT preload -> candidates the
  op-doc/bundle should add. Both are tuning outputs, not pass/fail.
- No quality/loop regression: rework count and completion rate within experiment-1 variance (0 rework,
  8/8 in Run 1); review unchanged.

State confounds honestly. The clean wins are (1) and the discovery/redundant-read deltas in (3) (the
ablation removes the deriver-change confound that (2) carries). If discovery drops but production /
verification / "other" hold, the cut is attributable to the preload and to this experiment.

---

## 10. File-by-file change checklist

| # | File | Change | Part | Agnostic? |
|---|------|--------|------|-----------|
| 1 | `src/ThroughlineBuild.Briefs/PreloadedContextBuilder.cs` (new) | Parse named-input paths from DescriptionHtml; prepend `project.ConventionFiles`; read via injected `Func<string,string?>`; dedupe; bound; render section (or "") | 1+2 | yes (no tool/lang) |
| 2 | `src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs` | New optional `preloadedContextSection = ""` param -> `vars["preloaded_context_section"]` | 1 | yes |
| 3 | `src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md` | `{{preloaded_context_section}}` between Plan and Worktree; empty -> inert | 1 | yes |
| 4 | `src/ThroughlineBuild.Phases/ImplementPhase.cs` | Worktree-rooted, containment-checked reader; build section via `PreloadedContextBuilder`; gate on `project.PreloadContext`; pass into `Build` | 1 | yes |
| 5 | `src/ThroughlineBuild.Scaffold/ProjectProfile.cs` (+ `ProfileJsonContext`) | `convention_files` DTO + parsed `ProjectProfile.ConventionFiles`; source-gen `List<string>` if missing | 2 | data, yes |
| 6 | `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` | `convention_files` rule + stack examples + example block | 2 | data, yes |
| 7 | `src/ThroughlineBuild.Cli/ConfigProfileWriter.cs` | Render `[project].convention_files` array; add to owned `[project]` keys | 2 | yes |
| 8 | `src/ThroughlineBuild.Cli/Config.cs` | Read `convention_files` + `preload_context` in `ReadProjectSection`; add both to `KnownProjectKeys` | 2 | yes |
| 9 | `src/ThroughlineBuild.Briefs/ProjectContext.cs` | `ConventionFiles` (default empty) + `PreloadContext` (default true) + `Empty` | 2 | yes |
| 10 | `src/ThroughlineBuild.Commands/Templates/config.toml.template` | Commented `# convention_files` / `# preload_context` in `[project]` | 2 | yes |
| 11 | tests: Briefs / Phases / Scaffold / Cli | Builder agnostic core (incl. second-stack), gate-off byte-identical, snapshots (empty + populated), parser/writer/loader, containment | 1+2 | yes |

Note: rows 1-4 are Change 1 (useful alone); rows 5-10 are Change 2 (needs row 1's reader). Every row
keeps stack knowledge in data (op-doc paths in rows 1/4; derived paths in rows 5-8) or in stack-free
mechanism (rows 1-4); none branches on language in C#. Items marked "(verify)" in sections 2-5
(`ScaffoldPhase`/`ChainPhase`/`ImplementPhase`/`ConfigProfileWriter` exact lines) must be confirmed by
reading before editing, and the chosen template seam, bound numbers, and gate placement reported in the
implementation summary.
