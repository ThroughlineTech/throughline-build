# src/ - library + CLI orientation

20 projects (19 libraries + Cli) compile into the Native AOT `build` binary.
Architecture overview:
[docs/throughline-build-architecture.md](../docs/throughline-build-architecture.md).

Dependency order (leaf -> root):
`Contracts`, `ModelClient` (leaves) -> `Git`, `Helpers`, `EventLog`, `Plane`,
`JudgmentSlots`, `Scaffold`, `Workers.Common` -> `Briefs`, `Anthropic`,
`Workers.{ClaudeCode,Codex,Gemini,Copilot}` -> `Verification` -> `Phases` ->
`Commands` -> `Cli`.

The sln (`throughline-build.sln`) is the source of truth for what is a project:
directories not in it (e.g. untracked `ThroughlineBuild.Linear/`) are local
debris, not projects.

AOT discipline: `Cli` sets `PublishAot=true`. Use source-generated
`JsonSerializerContext` for anything serialized; do not rely on reflection-based
serialization. Keep `Contracts` I/O-free.

Trust the code over the docs. The state-of-the-system set is a historical
snapshot; the architecture document describes the current tree.
