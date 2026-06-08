# ThroughlineBuild.Workers.ClaudeCode - vendor worker (template)

`ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`). Spawns
`claude --print --output-format stream-json` with the brief on stdin, parses
the NDJSON stream, and emits a one-line progress digest
(`WorkerProgressDigest` / `ClaudeCodeProgressDigester`).

This is the reference pattern for the other three vendors
(`Workers.Codex`, `Workers.Gemini`, `Workers.Copilot`). To add or change a vendor:
1. Implement `IWorkerAgent` (unique `Name`, spawn the CLI, parse its output to a
   `WorkerResult` via `Workers.Common`, optional `IWorkerProgressDigester`).
2. Add a `case` to `WorkerAgentBuilder.Create` (in `ThroughlineBuild.Cli`), the
   central name->IWorkerAgent construction seam wired into the `Program.cs`
   factory loop and resolved by `WorkerAgentFactory`.

CLI invocation shape differs per vendor: ClaudeCode = brief on stdin;
Codex = `codex exec --json` with the brief on stdin, plus `-c
model_reasoning_effort=<effort>` when the size tier defines an effort;
Copilot = `-p "<brief>"` with per-tool `--allow-tool` flags; Gemini = JSON DTO
parsing. Copilot has no digester. Size->model(+effort) mapping comes from config
(`[workers.<name>.sizes]`, now `{model, effort}` tables since op-33).

AOT regression coverage is concentrated here - keep new parsing AOT-safe.
