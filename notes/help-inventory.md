# Help Inventory

Maps every CLI command to the files where its name is matched, its current help text lives,
and its implementation entry point. Produced by TLB-419; load-bearing for later help-system
briefs that touch the entry path.

---

## Entry and dispatch file

| Role | File |
|------|------|
| Program entry point and verb dispatcher | `src/ThroughlineBuild.Cli/Program.cs` |
| Current monolithic help string (`CliUsage.UsageText`) | `src/ThroughlineBuild.Cli/CliUsage.cs` |

`CliUsage.UsageText` is a raw string literal (`"""`...`"""`) declared as a `public const string`
on `static class CliUsage`. It is written to stdout when the user passes no arguments, `help`,
or `--help` (Program.cs line 25), and also to stderr for unknown verbs (Program.cs line 737).

---

## Global options (pre-pass, before verb dispatch)

Stripped from `args` before any verb sees them. Declared in Program.cs lines 32-70 (bool pre-pass)
and lines 72-75 (agent-flag pre-pass via `CliArgParser.ExtractAgentFlags`).

| Flag | Parsed by |
|------|-----------|
| `--debug` | Program.cs bool pre-pass |
| `--quiet` | Program.cs bool pre-pass |
| `--summary-json` | Program.cs bool pre-pass |
| `--error-location` | Program.cs bool pre-pass |
| `--no-auto-resolve` | Program.cs bool pre-pass |
| `--no-auto-merge` | Program.cs bool pre-pass |
| `--no-push` | Program.cs bool pre-pass |
| `--continue-past-failure` | Program.cs bool pre-pass |
| `--from-brief` | Program.cs bool pre-pass |
| `--skip-baseline` | Program.cs bool pre-pass |
| `--agent <name>` | `CliArgParser.ExtractAgentFlags` (CliArgParser.cs) |
| `--agent-plan <name>` | `CliArgParser.ExtractAgentFlags` (CliArgParser.cs) |
| `--agent-implement <name>` | `CliArgParser.ExtractAgentFlags` (CliArgParser.cs) |
| `--agent-review <name>` | `CliArgParser.ExtractAgentFlags` (CliArgParser.cs) |

---

## Command map

### Pipeline group

| Command | Verb matched (Program.cs line) | Implementation entry | Command file |
|---------|-------------------------------|----------------------|--------------|
| `plan` | line 1107: `if (verb == "plan")` | `PlanPhase.RunAsync` | `src/ThroughlineBuild.Phases/` |
| `implement` | line 1157: `if (verb == "implement")` | `ImplementPhase.RunAsync` | `src/ThroughlineBuild.Phases/` |
| `review` | line 1220: `if (verb == "review")` | `ReviewPhase.RunAsync` | `src/ThroughlineBuild.Phases/` |
| `ship` | line 1272: `if (verb == "ship")` | `ShipPhase.RunAsync` | `src/ThroughlineBuild.Phases/` |
| `chain` | line 1370: `if (verb == "chain")` | `RunChainVerbAsync` (local function in Program.cs) | `src/ThroughlineBuild.Commands/ChainCommand.cs` |
| `rework` | line 855 group check + line 899: `if (verb == "rework")` | `ReworkPhase.RunAsync` via `src/ThroughlineBuild.Commands/ReworkCommand.cs` | `src/ThroughlineBuild.Commands/ReworkCommand.cs` |
| `decompose` | line 855 group check + line 968: `else if (verb == "decompose")` | `DecomposePhase.RunAsync` | `src/ThroughlineBuild.Phases/` |

### WorkItems group

| Command | Verb matched (Program.cs line) | Implementation entry | Command file |
|---------|-------------------------------|----------------------|--------------|
| `new` | line 118 (early classifier) + line 372 (full dispatch) | `NewCommand` (draft/file/stdin modes) | `src/ThroughlineBuild.Commands/NewCommand.cs` |
| `list` | line 227: `if (verb == "list")` | `ListCommand.ExecuteAsync` | `src/ThroughlineBuild.Commands/ListCommand.cs` |
| `amend` | line 269 group check, registered via `TicketCommandRegistry` | `AmendCommand.ExecuteAsync` | `src/ThroughlineBuild.Commands/AmendCommand.cs` |
| `close` | line 269 group check + line 1740: `if (verb == "close")` via `WireUpConditionalCommands` | `CloseCommand.ExecuteAsync` | `src/ThroughlineBuild.Commands/CloseCommand.cs` |
| `defer` | line 269 group check + line 1752: `else if (verb == "defer")` via `WireUpConditionalCommands` | `DeferCommand.ExecuteAsync` | `src/ThroughlineBuild.Commands/DeferCommand.cs` |
| `reopen` | line 269 group check + line 1764: `else if (verb == "reopen")` via `WireUpConditionalCommands` | `ReopenCommand.ExecuteAsync` | `src/ThroughlineBuild.Commands/ReopenCommand.cs` |

### Configure group

| Command | Verb matched (Program.cs line) | Implementation entry | Command file |
|---------|-------------------------------|----------------------|--------------|
| `init` | line 138: `if (verb == "init")` (early return, before config load) | `InitCommand.Execute` | `src/ThroughlineBuild.Cli/InitCommand.cs` |
| `settarget` | line 157: `if (verb == "settarget")` (early return, before config load) | `SetTargetCommand.Execute` | `src/ThroughlineBuild.Cli/SetTargetCommand.cs` |
| `user-guide` | line 167: `if (verb == "user-guide")` (early return, before config load) | `UserGuideCommand.Execute` | `src/ThroughlineBuild.Cli/UserGuideCommand.cs` |
| `scaffold` | line 107 (early arg check) + line 653: `if (verb == "scaffold")` (full dispatch) | `ScaffoldCommand` | `src/ThroughlineBuild.Commands/ScaffoldCommand.cs` |

---

## Command-registry mechanism

For `amend`, `close`, `defer`, and `reopen`, Program.cs instantiates a `TicketCommandRegistry`
(defined in `src/ThroughlineBuild.Commands/TicketCommandRegistry.cs`), registers command
instances, and calls `registry.TryGet(verb)` to resolve the handler. `close`, `defer`, and
`reopen` are wired up inside `WireUpConditionalCommands` (local function near line 1740) because
they require an LLM client that is only constructed after secret validation.

---

## Help string location summary

All current user-facing help text lives in a single place:

- **File:** `src/ThroughlineBuild.Cli/CliUsage.cs`
- **Symbol:** `CliUsage.UsageText` (a `public const string` raw-string literal)
- **Content:** Full usage block covering all verbs, flags, config keys, exit codes, and progress
  digest description. No per-command help strings exist elsewhere; every verb's documentation is
  inlined into this one constant.
- **Displayed:** stdout on `build help` / `build --help` / `build` (Program.cs line 25);
  stderr on unknown verb (Program.cs line 737).
