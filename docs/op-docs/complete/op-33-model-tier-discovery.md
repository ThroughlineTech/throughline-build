# Operation: model-tier-discovery

Replace the static, hand-maintained per-size model strings with effort-aware tiers and a Codex discovery step, so the build never silently runs the wrong model. Each `[workers.<agent>.sizes]` entry becomes a `{ model, effort }` tier; Claude keeps stable tier aliases (`haiku`/`sonnet`/`opus`) that auto-track the latest model; Codex gains a reasoning-effort dimension plus a probe (`codex debug models`) that `build init` and a new `build models refresh` use to write and reconcile its tier block. The dead in-code default model maps and the stale `default_model` template pin are removed.

## Why this exists

A `build chain` run logged `system init session ... model claude-opus-4-7` when the operator expected Sonnet. Investigation TLB-465 (and `docs/survey-smoketest.md`) found the cause: `[llm] default_model` does not drive worker model selection at all - it is consumed only by the close/defer/reopen reason translator. The worker model comes from ticket size -> WorkerSize -> the `[workers.<agent>.sizes]` map, and the L-sized brief correctly resolved to `large`, whose value was the stale `claude-opus-4-7` baked into the init template. The operator's apparent lever (`default_model`) was inert; the real lever (the sizes map) was stale and hidden in three files.

This is structural, not a one-off. Model strings are hand-maintained in `.build/config.toml`, `.build/config.toml.example`, and the embedded `config.toml.template`, plus dead default maps inside each agent's Options class. They drift the day a vendor ships a new model. Pinning `claude-opus-4-8` (or `-4-7`) means the binary keeps calling a model that will be superseded, and nothing tells the operator. This recurs on every model release, and the dead in-code maps make it worse: a missing or misnamed `sizes` block silently falls back to a stale hardcoded string instead of failing loudly.

Two fixes compound. Claude exposes stable tier aliases that resolve to the current model of each tier, removing the need to pin Claude versions at all - and the concrete model still lands in the event log via the stream's system event, so survey attribution is unaffected. Codex exposes `codex debug models`, which lists current models and their reasoning-effort levels, turning `build init` into a step that both verifies Codex is usable and writes a correct, current tier block. Doing both now, while the project is pre-release, lets the schema change land without back-compat machinery.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Tier schema and Codex effort | - | L |
| B    | Codex discovery and refresh | A | M |

Plan A first: it reshapes the sizes config into `{model, effort}` everywhere and is the type all later work reads. Plan B layers discovery, init enrichment, and the refresh verb on top of A's schema, so it cannot start until A's tier type exists.

## Plan A: Tier schema and Codex effort

### Goal

After this plan, every `[workers.<agent>.sizes]` entry is a `{ model, effort }` table with `effort` optional, the four worker agents resolve their model from that tier through one shared type, Codex passes its tier's reasoning effort to the CLI and records it in usage telemetry, and no model version string is hardcoded in agent code. The redundant checked-in `.build/config.toml.example` is gone, leaving the embedded template plus `build init --print-template` as the single config reference. An L-sized ticket still selects the `large` tier; Claude's behavior is unchanged except that its configured strings are now tier aliases.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | model-tier-schema | Sizes entries become `{model, effort}` tables across contracts, config, all agents | - | src/ThroughlineBuild.Contracts/Models/ModelTier.cs (new), src/ThroughlineBuild.Cli/Config.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs, src/ThroughlineBuild.Workers.Codex/CodexOptions.cs, src/ThroughlineBuild.Workers.Codex/CodexAgent.cs, src/ThroughlineBuild.Workers.Gemini/GeminiOptions.cs, src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs, src/ThroughlineBuild.Workers.Copilot/CopilotOptions.cs, src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs, src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs, src/ThroughlineBuild.Commands/Templates/config.toml.template, tests/ |
| 02 | codex-effort-and-telemetry | Codex emits `-c model_reasoning_effort` from the tier and logs effort in usage | 01 | src/ThroughlineBuild.Workers.Codex/CodexAgent.cs, tests/ |
| 03 | retire-config-example | Delete the example; the embedded template plus `--print-template` is the single config reference | 01 | .build/config.toml.example (deleted), src/ThroughlineBuild.Commands/Templates/config.toml.template, src/ThroughlineBuild.Commands/Templates/throughline_build_userguide.md, tests/ThroughlineBuild.Cli.Tests/ShipCliTests.cs |

### Briefs - detail

#### Brief 01: model-tier-schema

