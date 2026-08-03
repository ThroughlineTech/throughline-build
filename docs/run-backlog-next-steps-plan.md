## The doc you're thinking of

`docs/research/sop-bundle-design.md` on the branch — that's the design of record, and it's where the two-layer stub model is specified (line 106: "Claude and Codex discover capabilities differently and cannot share one file"). The *original* kit docs weren't lost either: `read-this-human.md`, `migrate-from-hand-rolled.md`, and the kit `README.md` are all preserved inside the rollback archive tarball, so nothing was destroyed.

## Status: the mechanism works, but not here

The six consumer repos are live and passing. But **602, 604, 606, and 609 live in latticeflow, and latticeflow is not an enrolled consumer** — no `.build/conductor.toml`, no stubs. That's my doing: I flagged it as half-migrated in round 1, and the fix was to de-enroll it cleanly. Correct for the review, inconvenient for what you now want.

Two things stand between you and a working `$run-backlog` here:

**1. Gates are empty — this will fail closed, correctly.** `.build/config.toml` in latticeflow has zero `[[review.checks]]`. So `sop doctor` fails `review.checks.empty` → `sop brief` refuses → the Codex stub stops. That's the silent-green-gate hazard working as designed, but it means enrollment alone won't get you a running conductor. You need `dotnet build` and `dotnet test` as gating checks first.

**2. The Codex discovery path is unverified, and it's the thing your goal actually hinges on.** The design doc records at line 42 that the *old* installer wrote to `$CODEX_HOME/skills`. The new stubs go repo-local to `.agents/skills/run-backlog/SKILL.md`. I confirmed `~/.codex/skills/` currently holds only `.system` and `make-resume` — no `run-backlog`. **Whether Codex discovers repo-local `.agents/skills` is something I cannot verify from here**, and if it doesn't, `$run-backlog` simply won't resolve no matter how correct the stub is. The stub content itself is right (`name: run-backlog` front matter, five lines, defers to `build sop brief`).

That's a cheap thing to test and worth testing before anything else: open Codex in one of the already-migrated repos, type `$run-backlog`, and see whether it resolves.

## If it does resolve, the sequence is



```
build sop install                    # scaffolds conductor.toml + all four stubs
# then edit .build/conductor.toml: source roots, review invariants, escalation
# then add [[review.checks]] to .build/config.toml (dotnet build, dotnet test)
build sop doctor --json              # must pass before brief will emit
```

Then `$run-backlog` in Codex, which calls `build sop brief run-backlog --json` and follows the returned procedure. Worth knowing: the SOP verbs don't spawn workers, so this path works from inside an agent session — unlike `build chain`/`implement`/`review`.

## Two cautions specific to these tickets

- **You'd be pointing the tool at itself.** Codex implementing 602/604 means editing `build` while running `build`. That's the standing repo hazard. The SOP cuts worktrees via `build worktree`, which contains it, but I'd keep the conductor off the branch under review.
- **606 is an Investigate ticket, not an implement one**, and all four are in Backlog with no plan. run-backlog's `ticket-transaction.md` covers plan → implement → review, so it can drive them — just don't expect 606 to produce a diff.

I haven't installed anything — enrollment mutates the repo and adds a tracked `conductor.toml`, which is exactly what I asked to be reverted an hour ago, so it should be a deliberate call rather than a side effect of a question. Say the word and I'll do the install plus draft the conductor.toml and the two gating checks. But I'd test whether `$run-backlog` resolves in Codex first — if the discovery path is wrong, that's a TLB-615 finding and everything else is premature.