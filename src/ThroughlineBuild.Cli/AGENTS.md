# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` is the three-line process entry point. `CliApplication` owns argument
pre-passes, bootstrap coordination, registry dispatch, and top-level error handling.
`CliVerbRegistryFactory` registers all 26 action verbs and marks the five verbs that
must run before config load. `CliBootstrap` creates the immutable `CliContext`, including
the one shared `HttpClient` and `PlaneTicketingClient` used by configured verbs. The newer
ticket-facing surface is `get`, `comments`, `comment`, `transition`, and `relate`;
supported ticket verbs also accept `--json`. Unknown token -> exit 2.

Argument pre-passes live in `CliArgParser`: bare bool flags are stripped;
`--agent` / `--agent-<phase>` pairs are extracted; chain traversal and batch flags
are extracted; ticket IDs are extracted for phase verbs.

Help is the `Help/` subsystem: `Tier0Renderer` (verb list), `Tier1Renderer`
(`build <verb> --help`), topics in `Help/Topics/`, all fed by
`HelpRegistryFactory`. `CliUsage.cs` is legacy (tests only). `models` and
`sweep` are not in the 24-entry help registry. To add a verb: dispatch block in
`Program.cs` + entry in `Help/HelpRegistryFactory.cs`.

`init`, `settarget`, `user-guide`, `op-doc`, and `models refresh` have
`RunsBeforeConfig = true` registry entries. Worker wiring: config agent names -> `WorkerAgentBuilder.Create`
(name -> vendor class lives THERE, not in Program.cs), resolved via
`WorkerAgentFactory`. `ChainPhaseComposition` is the test-coverable seam that
news up `ChainPhase`; `ChainExitCodeMapper` maps outcomes to exit codes.
Verb-by-verb detail: [../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
