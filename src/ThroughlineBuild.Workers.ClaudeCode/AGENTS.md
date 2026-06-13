# ThroughlineBuild.Workers.ClaudeCode - vendor worker (template)

`ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`). The default print
transport spawns `claude --print --verbose --output-format stream-json` with
the brief on stdin. The opt-in interactive-hook transport runs under a terminal
host, trusts a correlated Stop hook, and recovers telemetry through the isolated
`ClaudePersistedTranscriptReader` adapter.

Process/terminal hosting is behind a focused abstraction
(`InteractiveClaudeProcessHost.cs`): `InteractiveClaudeProcessLauncherFactory`
picks the platform host, so the transport, run store, and parsing never touch
platform code. Windows = `WindowsConPtyClaudeProcess` (ConPTY) which puts the
whole child tree in a kill-on-close **job object** - the guarantee that no
descendant (incl. tool subprocesses) survives - and terminates via
`ProcessShutdownSequence`: close-the-console (graceful) -> bounded wait ->
TerminateJobObject (forced). Unix is a documented unsupported arm
(`UnixInteractiveClaudeProcessLauncher`) and the single drop-in extension point;
there is no Stage-01-equivalent Unix evidence yet, and the transport never
silently falls back to `--print`. Each run holds a `ClaudeRunLease`
(exclusive `run.lock` + `owner.json`); `ClaudeRunDirectorySweeper` reclaims
crash-orphaned run dirs by lock-freeness (never by killing a pid). A per-worktree
temp lock (`InteractiveClaudeWorktreeLock`, hashed by full path) prevents two
runs racing on the shared `.build/brief.md`. The kernel-validated Windows
tree-cleanup test (`WindowsProcessTreeCleanupTests`) runs on the dev host.

WORKER_RESULT + fenced blocks are parsed from the FULL assistant transcript
reconstructed from the stream (`TryExtractAssistantTranscript`), not from the
terminal envelope's `result` field alone - models that split output across
messages (Fable, especially) would otherwise lose earlier blocks. `result`
stays as legacy fallback and the source of usage/cost.
`ClaudeCodeModelValidator` rejects bad `[workers.claude-code.sizes]` values at
config load: only tier aliases haiku/sonnet/opus or a full claude-* slug -
`model = "fable"` must be `"claude-fable-5"`. `WorkerTranscriptWriter` emits a
stable JSONL transcript under --debug (pure observation, post-exit only).

Reference pattern for the other vendors. To add/change one: implement
`IWorkerAgent` (parse via `Workers.Common`), then add the name -> class arm in
`ThroughlineBuild.Cli/WorkerAgentBuilder.cs` (NOT Program.cs). Invocation
shapes differ: ClaudeCode + Codex (`codex exec --json -`) take the brief on
stdin; Copilot = `-p "<brief>"` + per-tool `--allow-tool`; Gemini = JSON DTOs.
Copilot has no digester. Keep new parsing AOT-safe.
