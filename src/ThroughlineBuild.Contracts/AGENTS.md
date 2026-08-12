# ThroughlineBuild.Contracts - interfaces, records, enums

Pure abstractions only: no I/O, process spawning, HTTP, or file access. Nearly
every project references this one, so keep it leaf.

Key interfaces include worker, ticketing/attachments/provisioning/connectivity/discovery,
LLM, git, event sink, workflow phase, progress digester, review feedback, and
verifier/obsolete-ratifier contracts.

Models live in `Models/`; `TicketAttachment`/`TicketAttachmentContent` keep
attachment metadata and bytes backend-neutral. `TicketingUnavailableException` marks environmental
failure so orchestration skips siblings instead of reworking. The one
data-carrying exception to "interfaces only" is `WorkspaceSchema`, the canonical
state/label set shared by Plane and `build setup`.

Contract details:
[../../docs/state-of-the-system/07-contracts.md](../../docs/state-of-the-system/07-contracts.md).
