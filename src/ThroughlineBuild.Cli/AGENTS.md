# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` is ~2209 lines: verb dispatch is a chain of `if (verb == ...)`
blocks, not a registry. 20 action verbs + help + `-V`/`--version` (init,
settarget, user-guide, op-doc, models, plan, implement, review, ship, chain,
rework, decompose, new, scaffold, list, setup, amend, close, defer, reopen).
Unknown token -> exit 2.

Three arg pre-passes run before dispatch:
1. bare bool flags stripped (`--debug`, `--quiet`, `--summary-json`, ...).
2. `--agent` / `--agent-plan|implement|review` pairs extracted (`CliArgParser`).
3. ticket IDs extracted for phase verbs (`CliArgParser`).

Help text now lives in the `Help/` registry (`HelpRegistryFactory` + the
`Tier0Renderer`/`Tier1Renderer` + a `help <topic>` dispatcher under
`Help/Topics/`). `CliUsage.cs` is the OLD monolithic usage blob - dead in
production (Program.cs no longer reads it), kept only for tests. To add a verb:
dispatch block in `Program.cs` + a `CommandHelp` entry in `HelpRegistryFactory`
+ any arg handling in `CliArgParser.cs`.

`init`, `settarget`, `user-guide`, `op-doc`, and `models` run BEFORE config load
(they edit or ignore the config itself). DI wiring lives in `Program.cs`; the
worker registry (name -> IWorkerAgent) is built there (~1078-1087 via
`WorkerAgentBuilder.Create`) and resolved by `WorkerAgentFactory`. Config
loading + secret resolution is `Config.cs`.

Verb-by-verb behavior, inputs, side effects, exit codes:
[../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
