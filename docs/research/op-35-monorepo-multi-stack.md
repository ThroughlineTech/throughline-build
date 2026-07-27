# Operation: monorepo-multi-stack

Make "stack" a plan-level attribute so a single Throughline Build operation can span a monorepo with more than one toolchain - for example a native iOS app and a web app in the same repo, each with its own build/test commands, convention bundle, review/ship check suite, and working directory. The engine stays stack-agnostic: it routes toolchain, convention, and check selection by a stack id that lives in derived data, and never hardcodes iOS or web.

This is a stashed plan. The work is sequenced so Plan A lands the data model the engine reads at runtime, and Plan B wires stacks through scaffold-time attribution and the phases. Nothing here is required to run a single-stack operation, and the single-stack path must stay byte-for-byte unchanged.

## Why this exists

Throughline Build assumes one toolchain per operation. There is one `ProjectContext` (one language, one build command, one test command), one `convention_files` bundle inlined into every implement brief, and one review/ship check suite that runs from a single working directory against every brief's branch. That is correct for a single-stack repo and wrong for a monorepo: a native iOS app and a web app have two of each, rooted in different subdirectories, driven by completely different tools (`xcodebuild` / `swift test` / SwiftLint vs `npm run build` / `vitest` / `tsc`).

Today the only way to model a two-stack repo is two separate operations with no shared dispatch graph - so you cannot express "a shared-contract brief, then an iOS brief and a web brief that both depend on it" in one operation, and you maintain two op-docs and two configs by hand.

Option B resolves this without teaching the engine about any specific language. An operation declares a stacks registry (stack id -> toolchain, conventions, working directory, path prefixes); each plan names its stack; every brief inherits its plan's stack. At implement time a brief uses its stack's toolchain and conventions; at gate/ship time its checks run from its stack's working directory, and ship regression routes a touched subtree to that stack's suite. The "which paradigm" decision becomes data attached at the smallest stack-uniform unit (the brief, grouped by plan), and the engine keeps doing what it already does - running whatever the derived data declares.

Non-negotiable invariant, carried into every brief's acceptance criteria: an operation with no stacks declared behaves exactly as it does today. The stacks model is purely additive - one implicit default stack stands in for the legacy single-profile path.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Stack model + config schema | - | M |
| B | Stack attribution + routing | A | L |

Plan A introduces the `StackProfile` type, the keyed `ProjectContext`, the config schema that supplies multiple stacks, and the stack/working-directory tags on checks - the contract everything downstream front-loads against. Plan B extends the op-doc/scaffold format to author and attribute stacks, then routes selection by stack through the implement, gate, and ship phases, and documents the result. Plan B's dependency on Plan A rides the plan-level A edge; brief-level Deps below stay within their own plan.

## Plan A: Stack model + config schema

### Goal

Introduce a typed, keyed representation of "a stack" (its toolchain, conventions, working directory, and path ownership), let `.build/config.toml` supply more than one, and let checks carry a stack and a working directory - all while a config that declares no stacks still yields exactly one default stack with today's behavior. This plan changes data shapes and parsing, not phase behavior.

### Briefs

| # | Slug | Intent | Deps | Effort |
|---|------|--------|------|--------|
| 01 | stack-profile-model | Introduce `StackProfile` and turn the single `ProjectContext` into a keyed collection plus a path-to-stack resolver with a default-stack fallback | - | M |
| 02 | config-stacks-schema | Let `.build/config.toml` declare a stacks table-array and parse it into the keyed collection; no stacks block yields one implicit default stack | 01 | S |
| 03 | check-spec-stack-workdir | Give `CheckSpec` an optional stack tag and working directory; the checks runner runs each check from its own cwd; untagged checks are cross-cutting | 02 | M |

### Briefs - detail

#### Brief 01: stack-profile-model

Goal: Introduce a `StackProfile` value type and turn the single per-operation toolchain into a keyed collection with a resolver, so the rest of the engine can ask "which stack owns this path / this brief" instead of assuming one global toolchain.

