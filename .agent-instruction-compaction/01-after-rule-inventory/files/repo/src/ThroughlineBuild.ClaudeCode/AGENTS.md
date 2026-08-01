# ThroughlineBuild.ClaudeCode - reusable public facade

This project is the supported public API over
`ThroughlineBuild.Workers.ClaudeCode`. Keep the surface small and
consumer-oriented: `ClaudeCodeClient`, immutable option records, transport
mode, and the optional `WORKER_RESULT` instruction contract.

Do not duplicate transport or transcript logic here; delegate to
`ClaudeCodeAgent`. Preserve AOT-safe serialization and the string overload's
idempotent contract append. The advanced `Brief` overload must remain usable
without modifying caller instructions.

The project has NuGet metadata, but repository CI does not currently pack or
publish it. Tests live in `tests/ThroughlineBuild.ClaudeCode.Tests`.
