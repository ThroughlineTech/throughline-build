# Plan: Ticketing-backend generalization + agent-facing `build` CLI

**Date:** 2026-06-08
**Status:** Proposed (plan doc; no code changes yet)
**Goal:** Replace the old claude-config markdown slash commands (which talk to Plane via
`.claude/plane-rest` + a hand-maintained `plane-config.md` + LLM prose-to-Plane mapping) with
thin wrappers that shell out to the `build` binary. To get there, finish generalizing the
ticketing seam, prove it against a second live backend, and give `build` a structured JSON
input/output surface that agents can drive deterministically.

## Decisions locked (2026-06-08)

- **Delivery vehicle: extend `build`.** No new standalone exe. The `ITicketing` seam, the Plane
  client, the AOT publish pipeline, config, auth, and rate-limiting all already live in `build`.
  Agents reach the ticketing system by calling `build <verb> --json`.
- **This task's output: this plan doc only.** No code changes in this pass.
- **Scope: add a real second backend now.** Generalize the interface AND ship one more live
  adapter, so the abstraction is validated against two backends rather than asserted against one.

## Second backend: Linear (locked 2026-06-08)

A complete feasibility study already exists
([linear-integration-feasibility.md](linear-integration-feasibility.md)): ~3-5 days for the
adapter + ~1 day for the HTML/Markdown shim + state-map config. It validates the *hardest*
impedance mismatch (GraphQL transport + Markdown content), so if the seam survives Linear it
survives anything. No new AOT risk (uniform `{data, errors}` envelope is easier to source-gen
than Plane's varied REST shapes). GitHub Issues (cheaper, weaker test) and Jira (heaviest, ADF
content) remain documented follow-on candidates.

### Onboarding comes first (a precursor op-doc, not part of the adapter op-doc)

**Before the day-to-day Linear adapter, `build init` and `build setup` must learn Linear.** You
cannot test a `LinearTicketingClient` against a real Linear team until onboarding can *create and
provision* that team. The onboarding surface is also the most Plane-shaped code in the tree:

- **`build init`** ([InitCommand.cs](../src/ThroughlineBuild.Cli/InitCommand.cs)) prompts for
  `Plane base URL -> workspace slug -> API token`, then create-or-pick a Plane *project*, and
  substitutes `REQUIRED_PLANE_*` placeholders into `.build/config.toml`. Linear differs on every
  axis: the endpoint is fixed (`https://api.linear.app/graphql`), there is no "workspace slug" to
  enter, "project" maps to a Linear **Team** (with a `key` like `TLB`), and auth is
  `Authorization: <key>` (no `Bearer`). The UX goal is preserved - the operator never types a UUID
  - but the prompts and the config template are backend-specific.
- **`build setup`** ([SetupCommand.cs](../src/ThroughlineBuild.Cli/SetupCommand.cs)) provisions
  states and labels through `ITicketingProvisioner`. Its `CreateStateAsync(name, group, sequence)`
  encodes Plane's "state group + display sequence" model. Linear states carry a **category**
  (backlog/unstarted/started/completed/cancelled/triage), created via `workflowStateCreate`, with
  no display sequence - so the provisioner contract itself has to generalize.

This onboarding work is a **self-contained vertical slice**: it needs the backend factory, the
Linear *connection* config, and only the thin facets of the Linear client used to onboard
(`ITicketingProvisioner`, `ITicketingConnectivity`, `IProjectDiscovery` for team create-or-pick) -
**not** the full `ITicketing` ticket-CRUD surface and **not** the content-format refactor (onboarding
writes no descriptions or comments). It therefore ships as its own op-doc *before* the adapter
op-doc, and it proves auth + transport + provisioning end to end first. See W1.3 below.

---

## Current state (what already exists)

Confirmed by reading HEAD, not the docs:

- **`ITicketing` is already a clean, backend-neutral interface.**
  [src/ThroughlineBuild.Contracts/ITicketing.cs](../src/ThroughlineBuild.Contracts/ITicketing.cs)
  is the only ticketing surface the commands and phases touch. ~16 async methods plus
  `BackendCapabilities`, `TicketQuery`, `LifecycleTransition`, `ChildTicketSpec`, etc.
