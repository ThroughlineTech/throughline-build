# ThroughlineBuild.Plane - the ticketing backend

`PlaneTicketingClient` is the SOLE `ITicketing` implementation (it also
implements `ITicketingProvisioner`, `ITicketingConnectivity`, and
`IProjectDiscovery`). No GitHub or Linear adapter exists. `ProjectResolver`
resolves/creates a project from raw credentials, pre-config (used by setup).

Key behaviors to know before touching it:
- Per-run issue snapshot cache: the whole project is paginated once into
  `_seqToUuid` + `_issueByUuid`; `FindIssueAsync`/`QueryAsync` answer from
  memory, with write-through `AddOrUpdate` on every PATCH (TLB-366). If you add
  a mutation, keep the cache write-through or lookups go stale within a run.
- `RequestThrottle` caps requests/min per process (default 40, configurable);
  Polly retries HTTP 429/5xx honoring Retry-After.
- All HTTP goes through `SendWithTransportRetryAsync` (TLB-545): fresh request
  per attempt, throttle re-acquired; exhausted transport failures surface as
  `TicketingUnavailableException` so orchestration classifies them as
  environmental instead of crashing or reworking.
- State-name and label-name maps are lazily cached; the canonical set lives in
  `Contracts.WorkspaceSchema` (shared with `build setup`).
- Typed relations use Plane's `issue-relation` endpoint. Chain reads use a
  per-issue relation cache; explicit CLI list/create/remove operations surface
  endpoint/configuration errors and invalidate source/target cache entries.
- AOT: JSON via source-generated context - no reflection serialization.

External-dependency and state detail:
[../../docs/state-of-the-system/03-external-dependencies.md](../../docs/state-of-the-system/03-external-dependencies.md),
[../../docs/state-of-the-system/05-state-and-persistence.md](../../docs/state-of-the-system/05-state-and-persistence.md).
