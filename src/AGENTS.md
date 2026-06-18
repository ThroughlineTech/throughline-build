# src/ - library + CLI orientation

19 projects (18 libraries + Cli) compiled AOT into the `build` binary. Full
per-project index: [docs/state-of-the-system/01-inventory.md](../docs/state-of-the-system/01-inventory.md).
Architecture overview: [docs/state-of-the-system/00-index.md](../docs/state-of-the-system/00-index.md).

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

Trust the code over the docs: state-of-the-system was written at an older commit
and flags its own drift. Where a doc disagrees with HEAD, the code wins.
