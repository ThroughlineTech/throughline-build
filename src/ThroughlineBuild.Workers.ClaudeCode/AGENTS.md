# ThroughlineBuild.Workers.ClaudeCode - vendor worker (template)

`ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`). The DEFAULT transport is
`print` (`claude --print --verbose --output-format stream-json`, brief on stdin)
pending the Stage 07 cutover; interactive-hook is the validated cutover target and is
opt-in (`transport = "interactive-hook"`, honored on any Claude-family agent block, not
just `claude-code`) until the default flips after operator dogfood. The interactive
transport runs an interactive Claude session under a terminal host (argv never contains
`--print`) and recovers telemetry through the isolated `ClaudePersistedTranscriptReader`
adapter. `ClaudeCodePreflight` gates the interactive path before any side effect - in
`build setup`, before the worker-spawning phase verbs, AND at the transport entry itself
(`ExecuteAsync`, so every path is covered: draft, investigate-plan, scaffold, batch) -
checking claude is runnable, version >= 2.1.177, platform supported, and never silently
falls back to print. Completion is SYNTHESIZED from
`claude`'s persisted transcript (tail for an assistant message at
`stop_reason == end_turn`, synthesize the completion record, best-effort `/exit`
nudge, terminate the tree, parse `WORKER_RESULT` + telemetry) - the correlated
Stop-hook `completion.json` is only a best-effort fast-path, because
`claude` 2.1.170+ does not fire the per-turn Stop hook in interactive mode.
Workspace trust is pre-seeded in `~/.claude.json` and all claude-facing paths
are canonicalized via `ClaudeRealPath.Resolve` (so spawn cwd, trust key, and
transcript `cwd` match). Validated live on Windows + macOS arm64 + Linux x86_64/glibc
(claude 2.1.177).

Process/terminal hosting is behind a focused abstraction
(`InteractiveClaudeProcessHost.cs`): `InteractiveClaudeProcessLauncherFactory`
picks the platform host, so the transport, run store, and parsing never touch
platform code. Both hosts terminate via `ProcessShutdownSequence`: a graceful
signal -> bounded wait -> forced kill escalation. Containment differs by platform
and is NOT equivalent:
- Windows `WindowsConPtyClaudeProcess` (ConPTY): the whole child tree goes in a
  mandatory kill-on-close **job object** - a kernel guarantee no descendant
  survives. The job is required; a job create/assign failure fails the launch
  (terminating the still-suspended child), never a silent best-effort fallback.
- Unix `UnixPtyClaudeProcess` (`posix_openpt` + `posix_spawnp`,
  `POSIX_SPAWN_SETSID`): **process-group** containment via `kill(-pid, SIGTERM)`
  then `SIGKILL`, then a bounded `kill(-pid, 0)` drain check. Weaker than the job
  object - a descendant that double-forks / `setsid`s out of the group escapes.
  Requires glibc >= 2.26 / macOS >= 10.15 (CI targets). The transport never
  silently falls back to `--print`.

Each run holds a `ClaudeRunLease` (exclusive `run.lock` + `owner.json`);
`ClaudeRunDirectorySweeper` reclaims crash-orphaned run dirs by lock-freeness
(never by killing a pid). A per-worktree temp lock
(`InteractiveClaudeWorktreeLock`, hashed by full path) prevents two runs racing
on the shared `.build/brief.md`. Real tree-cleanup tests exist per platform:
`WindowsProcessTreeCleanupTests` passes on the Windows dev host;
`UnixProcessTreeCleanupTests` (forced-termination + early-root-exit) passes on
macOS arm64 and Ubuntu 24.04 x86_64 / glibc 2.39 (both validated 2026-06-13).

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
