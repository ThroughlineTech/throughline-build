# Feedback from Experiment 2

## Defect 1 - The extractor can't find the inputs, and couldn't tell "read these" from "don't read these"

In the ticket-10 stdin, the brief's `Inputs:` content is flattened into the Goal `<p>` during markdown->HTML promotion - there is no `<h3>Inputs</h3>`. `PreloadedContextBuilder`'s "scan the Inputs section" finds no section, extracts zero named inputs, and only the scaffold convention bundle survives.

And even if you fixed the section detection, scraping it for path-shaped `<code>` tokens would pull the *exclusion* paths - the read-map literally says "do not read `setupTests.ts`, `vite.config.ts`, `package.json`" - so a heuristic scrape loads exactly the wrong files.

**Fix - stop scraping prose.** Carry the inputs as an explicit, positive-only, machine-parseable block that survives promotion:

- The brief emits a fenced ` ```preload-inputs ` block (just the paths, one per line).
- The markdown->HTML renderer already preserves fenced blocks as `<pre><code class="language-preload-inputs">` (verify - the Brief 08 grammar fence is the precedent).
- `PreloadedContextBuilder` reads that block instead of `DescriptionHtml` prose.

Rides inside the description text, so no Plane schema change.

**Acceptance:**

- A brief with a `preload-inputs` block yields exactly those paths.
- Exclusion paths in prose are never loaded.
- No block -> zero (empty-section behavior preserved).

## Defect 2 - The reader read a tree that didn't have the files yet

Ticket-10's section rendered `(not found)` for `vite.config.ts` / `tsconfig.json` / `repository.test.ts`, all of which exist by worker-run time - the worker edited B06's `AdminResults.tsx` in the same worktree.

So `ImplementPhase.MakeWorktreeReader` read at brief-build time, before the integration state (B01-B06's commits) was present in the ticket worktree - exactly the timing risk doc 12 flagged and assumed chain mode dodged. It doesn't.

**Fix:** build the preload section from the worktree the worker will actually run in, after prior briefs' commits are materialized into it, immediately before spawning the worker - not at the moment the worktree is created off base.

Verify where the ticket worktree receives the integration tip (likely the `EnsureIntegrationWorktreeAsync` / materialization seam in `ChainPhase` - verify) and sequence the section build after it.

**Acceptance:** in a chain where B01-B06 ran, ticket-10's preload reads the real `aggregate.ts` / `AdminResults.tsx` contents, not `(not found)`.

## Defect 3 - It failed silently, which is the worst part

Not-found files became dead `(not found)` lines in the prompt; an empty extraction rendered a convention-only section; nothing flagged that the whole mechanism had no-opped. The experiment *looked* like it ran.

This is precisely the non-actionable noise the gate-output convention exists to kill.

**Fix** (same principle as `gate_unverified`):

- A declared input that resolves to not-found -> emit a loud, countable event (`preload_file_not_found`, naming file + ticket) and **do not** paste a `(not found)` line into the prompt.
- A brief that declares inputs but the builder loads zero -> emit `preload_empty`. This single signal would have caught experiment 2 on the first ticket.
- Per-session telemetry: files requested / loaded / bytes / not-found list, so the run can confirm the mechanism fired and the on/off ablation is measurable. On success, one terse line (`preloaded N files, K KB`) - no per-file enumeration in the worker's context.

**Acceptance:** experiment 2's exact failure (declared/derived files all not-found, silent) now produces a hard, countable signal instead of a clean-looking prompt.