Goal: The size-to-model configuration carries a model and an optional reasoning effort per tier, parsed and validated uniformly, and consumed by every worker agent through a single shared type. An L-sized ticket still selects the `large` tier; the only change is that a tier is now `{model, effort}` instead of a bare string, and the in-code fallback maps that masked missing config are gone.

Inputs: `src/ThroughlineBuild.Cli/Config.cs` - the `[workers]` parser `ReadWorkersSection`, the `sizes` sub-table handling (~520-539), `KnownSizesKeys` (~203-206), the `AgentConfig` record (Config.cs:24), and the sizes branch of `CollectUnknownKeyWarnings` (~287-294). The four agent option+agent pairs where `Sizes.TryGetValue(...)` feeds `NormalizeModel(...)`: `ClaudeCodeOptions`/`ClaudeCodeAgent` (BuildArgs ~374-388), `CodexOptions`/`CodexAgent` (BuildArgs ~290-303), `GeminiOptions`/`GeminiAgent`, `CopilotOptions`/`CopilotAgent`. `src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs` (passes `cfg.Sizes` into each Options). The embedded `src/ThroughlineBuild.Commands/Templates/config.toml.template`. `WorkerSize` at `src/ThroughlineBuild.Contracts/Models/WorkerSize.cs`.

Outputs:
- A new `ModelTier(string Model, string? Effort)` record in `src/ThroughlineBuild.Contracts/Models/`, leaf and I/O-free per the Contracts constraint.
- `AgentConfig.Sizes` retyped to `IReadOnlyDictionary<WorkerSize, ModelTier>`; the four `*Options.Sizes` properties retyped to match.
- `Config.cs` parses each size entry as an inline table with a required `model` string and an optional `effort` string; the bare-string form is no longer accepted. `KnownSizesKeys` and the sizes unknown-key warning updated to recognize `model`/`effort` inside each size table.
- The in-code default size maps in `CodexOptions`, `GeminiOptions`, and `CopilotOptions` removed - they default to an empty map, matching `ClaudeCodeOptions`.
- All four agents resolve the model from `tier.Model` through the existing `NormalizeModel`; the effort field is plumbed but acted on only by Codex (Brief 02).
- The embedded template rewritten to the table schema: Claude tiers as `{ model = "haiku|sonnet|opus" }`; Codex tiers as `{ model, effort }`. The template's active `default_model = "anthropic:claude-opus-4-7"` and `large = "claude-opus-4-7"` removed or replaced, and the live config's deprecated-`default_model` note carried over.
- AOT publish succeeds; `ModelTier` introduces no reflection-based serialization.

Acceptance:
- [ ] A size entry written as `{ model = "x", effort = "y" }` parses into a `ModelTier` carrying both fields
- [ ] A size entry written as `{ model = "x" }` parses with a null effort
- [ ] A bare-string size value is rejected with a clear config error
- [ ] A missing required size key (small/medium/large) still fails with the existing actionable error
- [ ] An unknown key inside a size table (not `model`/`effort`) emits the unknown-key warning
- [ ] Every agent passes its configured tier model to the CLI, and no agent source contains a hardcoded model string
- [ ] The template parses cleanly under the new parser and contains no `claude-opus-4-7`
- [ ] AOT publish succeeds
- [ ] dotnet test green

Notes: This brief is wide but shallow and must land atomically - flipping `AgentConfig.Sizes` from string to `ModelTier` breaks compilation until every Options and Agent is updated, so the migration cannot be partially shipped. The bare-string form is dropped rather than dual-supported because the project is pre-release and one parse path is simpler than a migration shim. Removing the in-code default maps is the point, not a side effect: they silently masked a missing or misnamed `sizes` block by feeding a stale model, which is the exact failure class this operation exists to end - with them gone, a missing tier surfaces as the existing config error rather than a wrong model. Claude's tiers move to aliases because the CLI resolves `haiku`/`sonnet`/`opus` to the current model of each tier, so the binary never needs a Claude version pin; the concrete model still reaches the event log through the stream's system event.

OOS:
- Codex effort behavior at call time and in telemetry (Brief 02)
- Deleting the redundant `.build/config.toml.example` and repointing its references (Brief 03)
- Any model discovery or probe (Plan B)
- Removing the `[llm] default_model` code path / `ReasonTranslator` (still used by close/defer/reopen)
- Gemini and Copilot discovery (they keep manually-edited tiers; only their dead in-code defaults are removed here)

#### Brief 02: codex-effort-and-telemetry

Goal: When a Codex tier specifies an effort, the Codex worker runs at that reasoning effort and the chosen effort is recorded in usage telemetry, so cost and quality can be attributed to effort and not just model. When effort is absent the run uses Codex's own per-model default and no effort flag is emitted, keeping effort-less tiers byte-identical to today.

