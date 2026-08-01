# ThroughlineBuild.ClaudeCode - reusable public facade

This is the supported public API over `ThroughlineBuild.Workers.ClaudeCode`.
Keep the surface small and consumer-oriented: `ClaudeCodeClient`, immutable
options, transport mode, and the optional `WORKER_RESULT` instruction contract.

Do not duplicate transport or transcript logic; delegate to `ClaudeCodeAgent`.
Preserve AOT-safe serialization, the string overload's idempotent contract
append, and the advanced `Brief` overload's ability to leave caller
instructions untouched.

NuGet metadata exists, but CI does not pack or publish. Tests live in
`tests/ThroughlineBuild.ClaudeCode.Tests`.
