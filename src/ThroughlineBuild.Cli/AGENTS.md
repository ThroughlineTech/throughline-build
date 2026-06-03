# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` is ~1654 lines: verb dispatch is a chain of `if (verb == ...)`
blocks, not a registry. Seventeen verbs (init, settarget, plan, implement,
review, ship, chain, rework, decompose, new, scaffold, list, amend, close,
defer, reopen, help). Unknown token -> exit 2.

Three arg pre-passes run before dispatch:
1. bare bool flags stripped (`--debug`, `--quiet`, `--summary-json`, ...).
2. `--agent` / `--agent-plan|implement|review` pairs extracted (`CliArgParser`).
3. ticket IDs extracted for phase verbs (`CliArgParser`).

To add a verb: dispatch block in `Program.cs` + usage text in `CliUsage.cs`
(the only help text) + any arg handling in `CliArgParser.cs`.

`init` and `settarget` run BEFORE config load (they edit the config itself).
DI wiring lives in `Program.cs`; the worker registry (name -> IWorkerAgent) is
built there (~744-777) and resolved by `WorkerAgentFactory`. Config loading +
secret resolution is `Config.cs`.

Verb-by-verb behavior, inputs, side effects, exit codes:
[../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
