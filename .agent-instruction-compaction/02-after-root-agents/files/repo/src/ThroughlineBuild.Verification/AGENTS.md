# ThroughlineBuild.Verification - gate checks + review verification

Stack-agnostic by construction: everything here runs configured `CheckSpec`
commands and inspects exit codes/output as data. Never teach this project a
language or tool - stack knowledge belongs in config/derived data.

- `AutomatedChecksRunner` / `PreComputedChecksRunner`: run the configured
  checks (Gating roles hard-fail; lint/format/smoke are advisory).
- `GateVacuityProver`: proves a GREEN gating check CAN fail by materializing
  the check's declared canary file and asserting the re-run fails. A vacuous
  gate hard-fails the chain WITHOUT rework (it is gate integrity, not a code
  defect). Proven-once set is per chain run.
- `GateControlProber` (TLB-538): after a gate hard-fail, re-runs the failed
  checks against the untouched base ref in a throwaway worktree; if the base
  also fails, the failure is ENVIRONMENTAL - chain stops, siblings skipped,
  no rework rounds burned. Costs nothing on the green path.
- `ObsoleteRatifier` (`IObsoleteRatifier`): verifies an obsolete-escalation
  claim - cited commit exists, cited files exist at HEAD, then a model-driven
  check that the prior work satisfies the brief's acceptance criteria.
- `WorkerAgentReviewer` adapts a worker agent as the review `IVerifier`;
  Cli's `VerifierToolEnforcement` warns when the configured allowed-tools
  list cannot be enforced for the chosen review agent.

`Templates/ratify-obsolete-prompt.md` is EmbeddedResource and LF-pinned via
`.gitattributes` - rebuild after edits, edit as LF.