- **Plane is fully contained** in the `ThroughlineBuild.Plane` library (`OutputType=Library`,
  references only `Contracts`). `PlaneTicketingClient` implements `ITicketing`,
  `ITicketingProvisioner`, `ITicketingConnectivity`, and `IProjectDiscovery`. Nothing
  Plane-specific leaks outside that project except as noted under "gaps" below.
- **A neutral domain model exists**: `Ticket`, `TicketState`, `Relation`, `TicketComment`,
  `NewTicketResult` in `ThroughlineBuild.Contracts.Models`. Translation happens at the Plane
  boundary (`ToTicketAsync`), not in the command layer.
- **The full lifecycle verb surface is already in `build`**: `new`, `plan`, `implement`,
  `review`, `ship`, `chain`, `rework`, `decompose`, `scaffold`, `list`, `amend`, `close`,
  `defer`, `reopen`, `init`, `setup`, `settarget`, plus help/op-doc/models utilities
  (see [src/ThroughlineBuild.Cli/CliUsage.cs](../src/ThroughlineBuild.Cli/CliUsage.cs)).
- **`--summary-json`** already exists on the phase verbs (plan/implement/review/ship/chain), so
  there is precedent for a machine-readable output mode.

So request #1 ("make Plane a library with a generic interface") is roughly 70% done already.
The remaining work is three concrete gaps plus the new adapter.

### The three gaps that block a second backend

1. **No backend factory.** `[ticketing].backend` is read into `TicketingConfig.BackendName`
   ([Config.cs:10,542](../src/ThroughlineBuild.Cli/Config.cs)) but **never branched on**. All
   ticketing clients are constructed as `new PlaneTicketingClient(...)` directly - 7 sites:
   [Program.cs:477,522,569,671,955,1057](../src/ThroughlineBuild.Cli/Program.cs) and
   [InitCommand.cs:343,419](../src/ThroughlineBuild.Cli/InitCommand.cs). This is the entire
   wiring change: a factory that returns `ITicketing` (and the optional interfaces) based on
   `BackendName`.

2. **The content model is HTML-native and leaks the format.** Four `ITicketing` methods take
   HTML: `AppendDescriptionAsync(id, html)`, `CreateCommentAsync(id, html)`,
   `UpdateDescriptionAsync(id, html)`, `CreateTicketAsync(..., descriptionHtml, ...)`. Worse,
   markers are embedded *as HTML* and parsed *as literal HTML strings*: `ReopenCommand` does
   `commentBody.Contains("<strong>deferred:</strong>")` and `"<strong>wontfix:</strong>"`
   ([ReopenCommand.cs:65,70](../src/ThroughlineBuild.Commands/ReopenCommand.cs)). The `Ticket`
   model exposes `DescriptionHtml`, read by every brief builder
   (`PlanBriefBuilder`, `ImplementBriefBuilder`, `ReviewBriefBuilder`, `DecomposeBriefBuilder`).
   A Markdown backend (Linear, GitHub) cannot satisfy this without a translation layer, and the
   marker-parse-by-HTML-string approach breaks outright.

3. **Hardcoded Plane state names + Plane-shaped provisioning.** `PlaneTicketingClient` hardcodes
   the 7 state names (Backlog, Planning, Ready, In Progress, In Review, Done, Cancelled), and
   `ITicketingProvisioner.CreateStateAsync(name, group, sequence, ...)` leaks Plane's "state
   group" and "display sequence" concepts. The logical-state -> backend-state-name map must
   become config-driven so other backends (which name states freely) can map onto the neutral
   `TicketState` enum.

---

## Workstream 1: Generalize the seam + add a second backend

**Outcome:** `build` can target Plane or Linear by flipping `[ticketing].backend`, with no change
to any command or phase. Two adapters exercise the same contract through the same golden tests.

This workstream splits into **two op-docs**: an **onboarding precursor** (W1.1-W1.3: factory,
connection config, and the `init`/`setup` generalization plus the thin Linear onboarding client)
and the **adapter op-doc** (W1.4-W1.6: content-format refactor, full Linear CRUD adapter, contract
tests). The precursor must land first - it is what makes a Linear team exist to test against.

### W1.1 - Backend factory (small, mechanical, do first)

