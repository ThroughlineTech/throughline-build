# ThroughlineBuild.Plane - the ticketing backend

`PlaneTicketingClient` is the sole `ITicketing` implementation and also owns
provisioning, connectivity, and project discovery. No GitHub or Linear adapter
exists. `ProjectResolver` resolves/creates projects from raw credentials
pre-config.

Key behaviors:

- Per-run issue snapshot cache paginates the project once into `_seqToUuid` and
  `_issueByUuid`; reads answer from memory. Mutations must keep write-through
  `AddOrUpdate` or same-run lookups go stale (TLB-366).
- `RequestThrottle` caps requests/min per process; Polly retries 429/5xx with
  Retry-After.
- All HTTP uses `SendWithTransportRetryAsync` (TLB-545): fresh request per
  attempt, throttle re-acquired, exhausted transport failures surfaced as
  `TicketingUnavailableException` so orchestration treats them as environmental.
- State/label maps are lazy; the canonical set is `Contracts.WorkspaceSchema`,
  shared with `build setup`.
- Typed relations use Plane's `issue-relation` endpoint. Chain reads use a
  per-issue cache; CLI list/create/remove surfaces endpoint/config errors and
  invalidates source/target cache entries.
- JSON uses source-generated AOT context, never reflection serialization.

Details:
[../../docs/state-of-the-system/03-external-dependencies.md](../../docs/state-of-the-system/03-external-dependencies.md),
[../../docs/state-of-the-system/05-state-and-persistence.md](../../docs/state-of-the-system/05-state-and-persistence.md).
