# Linear Integration Feasibility Report

**Date:** 2026-06-04
**Scope:** Adding Linear as a ticket backend for the `build` CLI (Throughline Build)

## TL;DR / Verdict

**Feasible and architecturally clean, but not a drop-in.** The codebase already has the
right abstraction (`ITicketing`), so no refactoring of the command/phase layer is needed -
you write one new class. The real work is in four *impedance mismatches* between Plane and
Linear, not in the wiring:

1. **Plane is REST + HTML; Linear is GraphQL + Markdown.** Every description/comment in the
   codebase is HTML; Linear wants Markdown. This is the single biggest porting cost.
2. **Hardcoded workflow-state names** (`Backlog`, `Planning`, `Ready`, ...) need to become
   config-driven, because Linear teams name states freely.
3. **GraphQL transport** means you can't reuse the Plane JSON/Polly machinery as-is -
   different query shape, cursor pagination, complexity-based rate limits, and errors that
   arrive inside HTTP 200 bodies.
4. **Identity** - an API key acts *as a user*; Linear's `actor=app` OAuth gives the bot its
   own identity (optional, nice-to-have).

**Rough effort:** ~3-5 focused days for a working `LinearTicketingClient`, plus a day for
the HTML->Markdown layer and state-map config. The architecture does *not* fight you.

---

## 1. What we'd plug into (current architecture)

The good news, confirmed by reading the source:

- **One interface, cleanly isolated.** `ITicketing`
  (`src/ThroughlineBuild.Contracts/ITicketing.cs`) - 14 async methods - is the *only*
  surface the commands/phases touch. All 17 CLI verbs depend on the interface, never on
  Plane directly.
- **Plane is fully contained** in `ThroughlineBuild.Plane`
  (`src/ThroughlineBuild.Plane/PlaneTicketingClient.cs`). Nothing Plane-specific leaks
  outside that project.
- **Neutral domain model already exists** - `Ticket`, `TicketState`, `Size`, `Relation`,
  `TicketComment` in `ThroughlineBuild.Contracts.Models`. These are *not* Plane-shaped;
  translation happens at the boundary (`ToTicketAsync`). Linear maps onto these same types.
- **The only gap:** `Program.cs` always does `new PlaneTicketingClient(...)` directly.
  There's a `[ticketing].backend` config key that's read but never branched on. So you need
  a tiny backend factory - that's the entire wiring change.

**Verdict on coupling: excellent.** Adding Linear = implement `LinearTicketingClient :
ITicketing` + a factory + config. No phase/command code changes.

---

## 2. Linear API fundamentals

| Aspect | Linear | vs. Plane today |
|---|---|---|
| Protocol | **GraphQL only**, single endpoint `https://api.linear.app/graphql` | Plane is REST with many endpoints |
| Auth | API key in `Authorization: <key>` header (no `Bearer`); or OAuth `Bearer <token>` | Plane uses `X-API-Key` header - similar simplicity |
| Identifiers | Team key + number, e.g. `ENG-123`; **accepts UUID *or* shorthand interchangeably** in queries/mutations | Plane needs UUID for writes, seq for humans - Linear is friendlier here |
| Project mapping | Throughline Build "project" = a Linear **Team** (has the `key` like `TLB`) | Plane project_id/identifier -> Linear team id/key |
| Content format | **Markdown** for `description` and comment `body` | Plane uses `description_html` - **mismatch** |
| Rate limit | **5,000 req/hr** per user (~83/min) + complexity budget (3M points/hr, **10k max per single query**); leaky-bucket | Plane 60/min, throttled to 40/min - Linear is more generous on count, but adds query-complexity ceiling |
| Errors | Rate-limit = HTTP **400** w/ `RATELIMITED` code; GraphQL errors arrive inside **HTTP 200** with an `errors[]` array | Plane is status-code driven (429/5xx) - **different retry logic** |

---

## 3. Operation-by-operation mapping (the `ITicketing` contract -> Linear)

Every Plane operation has a Linear equivalent. Nothing is missing:

