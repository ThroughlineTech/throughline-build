# Feedback from Experiment 1

## Code-Change Proposal

*The real leverage - kills the constant for all briefs at once.*

A project constant re-read 7x shouldn't be fixed by pasting it into 8 briefs; fix it at the mechanism. The engine should pre-load a small, stable context bundle into every implement prompt:

- **The brief's named inputs, resolved and inlined.** This needs the op-doc to emit `Inputs:` as a structured file list (paths the engine resolves) alongside the prose rationale. The engine reads each named file's current contents from the worktree and prepends them to the prompt. T11 read its 3-4 named files and stopped - so handing it those files means those `Read` turns never happen.
- **A derived project-convention bundle:** the harness/config files (`setupTests.ts`, `vite.config.ts`) plus one canonical test example, derived once at scaffold (same channel as the check profile) and carried into every brief. This is what eliminates the `setupTests.ts`-read-7-times pattern.

Pre-loading costs a little `cache_create` (bigger prompt once) and removes `Read` turns, each of which was costing a full ~58k `cache_read` round-trip - net negative on turns and cache.

**Measurement:** named-input and harness reads should go to ~0.

## Op-Doc Change

*Runnable now, no engine work.*

Two parts:

1. Structure each brief's `Inputs:` as a parseable file list (so the engine change above can consume it later - harmless to the current engine, it's still readable prose).
2. Complete T10's read-map, which is the one genuine per-brief gap.

Let me make the T10 fix now.

Op-doc updated - T10's read-map now names the test to mirror, bounds scope to the two files it actually touches, and explicitly tells it the harness/config are stable and the rest of the tree is off-limits. That targets exactly what the transcript showed it fishing for.

## Re-Run Plan

With falsifiable predictions against this baseline, so the result is a measurement, not a vibe.

### Run now (op-doc only, no engine work)

This isolates the read-map content lever on the worst brief. From the `--debug` extractor, expect on T10:

- discovery 14 -> ~4
- the 5 globs -> 0 *(caveat: globs are partly claude-code's orientation reflex, so the hard prediction is the unnamed file reads - `Header.tsx`, `ProgressBar.tsx`, `TakeSurvey.test.tsx`, config - going to 0; the globs are the softer part)*
- `tool_errors` 5 -> ~0

The other seven briefs are the control: unchanged. Total implement discovery 78 -> ~68.

Cost barely moves - T10 is small - so this run validates the lever, it doesn't move the headline. That's fine; it's the cheap confirmation before the expensive change.

### Run after the engine pre-load lands (the real lever)

Pre-loading named inputs + the harness/convention bundle should drive the systematic rediscovery to zero: `setupTests.ts` (read 7x), config (3-4x), and prior-brief test files (6x) stop being read at all, because they arrive in the prompt.

**Predict:** implement discovery 78 -> ~30, named-input reads -> ~0 across all briefs, with `cache_create` ticking up a little (bigger prompts) and `cache_read` down (fewer turns).

The turn-class resolution gives you the attribution for free: discovery is the only class these two changes target, so if discovery drops and production/verification/"other" hold, the cut is real and yours.

## Sequencing Notes

Two, so we don't confound:

- **Don't fold the `TodoWrite`/"other" lever into either run.** The obvious template fix for it ("don't re-read files already in context") also suppresses discovery re-reads, so running it alongside the read-map/pre-load changes would make the discovery delta unattributable.
- That's its own experiment - and the ~27% "other" share says it's worth its own run.
