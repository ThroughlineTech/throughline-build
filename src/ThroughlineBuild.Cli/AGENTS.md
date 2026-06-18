# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` is ~2300 lines: verb dispatch is a chain of `if (verb == ...)`
blocks, not a registry. 21 action verbs (init, settarget, setup, user-guide,
op-doc new|spec, models refresh, sweep, scaffold, new, list, amend, close,
defer, reopen, plan, implement, review, ship, chain, rework, decompose) plus
tiered help. Unknown token -> exit 2.

Three arg pre-passes run before dispatch: bare bool flags stripped; `--agent` /
`--agent-<phase>` pairs extracted (`CliArgParser`); ticket IDs extracted for
phase verbs.

Help is the `Help/` subsystem: `Tier0Renderer` (verb list), `Tier1Renderer`
(`build <verb> --help`), topics in `Help/Topics/`, all fed by
`HelpRegistryFactory`. `CliUsage.cs` is legacy (tests only). `models` and
`sweep` are not in the help registry. To add a verb: dispatch block in
`Program.cs` + entry in `Help/HelpRegistryFactory.cs`.

`init`, `settarget`, `user-guide`, `op-doc`, and `models refresh` run BEFORE
config load. Worker wiring: config agent names -> `WorkerAgentBuilder.Create`
(name -> vendor class lives THERE, not in Program.cs), resolved via
`WorkerAgentFactory`. `ChainPhaseComposition` is the test-coverable seam that
news up `ChainPhase`; `ChainExitCodeMapper` maps outcomes to exit codes.
Verb-by-verb detail: [../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
