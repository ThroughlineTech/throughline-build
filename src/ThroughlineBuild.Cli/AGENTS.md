# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` is about 2,760 lines: verb dispatch is a chain of `if (verb == ...)`
blocks, not a registry. There are 26 action verbs. The newer ticket-facing
surface is `get`, `comments`, `comment`, `transition`, and `relate`; supported
ticket verbs also accept `--json`. Unknown token -> exit 2.

Three arg pre-passes run before dispatch: bare bool flags stripped; `--agent` /
`--agent-<phase>` pairs extracted (`CliArgParser`); ticket IDs extracted for
phase verbs.

Help is the `Help/` subsystem: `Tier0Renderer` (verb list), `Tier1Renderer`
(`build <verb> --help`), topics in `Help/Topics/`, all fed by
`HelpRegistryFactory`. `CliUsage.cs` is legacy (tests only). `models` and
`sweep` are not in the 24-entry help registry. To add a verb: dispatch block in
`Program.cs` + entry in `Help/HelpRegistryFactory.cs`.

`init`, `settarget`, `user-guide`, `op-doc`, and `models refresh` run BEFORE
config load. Worker wiring: config agent names -> `WorkerAgentBuilder.Create`
(name -> vendor class lives THERE, not in Program.cs), resolved via
`WorkerAgentFactory`. `ChainPhaseComposition` is the test-coverable seam that
news up `ChainPhase`; `ChainExitCodeMapper` maps outcomes to exit codes.
Verb-by-verb detail: [../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
