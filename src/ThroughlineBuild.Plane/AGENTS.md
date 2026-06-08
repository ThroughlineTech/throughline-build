# ThroughlineBuild.Plane - the ticketing backend

`PlaneTicketingClient` is the SOLE `ITicketing` implementation, and also
implements `ITicketingProvisioner` (create states/labels), `IProjectDiscovery`
(list/find/create projects), and `ITicketingConnectivity` (op-34, driving
`build setup` and connected `build init`). `ProjectResolver` (find-or-create a
project by name) lives here too. GET/PATCH/POST against the Plane REST API. No
GitHub or Linear adapter exists.

Key behaviors to know before touching it:
- Per-run issue snapshot cache: the whole project is paginated once into
  `_seqToUuid` + `_issueByUuid`; `FindIssueAsync`/`QueryAsync` answer from
  memory, with write-through `AddOrUpdate` on every PATCH (TLB-366). If you add a
  mutation, keep the cache write-through or lookups go stale within a run.
- `RequestThrottle` caps at 40 requests/min per process; Polly handles retry.
- State-name and label-name maps are lazily cached.
- AOT: JSON via source-generated context - no reflection serialization.

External-dependency and state detail:
[../../docs/state-of-the-system/03-external-dependencies.md](../../docs/state-of-the-system/03-external-dependencies.md),
[../../docs/state-of-the-system/05-state-and-persistence.md](../../docs/state-of-the-system/05-state-and-persistence.md).
