# Operation: build-init

Add `build init` to scaffold a `.build/config.toml` in a new repo with sensible defaults and prompts for the project-specific values. Small op-doc. Hard-depends on op-14's reshaped config schema - this is the one place where building too early scaffolds a config the multi-agent hard-break immediately invalidates.

## Why this exists

Today, onboarding a repo means hand-writing `.build/config.toml`. `build init` removes that friction: it writes a valid config with defaults and prompts for `project_id`, the Plane URL, `workflow_tool`, and `default_agent`. It must target the post-op-14 schema (per-agent sub-tables under `[workers]`, sizes maps), because op-14 hard-breaks the old flat shape - so this depends on op-14 Brief 02. Its scaffolding also grows as agents land: each new agent op-doc adds a `[workers.<agent>]` block that init should optionally scaffold. Build this after op-14 (required) and ideally after the agents whose blocks it should offer.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | build init: command + config template | - | S |

Single small plan. (Depends on op-14 being landed, tracked at the roadmap level, not as a plan dep here.)

## Plan A: build init

### Goal

`build init` scaffolds a valid, current-schema `.build/config.toml`, prompting for the values that cannot be defaulted and filling sensible defaults for the rest.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | init-config-template | A current-schema config template with defaults and placeholders, sourced from the canonical schema | - | src/ThroughlineBuild.Cli/InitConfigTemplate.cs (or embedded resource) |
| 02 | build-init-command | `build init` verb: prompt for project values, render the template, write `.build/config.toml`, refuse to clobber | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |

### Briefs - detail

#### Brief 01: init-config-template

Goal: A single source-of-truth template for a fresh config in the current (post-op-14) schema.

Inputs: the post-op-14 `WorkersConfig` schema (`[workers]` with `default_agent`, `timeout_minutes`; `[workers.claude-code]` with `executable`, `max_output_tokens`, `[workers.claude-code.sizes]`); the `[ticketing]` / `[llm]` / other sections from `.build/config.toml.example`; whatever `workflow_tool` resolves to after the workflow_tool config fix.

Outputs:
- A template rendering a valid `.build/config.toml`: required-value placeholders (project_id, Plane URL, workflow_tool, default_agent) clearly marked, everything else defaulted (claude-code executable, timeout, max_output_tokens, a default sizes map).
- Structure that makes adding a `[workers.<agent>]` block a localized edit as agents land.

Acceptance:
- [ ] The template renders a config that parses cleanly under the current schema
- [ ] Required values are clearly marked placeholders; the rest are sensible defaults
- [ ] Adding a future agent block is a localized change

Notes: Keep this in lockstep with `.build/config.toml.example`; both must reflect the current schema. If they drift, init produces invalid configs.

OOS:
- Interactive multi-agent setup wizardry
- scaffolding agent blocks for agents not yet shipped (add per agent as they land)
- Probing for Plane project / workflow_tool values from the environment (operator supplies)

#### Brief 02: build-init-command

Goal: The `build init` verb.

Inputs: the template from B01; CLI dispatch; a prompt/input helper.

Outputs:
- `build init` prompts for the required values (or accepts them as flags for non-interactive use), renders the template, and writes `.build/config.toml`.
- Refuses to overwrite an existing `.build/config.toml` unless `--force`.
- Usage text documents the verb and flags.

Acceptance:
- [ ] `build init` writes a valid `.build/config.toml` from prompts or flags
- [ ] Existing config is not clobbered without `--force`
- [ ] The written config passes the normal config loader
- [ ] Usage documents the verb

Notes: Refuses-to-clobber semantics matter for operator safety - the `--force` flag is the only path to overwrite an existing config. Prompts should accept the same values that the operator could provide as flags, so non-interactive use (CI, automation) works identically to interactive prompting. The template from B01 is the single source of truth for the scaffolded shape.

OOS:
- Repo detection / git init
- installing the binary
- provisioning Plane projects

## What done looks like

In a fresh repo, `build init` produces a working `.build/config.toml` on the current schema - claude-code wired with a default sizes map, project values prompted - so the next `build` command runs without hand-editing TOML. As each agent ships, init's template gains an optional block for it.