| `ITicketing` operation | Plane (REST today) | Linear (GraphQL) |
|---|---|---|
| Fetch snapshot / list | `GET /issues?per_page=100` | `team(id){ issues(first:100, after:$cursor){ nodes{...} pageInfo{ hasNextPage endCursor }}}` (cursor loop) |
| Get single | from snapshot | `issue(id:"TLB-123"){...}` (shorthand works) |
| Create ticket | `POST /issues/` | `issueCreate(input:{ teamId, title, description, parentId, labelIds }){ issue{ id identifier }}` |
| Set parent (sub-issue) | `PATCH parent` | `issueUpdate(id, input:{ parentId })` - native sub-issues |
| Transition state | `PATCH state` | `issueUpdate(id, input:{ stateId })` - need state UUID |
| Append/replace description | `PATCH description_html` | `issueUpdate(id, input:{ description })` - **Markdown** |
| Apply labels (size) | `PATCH label_ids` | `issueUpdate(id, input:{ labelIds })` - whole array, no add-one mutation |
| Create comment | `POST /comments/` | `commentCreate(input:{ issueId, body })` - **Markdown** |
| Get comments (marker parse) | `GET /comments/` | `issue(id){ comments{ nodes{ id body createdAt }}}` |
| Get/add relation | `GET/POST /relations/` | `issueRelationCreate(input:{ issueId, relatedIssueId, type: blocks })`; read via `issue{ relations{ nodes{ type relatedIssue{ identifier }}}}` |
| Lazy-load states/labels/types | `GET /states/`, `/labels/`, `/issue-types/` | `team(id){ states{nodes{id name type}} labels{nodes{id name}}}` |
| Create child tickets (bulk) | N x `POST /issues/` | N x `issueCreate` with `parentId` |

Relation types line up: Linear's `type` enum includes `blocks`, `related`, `duplicate` -
the code's `blocked_by`/`blocks` maps cleanly (`blocked_by` = the inverse side of a `blocks`
relation).

---

## 4. The four real impedance mismatches (where the work is)

### A. HTML vs Markdown - the big one
The codebase is HTML-native: it *appends HTML blocks* to descriptions, posts comments with
`<strong>wontfix:</strong>` and embedded markers like `[planned_at: <sha>]`, and parses
comment bodies to find the freshest marker. Linear stores/returns **Markdown**.

Options:
- **Translate at the boundary** (recommended): in `LinearTicketingClient`, convert the HTML
  the phases produce into Markdown on write, and treat Linear's Markdown on read. The
  bracket markers (`[planned_at: ...]`) are plain text and survive round-trips either way -
  low risk. The risk is in richer HTML (lists, `<strong>`, links).
- **Better long-term:** lift marker-embedding/parsing out of HTML entirely (markers are just
  text tokens), so it's format-agnostic. Larger refactor; worth a follow-up ticket, not v1.

Note: Linear auto-converts pasted Markdown to rich text, and the `description` field
round-trips as Markdown - so a modest HTML->MD shim covers the common cases.

