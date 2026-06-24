# Tickets: use the `build` CLI

This repo tracks work in Plane. **You never talk to Plane directly.** All ticket
operations go through the vendored `build` binary at [bin/build](bin/build) (already in
the repo - no install). The CLI holds the Plane connection and API token in
`.build/config.toml` and loads them for you.

> **You do not need any credentials, URLs, or tokens.** Do not look for them, do not ask
> for them, do not connect to a Plane API or MCP. If a `build` command ever fails with a
> config or missing-secret error, stop and tell the human - the fix is in
> `.build/config.toml`, not something for you to hunt down.

## Which repo's tickets you hit (cwd decides, not the binary)

`build` finds its backend config by walking **up from the current working directory** until
it finds a `.build/config.toml`. There is no `--config` flag and no config-path env var. So
the directory you run it from - not which `build` binary you invoke - decides which Plane
project you read and write.

- The commands below, run from this repo, operate on **this** repo's tickets, because its
  `.build/config.toml` is right here.
- To operate on a **different** repo, run `build` with that repo as the working directory.
  Use a subshell so your own shell stays put:

  ```sh
  (cd /path/to/other-repo && ./bin/build list)
  (cd /path/to/other-repo && ./bin/build get <ID>)
  ```

  Wrap it in `( ... )`, not a bare `cd`: it keeps everything to one command and leaves your
  cwd unchanged. You cannot target another repo from here without `cd`-ing into it.

- You can use either repo's binary - resolution is cwd-based - but prefer the **target**
  repo's own `bin/build` (version match). If you don't know where the other repo lives, it
  is wherever it is checked out (often a sibling dir like `../other-repo`); `ls` the parent
  directory to find it.
- The target repo must have its own `.build/config.toml`. If it doesn't, `build` walks all
  the way up and fails with `config file not found: searched from <dir> upwards for
  .build/config.toml` - that repo simply isn't wired for `build`/Plane.

## The verbs you need

| Intent | Command |
|---|---|
| list / show the backlog | `build list [--state Backlog] [--type <type>] [--parent <id>]` |
| read one ticket | `build get <ID>` |
| read a ticket's comments | `build comments <ID>` |
| comment / write findings back | `build comment <ID> "<markdown>"` (or `-` to read body from stdin) |
| create a ticket | `build new - --json` with a JSON draft on stdin (fields: `title`, `type`, `description`, `acceptanceCriteria`, `labels`, `parent`); or `build new --print-template > draft.md`, fill it in, `build new draft.md` |
| move state | `build transition <ID> InProgress` |
| close / defer / reopen | `build close|defer|reopen <ID> "reason"` |
| amend (size / note / desc / AC) | `build amend <ID> (--size S\|M\|L \| --note "..." \| --description <path\|-> \| --ac <path\|->)` |

`<ID>` is the ticket identifier as shown in the first column of `build list` (e.g.
`PROJ-42`). A bare number means that project's ticket: `42` -> `<prefix>-42`. Run
`build list` once to see the prefix this repo uses.

## Changing a ticket: pick the right verb

This is where agents get confused. There are **four distinct ways** to change a ticket and
they do different things - choosing the wrong one is the most common mistake:

| You want to... | Use | What it touches |
|---|---|---|
| move it through the workflow | `build transition` | its **state** |
| close, shelve, or revive it | `build close` / `build defer` / `build reopen` | its **lifecycle** |
| edit the ticket body or size | `build amend` | its **content** |
| leave a remark for humans | `build comment` | the **discussion thread** (not the body) |

### State - `build transition <ID> <state>`

Moves the ticket between workflow states. Valid states: `Backlog`, `Planning`, `Ready`,
`InProgress`, `InReview`, `Done`, `Cancelled`. Matching is space/hyphen tolerant, so
`InReview`, `"In Review"`, and `in-review` are all accepted.

```sh
build transition <ID> InReview
```

### Lifecycle - `build close|defer|reopen <ID> "reason"`

Terminal moves with a recorded reason. `close` and `defer` **require** a reason and by
default also cascade to non-terminal child tickets (pass `--no-cascade` to affect only this
ticket). `reopen` brings a closed/deferred ticket back to active; its reason is optional.

```sh
build close  <ID> "superseded by <other-ID>"
build defer  <ID> "blocked on upstream release"
build reopen <ID>
```

Prefer `close`/`defer` over `transition <ID> Cancelled` when you want the reason recorded
and child tickets handled.

### Content - `build amend <ID> <one mode>`

Edits the ticket body or its size label. Exactly **one** mode per call:

| Mode | Effect | Append or replace? |
|---|---|---|
| `--size S\|M\|L` | set the size label | n/a |
| `--note "..."` | add a context note to the description | **APPENDS** - existing body kept |
| `--description <path\|->` | set the description | **REPLACES** - overwrites the whole body |
| `--ac <path\|->` | set the acceptance criteria | **REPLACES** the AC section |

The append-vs-replace difference is the trap: `--note` is additive and safe;
`--description` and `--ac` **overwrite**. Always `build get <ID>` to read the current body
before you replace it. `<path|->` reads from a file, or from stdin when you pass `-`:

```sh
build amend <ID> --size M
build amend <ID> --note "confirmed repro on staging"
build get <ID>                              # read current body BEFORE overwriting
build amend <ID> --description new-desc.md
printf '## Acceptance criteria\n- [ ] ...\n' | build amend <ID> --ac -
```

### Comment vs. note - don't confuse them

`build comment` posts to the **discussion thread** and never alters the ticket body - use it
to record findings, progress, or investigation notes. `build amend --note` writes **into the
description body itself** - use it only when the text belongs in the ticket's definition.
When in doubt, `comment`.

## Scripting it (`--json`)

Add `--json` to any verb for a machine-readable envelope - the only thing on stdout, with
diagnostics on stderr:

- success: `{schemaVersion, ok: true, data: ...}`
- failure: `{ok: false, error: {code, message}}` (codes: `usage`, `config_error`,
  `missing_secret`, `not_found`, `failure`)

Exit codes: `0` ok, `1` failure, `2` usage/config error, `3` missing secret.

## When you need more than the table

`build` is self-documenting - prefer it over guessing:

- `build --help` - full verb list
- `build <verb> --help` - flags and examples for one verb
- `build help <topic>` - reference docs (`config`, `exit-codes`, `summary`, `digest`)

## Composite intents

- **investigate ticket N**: `build get <ID>`, investigate with your own tools, then write
  findings back with `build comment <ID> -`.
- **work on N**: read it, implement with your normal process (branch, edit, test), then
  `build comment` + `build transition`. The full autonomous pipeline (plan -> implement ->
  review -> ship gate) is `build chain <ID>`.

## Do not

- Do not use any `/ticket-*` slash command, `ticket-*` skill, the Plane MCP
  (`mcp__plane__*`), or a hand-rolled `curl` against the Plane REST API. Those paths are
  obsolete or unsupported here. Use the `build` verbs above - they are the only supported
  interface.