- Add `ITicketingFactory` (or a static `TicketingClientFactory.Create(config, httpFactory)`)
  that branches on `BackendName` and returns the composite client. Keep returning the optional
  interfaces (`ITicketingProvisioner`, `ITicketingConnectivity`, `IProjectDiscovery`) via
  capability checks / `as` casts at the call sites that need them.
- Replace all 9 `new PlaneTicketingClient(...)` sites with the factory. Audit which ones need
  the provisioner/discovery facets (InitCommand and `setup` do; the phase verbs do not).
- No behavior change for Plane. This phase is shippable on its own and de-risks everything after.

### W1.2 - Connection config + logical-state map (config only; precursor needs it)

- Add Linear connection keys to `[ticketing]` and a logical-state -> backend-state-name map, e.g.:
  ```toml
  [ticketing]
  backend = "linear"
  linear_api_token_env = "LINEAR_API_KEY"
  linear_team_key      = "TLB"     # the operator picks/creates this in init; never typed as a UUID

  [ticketing.linear.states]
  Planning = "Planning"
  Ready    = "Ready"
  InReview = "In Review"
  # ...
  ```
- Plane keeps its current hardcoded names as defaults (zero-diff for existing workspaces); Linear
  supplies its own map. Mirror the secret-resolution path already used for `plane_api_token`
  ([Config.cs](../src/ThroughlineBuild.Cli/Config.cs)). This also future-proofs the Plane side.

### W1.3 - PRECURSOR OP-DOC: Linear onboarding (`init` + `setup`)

The vertical slice that makes a Linear team exist and be workflow-ready. Ships before the adapter.

- **Generalize `build init`:** factor the Plane-specific interactive flow (base URL -> workspace ->
  token -> create/pick project) behind a backend-keyed onboarding strategy. The Linear strategy
  prompts for `API token -> create-or-pick Team` (endpoint is fixed; no workspace slug; team `key`
  replaces project identifier), and writes the Linear config template (W1.2 keys). Preserve the
  invariant that the operator never types a UUID. Keep the offline-template fallback on a blank
  token. The `REQUIRED_PLANE_*` template placeholders become backend-specific template selection.
- **Generalize `build setup`:** the state/label provisioning currently calls
  `ITicketingProvisioner.CreateStateAsync(name, group, sequence)`. Generalize the provisioner so
  the Plane-only `group`/`sequence` parameters become backend-private (recommend a neutral
  `EnsureStatesAsync(IEnumerable<logical-state>)` plus `EnsureLabelsAsync(...)` that each adapter
  satisfies its own way). The setup driver reads the W1.2 state map to know which logical states to
  ensure; the standard label set (`risk:*`, `size:*`, `plan-ticket`, `stub`, `delegated`) maps
  directly to Linear `issueLabelCreate`. Keep `--check` (verify-only, exit 1 on missing) and
  idempotency.
- **Thin Linear onboarding client:** a `LinearTicketingClient` that implements ONLY
  `ITicketingProvisioner` + `ITicketingConnectivity` + `IProjectDiscovery` (team list/create,
  state/label list/create, connectivity probe) - the GraphQL transport, `LinearClientOptions`,
  `LinearJsonContext` source-gen, `LinearApiException`, and retuned throttle are introduced here and
  reused by the full adapter later. The ticket-CRUD methods can throw `NotSupportedException` until
  W1.5 fills them in (the onboarding paths never call them).
- **Exit of the precursor:** `build init` then `build setup --check` succeeds against a fresh Linear
  team, with `[ticketing].backend = "linear"`. No ticket has been created yet - that is W1.5.

### W1.4 - Content-format abstraction (the load-bearing refactor)

This is the single biggest porting cost (the feasibility doc flags it as "the big one"). Two
viable shapes - recommend B:

- **Option A (boundary translation):** keep `ITicketing` HTML-typed; the Markdown adapter runs an
  HTML->Markdown shim on write and Markdown->HTML on read. Smallest interface diff, but every new
  backend re-pays the shim and the HTML marker-parse bug (gap 2) stays latent.