### B. Hardcoded state names
`PlaneTicketingClient.cs:274-284` hardcodes 7 names: `Backlog, Planning, Ready, In Progress,
In Review, Done, Cancelled`. Linear's defaults are `Backlog, Todo, In Progress, Done,
Canceled` (note: one "l"). Linear lets you create arbitrary states, each tagged with a
**category** (`backlog / unstarted / started / completed / cancelled / triage`).

**Recommendation:** make the logical-state -> state-name (or state-UUID) map
**config-driven** for the Linear backend, e.g. in `.build/config.toml`:

```toml
[ticketing.linear.states]
Planning = "Planning"
Ready    = "Ready"
InReview = "In Review"
# ...
```

Then the workspace owner either names their Linear states to match, or remaps here. This
also future-proofs the Plane side.

### C. GraphQL transport (not REST)
- **No endpoint-per-operation** - one POST with a query string + variables JSON. You
  hand-build query strings (see AOT note below).
- **Cursor pagination** replaces `per_page`. The per-run snapshot pattern still works - just
  loop on `pageInfo.endCursor`.
- **Complexity budget:** a 100-issue page with ~10 fields each is well under the 10k/query
  ceiling, but keep page size sane and don't over-fetch nested connections.
- **Error handling differs:** Plane's Polly pipeline retries on 429/5xx. Linear puts GraphQL
  errors in **HTTP 200** bodies (`errors[]`) and rate-limits with **HTTP 400 +
  `RATELIMITED`**. The retry/throttle layer needs Linear-specific predicates. The existing
  `RequestThrottle` concept carries over (just retune to req/hr + complexity headers like
  `X-RateLimit-Complexity-Remaining`).

### D. Identity
An API key acts **as the user who owns it**, so all ticket churn shows up as that user.
Cleanest options, simplest first:
1. **Dedicated service-account user + its API key** (recommended for a CLI) - mirrors
   today's `X-API-Key` model exactly, bot activity is attributable, zero OAuth code.
2. **OAuth `actor=app`** - gives the agent its own first-class identity/avatar in the
   workspace. More setup (OAuth flow, token storage); overkill for a deterministic CLI but
   worth noting if you want "build bot" to appear as itself. The full Agents/AgentSession
   webhook machinery is for assignment-driven interactive agents - **not** relevant here.

**Plus a minor fifth: Size.** Today `Size` is parsed from a `size:s|l` label. You can keep
that convention on Linear verbatim (labels work the same), or switch to Linear's native
**estimate** field (numeric, or "T-shirt" scale if the team enables it). Keeping the label
convention is the lower-friction v1.

---

## 5. Native-AOT considerations

This is AOT, so the JSON approach matters:

- **Do NOT pull in a GraphQL client library** (`GraphQL.Client`, Strawberry Shake, etc.) -
  they lean on reflection-based serialization and code generators that fight AOT and bloat
  the binary.
- **Mirror the existing Plane pattern exactly:** hand-write GraphQL query strings as
  constants, POST `{ query, variables }`, and (de)serialize with a `System.Text.Json`
  source-gen context - a `LinearJsonContext : JsonSerializerContext` with `[JsonSerializable]`
  DTOs, identical in spirit to `PlaneJsonContext` (`PlaneApiModels.cs:177`). Reuse
  `UnsafeRelaxedJsonEscaping`.
- GraphQL responses are uniformly `{ "data": {...}, "errors": [...] }`, so you write a tiny
  generic `GraphQLResponse<T>` envelope and your DTOs slot under `data`. Cleaner than Plane's
  many response shapes.

No new AOT risk - if anything GraphQL's uniform envelope is *easier* to source-gen than
Plane's varied REST payloads.

---

## 6. Recommended implementation plan

1. **`ThroughlineBuild.Linear` project** (peer to `ThroughlineBuild.Plane`):
   `LinearTicketingClient : ITicketing`, `LinearClientOptions`, `LinearJsonContext`,
   `LinearApiException`, reuse/retune `RequestThrottle`.
2. **Backend factory** in `Program.cs`: branch on `[ticketing].backend` (`"plane"` |
   `"linear"`) - replaces the direct `new PlaneTicketingClient(...)` sites. Small, mechanical.
3. **Config additions:** `linear_api_token_env`/`linear_api_token`, `linear_team_id` (or
   key), `linear_base_url`, and the `[ticketing.linear.states]` map. Mirror the
   secret-resolution path in `Config.cs`.
4. **HTML->Markdown shim** at the write boundary (and treat reads as Markdown). Start
   minimal: pass-through for marker text, convert `<strong>`/lists/links.
5. **State + label + relation mapping** via the lazy-load-once cache pattern Plane already
   uses.
6. **Retry/throttle** tuned to Linear (HTTP 400 `RATELIMITED`, complexity headers, GraphQL
   `errors[]` in 200s).
7. **Golden/contract tests:** the repo already has golden-snapshot infrastructure - add
   Linear backend fixtures so both backends are exercised identically.

---

## 7. Risks & open questions

**Risks**
- *HTML/Markdown fidelity* - medium. Mitigate by keeping markers as plain text and starting
  with a narrow converter.
- *State-name drift* - low once config-driven; today's hardcoding would silently no-op
  transitions if names don't match (the code warns to stderr but continues).
- *Marker parsing across formats* - low; markers are bracketed plain text, but verify
  round-trip through Linear's Markdown normalization.
- *Complexity limits on large snapshots* - low; just paginate sensibly.

**Questions to resolve**
1. **One backend at a time, or both live?** (Affects whether the factory is a hard switch or
   per-invocation.)
2. **Bot identity:** service-account API key (simple) or OAuth `actor=app` (bot has its own
   identity)?
3. **Size:** keep the `size:` label convention, or adopt Linear's native estimate field?
4. **Migration vs. greenfield:** new Linear team starting fresh, or do existing Plane tickets
   (TLB-NNN) need to move over? (Migration is a separate, larger effort.)
5. Should the state-name map become config-driven for **both** backends (cleaner) or
   Linear-only (smaller diff)?

---

**Bottom line:** The `ITicketing` abstraction means this is "write one adapter," not
"re-architect." Budget your time for the GraphQL/Markdown boundary and the state-mapping
config, not for plumbing. Estimate: a usable Linear backend in under a week.

## Sources

- Linear GraphQL getting started: https://linear.app/developers/graphql
- Rate limiting: https://linear.app/developers/rate-limiting
- OAuth actor authorization: https://linear.app/developers/oauth-actor-authorization
- Agents: https://linear.app/developers/agents
- Issue relations: https://linear.app/docs/issue-relations
- Configuring workflows: https://linear.app/docs/configuring-workflows
- STJ source generation (AOT): https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
