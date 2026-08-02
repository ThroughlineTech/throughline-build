# ThroughlineBuild.Cli - the `build` entry point

`Program.cs` stays tiny. `CliApplication` owns argument pre-passes, bootstrap
coordination, registry dispatch, and top-level error handling. `CliBootstrap`
creates immutable `CliContext`, including the shared `HttpClient` and
`PlaneTicketingClient`. `CliVerbRegistryFactory` registers action verbs and
marks pre-config verbs. New ticket-facing verbs are `get`, `comments`, `comment`,
`transition`, and `relate`; supported ticket verbs accept `--json`. Unknown
tokens exit 2.

`CliArgParser` owns pre-passes: bare bool flags, `--agent` /
`--agent-<phase>`, chain traversal and batch flags, and ticket IDs for phase
verbs.

Help lives under `Help/`: `Tier0Renderer`, `Tier1Renderer` (`build <verb>
--help`), topics, and `HelpRegistryFactory`. `CliUsage.cs` is legacy tests-only.
`models` and `sweep` are not in the 24-entry help registry. To add a verb, add
registry, dispatch, and, when tiered help should expose it, `HelpRegistryFactory`
help metadata.

`init`, `settarget`, `user-guide`, `op-doc`, `models refresh`, and `sop doctor`
have `RunsBeforeConfig = true`. Worker wiring belongs in
`WorkerAgentBuilder.Create`/`WorkerAgentFactory`, not `Program.cs`.
`ChainPhaseComposition` news up `ChainPhase`; `ChainExitCodeMapper` maps
outcomes to exits. Verb detail:
[../../docs/state-of-the-system/01-inventory.md](../../docs/state-of-the-system/01-inventory.md).
