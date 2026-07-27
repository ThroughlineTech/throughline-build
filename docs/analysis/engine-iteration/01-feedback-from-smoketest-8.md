# Feedback from Smoketest 8

Two process defects from the Run 1 survey-app analysis. Both are cases of a gate (or the post-run tree) producing non-actionable signal.

Fold the fixes into the gate-output convention in progress: a check emits nothing on success beyond a terse one-line status, and on failure emits only the failing items with `file:line` and message. Gate output is consumed as worker/reviewer context and re-read from cache on later turns, so a body of "test 1 passed / test 2 passed / ..." is pure cache weight with zero actionable content.

> **Quiet on pass, loud and specific on fail.**

## Defect 1 - The typecheck gate is structurally vacuous

The scaffolded project uses a project-references tsconfig (`"files": []` + `references`). The typecheck check runs `tsc --noEmit`, which does not follow references, so it checks zero files and always passes.

Both real compile breaks in the run (tkt 10 null-narrowing, tkt 16 unused imports) slipped past it and were caught only by the heavier build check (`tsc -b && vite build`).

**Required:**

- The type gate must run `tsc -b --noEmit` (build mode follows references) wherever the command is configured.
- Make vacuity un-shippable - on scaffold or as a gate self-test, inject a deliberate type error and assert the type gate fails, erroring the setup if it passes rather than emitting a check that can never fail.

**Acceptance:**

- An intentional type error trips the typecheck gate (not just build).
- A passing typecheck emits one status line, no per-file enumeration.
- Typecheck and build are no longer redundant.

## Defect 2 - Worktrees are left uncleaned, poisoning post-run test runs

The chain leaves every per-ticket worktree under `.worktrees/` after success. With no vitest `test.exclude`, a root `npm test` collects the stale worktree copies (nested `node_modules` resolve React to null) and reports a false red (185 failed / 29 files).

Per-ticket verification ran inside each isolated worktree and was fine; the damage is only to anyone testing from `main` afterward.

**Required:**

- The engine prunes worktrees as their lifecycle ends - `git worktree remove` per ticket after a successful ship, and a sweep of `.worktrees/` on chain success - while leaving them in place on failure for debugging.

**Acceptance:**

- After a successful chain, `.worktrees/` is empty.
- `npm test` from `main` post-run matches a fresh clone.
- Worktrees survive on chain failure.