Inputs: `src/ThroughlineBuild.Workers.Codex/CodexAgent.cs` - `BuildArgs` (~290-303, where `--model` is appended) and `BuildLlmUsageMetadata` plus its call site (~146-153, ~350-372). The `ModelTier` shape from Brief 01. The documented invocation `codex --model <m> -c model_reasoning_effort="<effort>"`.

Outputs:
- `CodexAgent.BuildArgs` appends `-c model_reasoning_effort=<effort>` when the resolved tier carries a non-empty effort, and emits nothing when effort is null or empty.
- `BuildLlmUsageMetadata` and its caller record the effort under a stable key (`reasoning_effort`) in the Codex `llm_usage` metadata; null or absent when no effort was set.
- The effort value is passed as a discrete `ArgumentList` entry (no shell quoting required).

Acceptance:
- [ ] A Codex tier with `effort = "xhigh"` produces argv containing `-c model_reasoning_effort=xhigh`
- [ ] A Codex tier with no effort produces no `model_reasoning_effort` argument
- [ ] The Codex `llm_usage` metadata carries the effort when set and is null/absent when not
- [ ] dotnet test green

Notes: Effort is recorded in telemetry because the motivating use case is the A/B comparison work in `docs/survey-smoketest.md` - without effort in the event log, a Codex run's cost cannot be attributed to the effort level, only the model, which is exactly the gap that report flagged. The flag is omitted rather than defaulted when effort is null so Codex applies its own per-model `default_reasoning_level`, keeping effort-less tiers identical to current behavior. Effort stays a Codex-only behavior: the schema carries it for every vendor but only Codex acts on it, an accepted asymmetry because Claude has no equivalent discrete reasoning knob in headless mode.