Inputs: The current single-profile holder `ProjectContext` (`Language`, `Framework`, `PackageManager`, `BuildCommand`, `TestCommand`, `InstallCommand`, `DevCommand`) in `src/ThroughlineBuild.Briefs/ProjectContext.cs`. No stack concept exists today.

Outputs:
- A new `StackProfile` type carrying a stack id, the toolchain fields (language, framework, package manager, install/build/test/dev commands), a `convention_files` list, a working directory, and path-prefix patterns identifying the subtree the stack owns.
- `ProjectContext` becomes a keyed collection of `StackProfile` plus a resolver: lookup-by-id, resolve-by-path, and a designated default stack.
- A single-stack fallback so that when exactly one stack exists the resolver returns it for every path.

Acceptance:
- [ ] `StackProfile` carries id, toolchain commands, `convention_files`, working directory, and path prefixes
- [ ] `ProjectContext` exposes lookup-by-stack-id and resolve-by-path
- [ ] with exactly one stack defined, resolve-by-path returns that stack for any path (today's behavior)
- [ ] existing single-profile call sites still resolve through a default-stack accessor

Notes: this is the load-bearing contract brief - later briefs front-load against `StackProfile`'s shape. No behavior change beyond introducing the type and resolver; config still supplies one stack until Brief 02.

Out of scope:
- Reading multiple stacks from config (Brief 02 owns it)
- Any phase routing (Plan B)
- Path-prefix conflict resolution UI or precedence rules beyond a documented default

#### Brief 02: config-stacks-schema

Goal: Let `.build/config.toml` declare more than one stack, parsed into the keyed collection from Brief 01, with a no-stacks config still yielding exactly one implicit default stack.

Inputs: The `[project]` reader in `src/ThroughlineBuild.Cli/Config.cs` (maps the single toolchain into `ProjectContext` today). `StackProfile` and the keyed `ProjectContext` from Brief 01.

Outputs:
- A stacks table-array in `config.toml`, each entry supplying a stack's id, toolchain commands, `convention_files`, working directory, and path prefixes.
- Parsing that populates the keyed `ProjectContext`.
- Back-compat: a config with no stacks block produces one implicit default stack from the existing `[project]` fields.

Acceptance:
- [ ] a two-stack config parses into two `StackProfile`s addressable by id
- [ ] a legacy config (no stacks block) parses into exactly one default stack with today's values
- [ ] duplicate or malformed stack ids are rejected with a clear error

Notes: keep the legacy `[project]` shape valid - the stacks block is additive, not a replacement.

Out of scope:
- Per-check stack tagging (Brief 03)
- Scaffold-side authoring of the stacks block (Brief 04)

#### Brief 03: check-spec-stack-workdir

Goal: Tag review and ship checks with a stack and a working directory so the checks runner can run each check from the right subdirectory, with untagged checks treated as cross-cutting (run regardless of stack).

Inputs: `CheckSpec` (`Name`, `Executable`, `Arguments`, `Timeout`, `CheckRole`, `Canary`) in `src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs`; the single-`WorkingDirectory` executor `AutomatedChecksRunner` in `src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs`; the `[[review.checks]]` / `[[ship.regression_checks]]` readers in `src/ThroughlineBuild.Cli/Config.cs`; the runner invocation in `src/ThroughlineBuild.Phases/GatePhase.cs`.

Outputs:
- `CheckSpec` gains an optional stack id and working directory.
- The runner executes each check from its own working directory.
- The review/ship config readers parse stack and working directory per check.
- An untagged check is cross-cutting (runs from repo root, regardless of stack).

Acceptance:
- [ ] a check tagged with a stack runs from that stack's working directory
- [ ] an untagged check runs from repo root as today (cross-cutting)
- [ ] legacy config with no stack/workdir on checks behaves exactly as today

Notes: this is the data-and-runner half. Deciding which checks fire for which brief (and routing ship regression by touched subtree) lands in Brief 07.

Out of scope:
- Choosing which checks fire per brief or per touched subtree (Brief 07)
- Parallelizing checks across stacks

## Plan B: Stack attribution + routing

### Goal

Let an operation author declare its stacks and assign each plan a stack, carry that assignment onto every scaffolded ticket, and route toolchain, convention, and check selection by a brief's stack through the implement, gate, and ship phases - then document the model with a worked two-stack example. Throughout, a single-stack operation routes everything to the one default stack and behaves as it does today.

### Briefs

| # | Slug | Intent | Deps | Effort |
|---|------|--------|------|--------|
| 04 | op-doc-stack-format-and-deriver | Extend the op-doc/op-scaffold format with an operation stacks registry and a per-plan `Stack`; teach the profile deriver to emit the registry | - | M |
| 05 | scaffold-ticket-stack-tagging | At scaffold time, tag every brief ticket with its plan's stack so downstream phases can read it | 04 | M |
| 06 | implement-phase-stack-routing | `ImplementPhase` uses the brief's stack toolchain and inlines that stack's conventions | 05 | M |
| 07 | gate-ship-stack-routing | Gate and ship run a brief's stack checks in its working dir; ship regression routes touched subtrees to their stack's suite | 05 | M |
| 08 | monorepo-docs-and-example | Document the stack model and add a worked two-stack config and op-doc example | 06, 07 | S |

Plan B depends on Plan A at the plan level; the brief Deps above reference same-plan briefs only. Brief 07 additionally consumes Brief 03's `CheckSpec` stack/workdir via the Plan A edge.

### Briefs - detail

#### Brief 04: op-doc-stack-format-and-deriver

Goal: Extend the op-doc / op-scaffold input format so an operation can declare a stacks registry and each plan can name its stack, and teach the profile deriver to emit that registry from op-doc prose.

Inputs: The op-doc format (Dispatch order table, Plan headers) and the op-scaffold input spec; the single-profile deriver `ScaffoldProfileDeriver` in `src/ThroughlineBuild.Scaffold/ScaffoldProfileDeriver.cs`; the single `ProjectProfile` (with `convention_files`) in `src/ThroughlineBuild.Scaffold/ProjectProfile.cs`.

Outputs:
- A format extension: an operation-level stacks registry (stack id -> toolchain, conventions, working directory, path prefixes) and a per-plan `Stack:` attribute.
- The derived profile gains the multi-stack registry plus the per-plan stack assignment.
- The deriver emits the registry and each plan's stack from the op-doc.

Acceptance:
- [ ] an op-doc can declare two stacks and assign each plan a stack
- [ ] the deriver emits a stacks registry that maps onto Brief 02's config schema
- [ ] a single-stack op-doc with no stacks block still derives one default stack
- [ ] the format additions are additive - existing op-docs still validate

Notes: additive format extension only - do not relax any existing op-scaffold hard-reject. The registry shape must round-trip into the config schema from Brief 02.

Out of scope:
- Tagging individual tickets with their stack (Brief 05)
- Any runtime routing (Briefs 06, 07)

#### Brief 05: scaffold-ticket-stack-tagging

Goal: When op-scaffold creates the ticket hierarchy, tag every brief ticket with its plan's stack so the downstream phases can read it.

Inputs: The per-plan `Stack` attribute and stacks registry from Brief 04; the scaffold ticket-creation path; the existing mechanism by which tickets carry metadata.

Outputs:
- Scaffold writes each ticket's owning stack id onto the ticket (carried via ticket metadata).
- A brief with no stack (single-stack operation) inherits the default stack.

Acceptance:
- [ ] each scaffolded brief ticket carries its plan's stack id
- [ ] single-stack operations tag every ticket with the default stack (or leave it untagged, treated as default) with unchanged downstream behavior
- [ ] tickets remain consumable by the existing phases, which ignore the tag until Briefs 06 and 07 wire it

Notes: pure attribution - no phase reads the tag yet.

Out of scope:
- Phases consuming the tag (Briefs 06, 07)

#### Brief 06: implement-phase-stack-routing

Goal: `ImplementPhase` resolves a brief's stack and uses that stack's toolchain commands and convention bundle, instead of the single global profile.

Inputs: The keyed `ProjectContext` and resolver from Brief 01; the ticket stack tag from Brief 05; `ImplementPhase` in `src/ThroughlineBuild.Phases/ImplementPhase.cs` (calls `PreloadedContextBuilder.Build` with the single `ProjectContext` today); `PreloadedContextBuilder` in `src/ThroughlineBuild.Briefs/PreloadedContextBuilder.cs` and `ProjectContext.ConventionFiles`.

Outputs:
- `ImplementPhase` selects the `StackProfile` for the brief's stack and uses its build/test/install/dev commands.
- `PreloadedContextBuilder` inlines that stack's `convention_files` rather than a global merge.

Acceptance:
- [ ] a brief tagged with one stack implements with that stack's toolchain and conventions; a brief tagged with the other uses the other's
- [ ] a single-stack operation uses the default stack's toolchain and conventions exactly as today
- [ ] a brief's preloaded context contains only its stack's conventions

Notes: depends on the ticket carrying its stack (Brief 05) and the keyed model (Plan A).

Out of scope:
- Gate and ship check routing (Brief 07)

#### Brief 07: gate-ship-stack-routing

Goal: Gate and ship run a brief's stack checks from that stack's working directory, and ship regression routes a touched subtree to its stack's suite while still running cross-cutting checks.

Inputs: The `CheckSpec` stack/workdir and runner from Brief 03; the ticket stack tag from Brief 05; `GatePhase` in `src/ThroughlineBuild.Phases/GatePhase.cs` (invokes the runner); the ship-phase regression path; the path-prefix -> stack map from the stacks registry / resolver.

Outputs:
- `GatePhase` runs the brief's stack checks (plus cross-cutting checks) from the right working directory.
- The ship regression path selects suites by which stack's subtree the change touched, via the path-prefix map, always including cross-cutting checks.

Acceptance:
- [ ] a brief gated against its stack's checks runs them from that stack's working directory; cross-cutting checks still run
- [ ] a change touching only one stack's subtree runs that stack's regression suite plus cross-cutting checks, not the other stack's
- [ ] a change touching both subtrees runs both suites
- [ ] single-stack operations run the full suite from repo root as today

Notes: consumes Brief 03's `CheckSpec` stack/workdir via the Plan A edge, plus the ticket stack tag (Brief 05) and the path-prefix map.

Out of scope:
- Parallelizing cross-stack ship regression
- Per-file check selection finer than path-prefix ownership

#### Brief 08: monorepo-docs-and-example

Goal: Document the multi-stack model and give a worked two-stack example so future agents and operators can author monorepo operations.

Inputs: `docs/state-of-the-system/*` (configuration, contracts, lifecycle sections); the existing single-stack `config.toml` and op-doc examples under `docs/`.

Outputs:
- State-of-the-system docs updated for stacks (config schema, check routing, convention selection, working directories).
- A worked two-stack `config.toml` example (for example, an `ios` stack and a `web` stack).
- A worked op-doc example showing a stacks registry plus per-plan `Stack`.

Acceptance:
- [ ] docs describe how stack selection drives toolchain, conventions, checks, and working directory
- [ ] the example config and op-doc are internally consistent with the schema from Briefs 02 and 04
- [ ] the single-stack-is-unchanged invariant is stated explicitly

Notes: docs-only; lands after the behavior it describes.

Out of scope:
- Rewriting unrelated state-of-the-system sections

## What done looks like

After all eight briefs land:

- An operator can declare two stacks in one operation (for example `ios` and `web`), assign each plan a stack, and scaffold a single dispatch graph that spans both - including a shared-contract brief that an iOS brief and a web brief both depend on.
- Each brief implements with its stack's toolchain and sees only its stack's conventions in preloaded context.
- Gate runs a brief's stack checks (plus cross-cutting checks) from the correct working directory; ship regression routes a touched subtree to that stack's suite and still runs cross-cutting checks.
- No language knowledge is hardcoded in the engine - every stack fact (commands, conventions, working directory, path ownership) lives in derived data: the config schema and the op-doc stacks registry.
- An operation that declares no stacks behaves exactly as it does today: one implicit default stack, repo-root working directory, the full check suite, the single convention bundle. The single-stack path is unchanged.
