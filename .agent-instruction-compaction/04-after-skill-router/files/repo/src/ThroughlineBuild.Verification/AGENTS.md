# ThroughlineBuild.Verification - gate checks + review verification

Stack-agnostic by construction: run configured `CheckSpec` commands and inspect
exit codes/output as data. Never teach this project a language or tool; stack
knowledge belongs in config/derived data.

- `AutomatedChecksRunner` / `PreComputedChecksRunner`: run configured checks.
  Gating roles hard-fail; lint/format/smoke are advisory.
- `GateVacuityProver`: proves a green gate can fail by materializing the
  declared canary and requiring the re-run to fail. Vacuity hard-fails the chain
  without rework; proven-once state is per chain run.
- `GateControlProber` (TLB-538): after gate hard-fail, re-runs failed checks
  against untouched base in a throwaway worktree. Base failure is environmental:
  stop chain, skip siblings, burn no rework rounds. Green path costs nothing.
- `ObsoleteRatifier`: verifies obsolete-escalation claims by cited commit, cited
  files at HEAD, then model check against acceptance criteria.
- `WorkerAgentReviewer` adapts a worker as review `IVerifier`; Cli's
  `VerifierToolEnforcement` warns when allowed-tools cannot be enforced.

`Templates/ratify-obsolete-prompt.md` is EmbeddedResource and LF-pinned; edit as
LF and rebuild after changes.