- **Option B (format-neutral content type), recommended:** introduce a `TicketContent` value that
  carries the *semantic* payload (plain-text body + structured markers + a small set of rich
  spans), and let each adapter render it to its native format (HTML for Plane, Markdown for
  Linear/GitHub). Change the four HTML-typed methods to take `TicketContent`. Crucially, **lift
  the lifecycle markers** (`deferred:`, `wontfix:`, `planned_at: <sha>`, etc.) **out of HTML into
  format-agnostic tokens** so `ReopenCommand` and friends match on a token, not on
  `<strong>...</strong>`. This kills gap 2 at the root and is what the feasibility doc calls the
  "better long-term" fix.
- Rename/abstract `Ticket.DescriptionHtml`. The brief builders feed it to workers as context;
  workers handle HTML or Markdown fine, but the *field name and format* should be neutral
  (`Ticket.Description` + a `ContentFormat` enum, or a `RenderForWorker()` accessor).
- Scope guard: B is a real refactor touching Contracts + Plane + every marker read/write site.
  Land it behind the unchanged Plane behavior (golden tests must stay green) before any second
  adapter consumes it.

### W1.5 - Linear full CRUD adapter (`ThroughlineBuild.Linear`)

Fills in the ticket-CRUD half of the `LinearTicketingClient` started in W1.3, reusing its
transport, options, JSON context, exception type, and throttle. Per the feasibility doc:

