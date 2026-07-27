# Prompt - `--debug` Instrumentation for Optimization Analysis

## Goal

Let analysis measure turns and classify them, and decouple turn-count from context-size, so we can tell which turns front-loading removed and which it still should.

This is the inverse of the gate-output work: gates stay quiet in the worker's context; debug stays verbose on disk. Same principle - context carries only what the next turn needs, analysis data goes to a side channel.

## Hard Constraint

> Record to side-channel files only. Nothing here may enter the worker's prompt or alter its behavior - we're measuring the worker, not perturbing it. `--debug` tees to disk; the worker must run identically with or without it.

## Foundational Ask - Persist the Raw Worker Transcript

`claude-code` already emits per-turn stream-json containing nearly everything below; today it's parsed for `WORKER_RESULT` and discarded.

Under `--debug`, write the full stream verbatim, one file per worker session, keyed `<chain>/<ticket>/<phase>/<rework-round>`.

The rest is mostly already in that stream - listed explicitly so it survives parsing.

## What to Capture

### Per turn

- **`usage`** - input, output, `cache_read`, `cache_creation` tokens. This is what separates "more turns" from "bigger context per turn" - the exact confound that made `cache_read` useless for judging last run.
- **Tool calls** - tool name + args, with file paths and grep/glob patterns preserved verbatim. Turn class (discovery / production / verification) is derivable from tool names - don't have the model self-label, that adds tokens and is unreliable.
- **Tool results** - size (lines/bytes) and an error flag. A failed tool call then retry is a wasted turn; I need to see them.
- **Timestamp** - for per-turn latency.

### Per session

- **The exact prompt the worker received** (system + promoted brief), verbatim. This is what lets me compute redundant-read rate - reads of files/symbols whose content was already in the prompt. That one number says whether front-loaded context is being consumed or re-read anyway, which is the cleanest measure of whether front-loading actually works.
- **The set of files read vs written.** Diffed against the brief's Files/Inputs: read-but-not-named is exactly what to add to the next op-doc's read-map; named-but-not-read is over-specified front-loading to trim.

### Per rework round

- **Trigger** (which gate/check failed, or verifier verdict), the failure payload verbatim, and the commit sha before and after.

  Lets me classify each rework as a design miss (front-loadable) vs a hygiene slip (the gate's job). Last run's two reworks were both hygiene slips - I want that split measured, not inferred.

## Format

So cross-run comparison is mechanical:

- **JSONL**, one file per session, stable schema across runs.
- **Key every file with:** chain id, ticket, brief, phase, rework round, build sha, op-doc sha/hash, worker, model.

The op-doc sha is what lets me A/B old-thin vs new-front-loaded on the same build + model and compare turn counts per brief - the experiment that actually isolates front-loading instead of confounding it.
