# Op-Doc Format Spec - Addendum: chain-contract requirements

These rules extend the Op-Doc Format Spec. They take effect once the `investigation-provenance` operation lands, which changes the chain in two ways that make existing fields load-bearing: op-doc-scaffolded tickets are promoted straight to Ready and implemented with no plan-worker re-investigating, and the chain runs strictly sequentially in the dependency order declared in the op-doc (no parallelism, and scaffold encodes only the declared edges into Plane). Nothing here adds new structure - it tightens how the Deps columns and the briefs are written.

## Dependencies are load-bearing, not advisory

The chain runs tickets one at a time in exactly the order the declared dependencies imply. There is no parallelism to accidentally satisfy an unstated dependency, and scaffold writes only the edges you declare into Plane as `blocked_by` relations, which the chain reads back as its sole ordering source.

- Declare every real dependency. A dependency you omit becomes a wrong-order run. In chain 319 a verb ticket ran ahead of the loader it consumed because the edge was never declared, and both tickets independently created the same file.
- Intra-plan deps use the brief number; cross-plan deps use the plan letter. Cross-plan brief-to-brief deps are not expressible, so keep a dependent brief in its dependency's plan, or order the plans so the dependency's plan finishes first and state the precise brief sequencing in the plan's Goal.
- If two briefs would create or modify the same artifact, that is a dependency, not a coincidence. Order them and declare the edge so the later brief builds on the earlier one's commit instead of recreating it.

## Briefs must be implementation-ready

Scaffolded tickets are promoted to Ready and implemented directly; no plan-worker re-investigates or fills gaps. The op-doc is the plan.

- Each brief's Goal, Inputs, Outputs, and Acceptance must be sufficient for an implementer working only from the ticket, with no separate planning pass.
- Inputs must name the real files the brief reads and the specific prior-brief outputs it builds on. "Investigate the area" is not an input; a file path is.

## Lean on the carried-forward context

In a chain, each ticket's implement brief receives the prior tickets' touched-files and commit range. A dependent brief can reference the files an earlier brief produced rather than re-describing them - but only if the dependency is declared, since the declared edge is what aligns the run order with the carried-forward context.

## Common mistakes (additions)

- Omitting a real dependency from the Deps / Depends-on columns. With sequential execution this is a wrong-order run and duplicated work, not a missed optimization.
- Authoring a brief that re-creates an artifact an earlier brief already produces, instead of depending on it.
- Writing a thin brief on the assumption a planning pass will flesh it out. Promotion means there is no planning pass.