OOS:
- Claude reasoning or thinking control (explicitly dropped - no token-budget knob, tokens fall where they do today)
- Discovering which effort levels are valid for a model (Plan B's probe)

#### Brief 03: retire-config-example

Goal: The project keeps one config reference, not two. The redundant `.build/config.toml.example` is deleted, the embedded template is the single static source, and an operator who wants to see every supported key runs `build init --print-template` to generate it from code. Nothing that previously pointed at the example dead-ends.

Inputs: the example at `.build/config.toml.example` and everything that references it - the lockstep comment at the top of `src/ThroughlineBuild.Commands/Templates/config.toml.template` (~line 3), the operator-guide reference in `src/ThroughlineBuild.Commands/Templates/throughline_build_userguide.md` (~line 7, "Config reference: .build/config.toml.example") and its generated copy under `docs/`, and the `ShipCliTests` example check (`FindConfigExampleFile` plus the `[ship]` / `[[ship.regression_checks]]` assertion at ~145-167). The `build init --print-template` path (Program.cs ~215-229, `InitCommand`) as the replacement reference.

Outputs:
- `.build/config.toml.example` deleted from the tree.
- The "Keep this template in lockstep with .build/config.toml.example" comment removed from the embedded template.
- The user-guide template (and its generated copy under `docs/`) repointed from the example file to `build init --print-template` as the "every supported key" config reference.
- `ShipCliTests` repointed to assert the ship sections against `build init --print-template` output (or the embedded template resource) instead of the deleted example file.

Acceptance:
- [ ] `.build/config.toml.example` no longer exists in the tree
- [ ] No source, test, template, or operator doc references `config.toml.example`
- [ ] `build init --print-template` emits a config containing every supported section (ticketing, llm, workers + sizes, events, review, ship, project)
- [ ] The ship-config test passes against the print-template output (or embedded template), not the example
- [ ] dotnet test green

Notes: The example was a hand-maintained duplicate of the embedded template, and the template comment already admitted the two must be kept "in lockstep" - a standing drift hazard that this operation's whole premise (stop hand-maintaining config that rots) argues against. `build init --print-template` already renders the template to stdout without writing a file, so it is the natural create-from-code replacement and cannot drift from what `build init` actually writes. The embedded template stays a text file rather than being code-generated because its bulk is stable, comment-rich boilerplate (ticketing/review/ship/project); only the model tiers were volatile, and discovery plus aliases handle that volatility in Brief 01 and Plan B.

OOS:
- Refreshing the `docs/state-of-the-system/*` references to the example (those docs already self-flag drift against HEAD; a full state-of-the-system refresh is a separate housekeeping pass)
- Code-generating the whole template instead of keeping it as an embedded text file (rejected; see Notes)

## Plan B: Codex discovery and refresh

### Goal

After this plan, `build init` queries Codex for its current models and reasoning levels and writes a correct, current `[workers.codex.sizes]` block with a discovered-menu comment, while Claude's block stays static aliases; and a new `build models refresh` reconciles an existing config against a fresh probe by printing a current-to-proposed diff and overwriting only the Codex block. A probe failure is non-fatal at init (sensible defaults are still written, with a warning) and actionable at refresh (stops, file untouched).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | codex-model-probe | A component runs `codex debug models` and parses models + effort levels | A | src/ThroughlineBuild.Workers.Codex/ (new probe), src/ThroughlineBuild.Workers.Codex/CodexJsonDtos.cs, tests/ |
| 05 | init-discovers-codex-tiers | init enriches the Codex block from the probe; static fallback + warning on failure | 04 | src/ThroughlineBuild.Cli/InitCommand.cs, src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs, tests/ |
| 06 | build-models-refresh-verb | A new `build models refresh` verb diffs and overwrites the Codex block | 05 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, tests/ |

### Briefs - detail

#### Brief 04: codex-model-probe

Goal: A reusable component invokes `codex debug models`, parses its JSON, and returns the list-visible model slugs with each model's supported reasoning-effort levels and default level - or a typed failure when the command cannot be run or its output cannot be parsed - giving init and refresh a single source of discovered truth.

Inputs: the JSON shape of `codex debug models` (`.models[]` entries with `slug`, `default_reasoning_level`, `supported_reasoning_levels[].effort`, and `visibility`, where `visibility == "list"` marks operator-selectable models and `hide` marks internal ones). The existing Codex subprocess and AOT JSON patterns in `src/ThroughlineBuild.Workers.Codex/CodexAgent.cs` and `src/ThroughlineBuild.Workers.Codex/CodexJsonDtos.cs`. The AOT source-gen JSON convention (no reflection serialization).

Outputs:
- A probe component (e.g. `CodexModelProbe`) that spawns `codex debug models`, captures stdout, and deserializes it via a source-generated JSON context.
- A typed discovery result: model slugs filtered to `visibility == "list"`, each with its supported effort levels and default effort.
- A typed, non-throwing failure result that distinguishes "command failed or executable not found" from "output unparseable", carrying stderr or the first output bytes for the caller's message.
- AOT publish succeeds; the new DTOs are registered in a source-gen JSON context.

Acceptance:
- [ ] A representative `codex debug models` payload parses into the discovery result with per-model effort levels and default
- [ ] Hidden-visibility models are excluded from the result
- [ ] A non-zero exit or missing executable returns the typed failure rather than throwing
- [ ] Unparseable output returns the typed failure with diagnostic detail
- [ ] AOT publish succeeds
- [ ] dotnet test green

Notes: The probe is split from its consumers so it can be unit-tested against captured JSON without spawning a process, mirroring how the agents factor envelope parsing out of process spawning. Visibility filtering lives in the probe so init and refresh see one curated set and neither re-implements it. The failure is a typed result rather than an exception because the two callers want opposite policies on the same failure - init warns and continues, refresh stops with guidance - and a typed result lets each decide.

OOS:
- Claude discovery (Claude uses static aliases; there is no probe)
- Writing or mutating any config file (Briefs 05 and 06)
- Mapping discovered models onto small/medium/large (Brief 05 owns that heuristic)

#### Brief 05: init-discovers-codex-tiers

Goal: `build init` produces a config whose Codex tier block reflects the machine's actual Codex install - a best-guess small/medium/large mapping drawn from the probe plus a two-line discovered-menu comment - while Claude's block stays the static aliases. If the probe fails, init still writes the template's static Codex defaults and prints a warning, so init never hard-fails on a missing or unauthenticated Codex.

Inputs: `src/ThroughlineBuild.Cli/InitCommand.cs` (the load-template -> apply-flags -> write flow, ~35-63). The probe from Brief 04. The static Codex block in the template as the fallback. The `# models:` / `# effort:` comment shape the operator edits against. `src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs` for how the template is loaded.

Outputs:
- `InitCommand` runs the probe after loading the template and, on success, replaces the Codex sizes block with a best-guess mapping (a mini-class model -> small, the remaining models -> medium/large by capability order, effort taken from each model's default with `large` escalated) and injects the `# models:` and `# effort:` comment above the block.
- On probe failure, the static template Codex block is written unchanged and a single warning is printed naming the likely cause (Codex not installed or not logged in) and pointing at `build models refresh`; exit code stays 0.
- The Claude block is written from static aliases regardless of probe outcome.
- `--print-template` remains probe-failure-safe (it does not require a live probe).

Acceptance:
- [ ] init with a successful probe writes a Codex block matching the discovered models, with a `# models:` / `# effort:` comment
- [ ] init with a failed probe writes the static Codex defaults plus one actionable warning and exits 0
- [ ] The Claude block is the static aliases in both the success and failure cases
- [ ] `--print-template` produces output without depending on a successful probe
- [ ] dotnet test green

Notes: init stays non-fatal on probe failure because the template's Codex defaults are themselves valid and useful - blocking project setup on a Codex query would be a worse failure than a slightly stale tier the operator can refresh later. The mapping is a documented best guess, not an authority: the discovered menu is emitted as a comment precisely so the operator can re-tune model and effort by hand without guessing valid slugs. `--print-template` must stay offline-safe because tests and operators use it without a configured Codex.

OOS:
- The refresh verb (Brief 06)
- Interactive tier selection (init writes a non-interactive best guess plus the comment menu)
- Probing or pinning Claude

#### Brief 06: build-models-refresh-verb

Goal: An operator can re-run discovery against an existing config with `build models refresh`: the command probes Codex, computes the proposed tier mapping, prints a current-to-proposed comparison, and overwrites the `[workers.codex.sizes]` block and its discovered-menu comment in place, so a config stays current as Codex's lineup changes. A probe failure stops with an actionable message and leaves the config byte-unchanged.

Inputs: the verb dispatch chain and arg pre-passes in `src/ThroughlineBuild.Cli/Program.cs` (the `if (verb == ...)` blocks; the `init` handler at ~215-229 as the pattern for a config-touching verb that dispatches before normal config load). `src/ThroughlineBuild.Cli/CliUsage.cs` for help text. The probe (Brief 04). The Codex-block writing logic from Brief 05. `BuildConfigLoader.FindConfigFile` for locating the existing `.build/config.toml`.

Outputs:
- A new models-refresh verb (operator form `build models refresh`) wired into Program.cs dispatch and documented in `CliUsage.cs`.
- The command locates the existing config, runs the probe, computes the proposed Codex mapping, and prints a current-to-proposed table of each tier's model and effort.
- It overwrites only the `[workers.codex.sizes]` block and its menu comment in place, leaving the Claude block, ticketing, and every other section untouched.
- On probe failure it prints the actionable Codex message and makes no file change.
- AOT publish succeeds.

Acceptance:
- [ ] `build models refresh` with a probe result that differs from the file prints a current-to-proposed diff and rewrites the Codex block plus comment
- [ ] A refresh whose probe matches the file reports up-to-date and makes no change
- [ ] A failed probe prints the actionable message and leaves the file byte-unchanged
- [ ] Only the Codex sizes block and its comment change; the Claude block and other sections are preserved verbatim
- [ ] The verb follows existing CLI dispatch and unknown-arg conventions
- [ ] AOT publish succeeds
- [ ] dotnet test green

Notes: Refresh is deliberately not idempotent in the regenerate sense - Codex's model lineup changes over time, so the same command can legitimately produce different output on different days; the current-to-proposed diff is the safety surface, making every change visible before it is written rather than relying on a silent merge. The block is rewritten as a surgical in-place edit of one section, not by regenerating the whole file, so an operator's hand edits elsewhere survive. The command runs in the early pre-config-load dispatch band like `init`, because it manipulates the config file rather than consuming it.

OOS:
- Refreshing or pinning Claude tiers (static aliases need no refresh)
- A non-Codex models subcommand surface (only refresh is in scope)
- Auto-applying without showing the diff (the current-to-proposed view is mandatory)

## What done looks like

An operator who runs `build init` on a machine with Codex installed gets a `.build/config.toml` whose Codex tiers reflect that machine's actual current models and reasoning levels, with the discovered menu sitting in a comment for easy hand-tuning, while the Claude tiers read `haiku`/`sonnet`/`opus` and keep working the day a new Claude model ships. When Codex's lineup changes, `build models refresh` shows exactly what it would change and rewrites only the Codex block. No model version string is hardcoded in agent code, an L-sized ticket runs the `large` tier exactly as before, a Codex tier's reasoning effort reaches both the CLI and the event log, and the `claude-opus-4-7` that triggered this work appears nowhere in the shipped template. There is one config reference, not two: the redundant `.build/config.toml.example` is gone, and `build init --print-template` regenerates it from code on demand. The init template no longer advertises `default_model` as a worker-model lever, so setting it can no longer create the illusion of controlling which model a worker runs.