- Complete `LinearTicketingClient : ITicketing` (the methods W1.3 stubbed): get/query, create,
  set-parent, transition, append/replace description, comment, labels, relations, rollup,
  bulk-create children. Content is Markdown (consumes W1.4's `TicketContent`).
- Hand-write GraphQL query strings as constants; POST `{query, variables}`; deserialize via a
  generic `GraphQLResponse<T>` envelope. Mirror `PlaneJsonContext` exactly in spirit.
- Map the neutral domain model onto Linear: project -> Team, `TLB-123` shorthand works for reads
  and writes, native sub-issues for parent/child, `issueRelationCreate` for blocked-by, labels
  for `size:*`. Identity: dedicated service-account API key (mirrors today's `X-API-Key`; zero OAuth).

### W1.6 - Two-backend contract tests

- The repo already has golden-snapshot infrastructure. Add Linear fixtures so both adapters run
  the identical contract suite (create -> transition -> comment -> relate -> rollup).
- A capability-matrix test: assert each adapter advertises `BackendCapabilities` honestly and that
  commands degrade gracefully where a capability is absent.

**W1 dependency order:** W1.1 -> W1.2 -> **W1.3 (precursor op-doc; ships here)** -> W1.4 -> W1.5 ->
W1.6. W1.1 and W1.2 are independently shippable. The precursor op-doc is W1.1 + W1.2 + W1.3; the
adapter op-doc is W1.4 + W1.5 + W1.6. W1.5 depends on W1.3 (transport) + W1.4 (content format).

---

## Workstream 2: Agent-facing JSON surface on `build`

**Outcome:** every operation a slash command needs is a single `build` verb with structured JSON
in and structured JSON out, stable enough to parse without an LLM in the loop. This is requests
#2 and #3.

### W2.1 - Expose the missing CRUD primitives as verbs

These operations already exist on `ITicketing` but are NOT exposed as standalone CLI verbs (they
are only reachable indirectly through phases). They are exactly what the slash commands hand-roll
via `.claude/plane-rest` today. Add thin command wrappers:

| New verb | ITicketing call | Replaces slash-command hand-rolling of |
|---|---|---|
| `build get <id>` | `GetAsync` / `GetBatchAsync` | `plane-rest get-by-ident` |
| `build comment <id> <body>` | `CreateCommentAsync` | direct comment POST |
| `build transition <id> <state>` | `TransitionAsync` | direct state PATCH |
| `build label <id> <label>...` | `ApplyLabelsAsync` | label PATCH |
| `build relate <id> --blocked-by <id> \| --blocks <id> \| --relates <id>` | `AddRelationAsync` / `GetRelationsAsync` | relation POST/GET |
| `build set-parent <child> <parent>` | `SetParentAsync` | parent PATCH |
| `build comments <id>` | `GetCommentsAsync` | comment GET (marker scan) |

(Lifecycle verbs `close`/`defer`/`reopen` and `amend` already exist; they just need `--json`.)

### W2.2 - `--json` output on every read/query verb

- `build list --json`, `build get --json`, `build comments --json`, `build relate --json` (and
  the existing `--summary-json` family) emit a stable, versioned JSON envelope on stdout:
  ```json
  { "schemaVersion": 1, "ok": true, "data": { ... }, "warnings": [] }
  ```
  Errors emit `{ "schemaVersion": 1, "ok": false, "error": { "code": "...", "message": "..." } }`
  and set the existing exit code. Human/table output stays the default; `--json` is opt-in
  (mirrors the `--summary-json` precedent so we do not regress interactive use).
- `ListCommand` currently renders a table only
  ([ListCommand.cs](../src/ThroughlineBuild.Commands/ListCommand.cs)) - add the JSON branch there.
- Decide once: a global `--json` flag handled centrally in arg parsing vs per-verb. Recommend a
  **global `--json`** that each command honors, so the surface is uniform and discoverable.

### W2.3 - Structured JSON *input* (request #3: "json or html or md")

- `build new` today accepts a body file (md), free text, or stdin
  ([CliUsage](../src/ThroughlineBuild.Cli/CliUsage.cs)). Add a JSON-draft path: when the input
  parses as a `TicketDraft` object, build the ticket from structured fields instead of LLM
  prose-mapping. Proposed schema (one round-trip creates a fully-formed ticket):
  ```json
  {
    "title": "string",
    "type": "bug | feature | enhancement | ...",
    "description": "markdown or html body",
    "format": "md | html",
    "acceptanceCriteria": ["...", "..."],
    "labels": ["size:m", "app:web"],
    "parent": "TLB-12",
    "relations": [{ "kind": "blocked_by", "ref": "TLB-9" }]
  }
  ```
- Disambiguation rule: existing-file -> file mode (unchanged); input starting with `{` (or
  `--json`/`--format json`) -> draft-object mode; otherwise free-text mode (unchanged). The
  adapter renders `description` to its native format via W1.2, so the same JSON works on any
  backend.
- `build amend --json` accepts the same partial shape for edits.
- This is what lets `/ticket-new` stop doing prose-to-Plane mapping in the LLM: the slash command
  assembles a `TicketDraft` and hands it to `build` once.

### W2.4 - Determinism + stability guarantees

- JSON envelope is **versioned** (`schemaVersion`) and additive-only within a version.
- All JSON goes to **stdout**; diagnostics/progress go to **stderr**, so agents parse a clean
  stream. Exit codes keep their current contract
  ([CliUsage exit-code table](../src/ThroughlineBuild.Cli/CliUsage.cs)).
- AOT: every new DTO gets a source-gen `JsonSerializerContext` entry (no reflection serialization).

**W2 dependency order:** W2.1 and W2.2 are independent of Workstream 1 and can start immediately
against Plane. W2.3's "same JSON on any backend" claim depends on W1.2, but the Plane-only path
can land first.

---

## Workstream 3: Re-point the slash commands at `build`

**Outcome:** the claude-config `ticket-*` commands become thin orchestration over `build ... --json`
instead of `.claude/plane-rest` + `plane-config.md` + LLM mapping. This is the original ask.

- Inventory the 16 `ticket-*` commands in claude-config
  (`/c/Users/developer/src/projects/claude-config/commands/ticket-*.md`) and map each to its
  `build` verb(s): `ticket-new` -> `build new --json`; `ticket-list` -> `build list --json`;
  `ticket-investigate` -> `build plan`; `ticket-approve` -> `build implement`; `ticket-chain` ->
  `build chain`; `ticket-close/defer/reopen/amend` -> the matching verbs; etc.
- Each command keeps its UX (argument parsing, image-context prose, AskUserQuestion preview-profile
  flow) but delegates the *Plane mechanics* to `build`, parsing the JSON envelope instead of
  curling the REST API. The LLM stops being the integration layer.
- Retire `.claude/plane-rest` and the hand-maintained ID maps in `plane-config.md` once every
  command path is covered. `build`'s config (`.build/config.toml`) becomes the single source of
  backend truth; `/ticket-install` writes it via `build init` + `build setup`.
- Backend portability falls out for free: the same slash commands work against Linear the moment
  `[ticketing].backend` flips, because they only speak the neutral JSON envelope.

**W3 depends on** W2 (the verbs + JSON envelope it consumes). It is a claude-config repo change,
not a latticeflow change, so it ships separately and last.

---

## Sequencing summary

1. **W1.1 backend factory** - small, mechanical, no behavior change. Ship first; unblocks everything.
2. **W2.1 + W2.2 CRUD verbs + `--json` output** (Plane) - immediately useful to agents; independent of W1.
3. **W1.2 + W1.3 ONBOARDING PRECURSOR OP-DOC** - connection config + state map, then generalize
   `init`/`setup` and the thin Linear onboarding client. Exit: `build setup --check` green on a
   fresh Linear team. This is the precursor the user called for: Linear from the onboarding angle,
   before any ticket CRUD.
4. **W1.4 content-format abstraction** - the real refactor; land behind green Plane golden tests.
5. **W2.3 JSON ticket-draft input** - depends on W1.4 for cross-backend rendering.
6. **W1.5 + W1.6 ADAPTER OP-DOC** - full Linear CRUD adapter + two-backend contract tests; validates
   the whole seam end to end.
7. **W3 re-point slash commands** - claude-config change, ships last, against the stable envelope.

Each numbered step is independently shippable and leaves `build` working. The order front-loads the
cheap high-leverage wins (factory, JSON surface), proves Linear from onboarding first, and defers
the expensive refactor (content format) until its consumers exist.

## Risks and watch-items

- **Content-format refactor blast radius (W1.2).** Touches Contracts + Plane + every marker
  read/write. Mitigate: token-ize markers first, keep Plane golden tests green at every commit,
  do not let a second adapter consume the new type until Plane is fully migrated onto it.
- **JSON envelope churn.** Once slash commands parse it, the shape is load-bearing. Version it from
  day one and treat changes as additive.
- **AOT regressions.** Every new DTO needs a source-gen context entry; a missed one fails only at
  runtime in the published binary, not in `dotnet build`. Add a published-binary smoke test for the
  new `--json` verbs.
- **Onboarding/CRUD client split (W1.3 -> W1.5).** The thin onboarding client stubs ticket-CRUD with
  `NotSupportedException`. Risk: an onboarding path accidentally calls a stubbed method. Mitigate:
  the precursor's exit criterion exercises only `init` + `setup --check`, and the contract suite
  (W1.6) does not run until W1.5 fills the stubs.
- **Capability gaps.** Linear has typed relations + native sub-issues, so degradation risk is low
  here; the contract tests (W1.6) still assert each adapter advertises `BackendCapabilities`
  honestly rather than silently no-op'ing (today Plane warns-to-stderr-and-continues on a missing
  state name; keep that discipline).
- **Two sources of backend truth during W3.** Until `.claude/plane-rest` is retired, an agent could
  hit Plane two ways. Cut over per-command and delete the old path promptly.

## Open decisions to confirm

- **D1 - second backend: Linear.** RESOLVED 2026-06-08.
- **D2 - content model: Option A (boundary shim) vs Option B (neutral `TicketContent`, recommended).**
- **D3 - `--json` as a global flag (recommended) vs per-verb opt-in.**
- **D4 - one backend live at a time (hard switch on `[ticketing].backend`) vs both addressable per
  invocation.** Recommend hard switch for v1.

## Next step

Confirm D2-D4, then `build scaffold` this into Plane as **three op-docs**, in order:

1. **Onboarding precursor** (W1.1 + W1.2 + W1.3): backend factory, Linear connection config + state
   map, generalized `init`/`setup`, and the thin Linear onboarding client. Exit on a green
   `build setup --check` against a fresh Linear team.
2. **Adapter + agent JSON surface** (W1.4 + W1.5 + W1.6, plus Workstream 2): content-format refactor,
   full Linear CRUD adapter, two-backend contract tests, and the `--json` I/O verbs. (W2.1/W2.2 can
   split out earlier against Plane if you want agent JSON before the adapter lands.)
3. **Slash-command cutover** (Workstream 3): scaffolds separately in the claude-config repo against
   the stabilized JSON envelope.
