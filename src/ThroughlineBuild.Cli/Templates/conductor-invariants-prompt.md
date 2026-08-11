Derive repository-specific review invariants for `.build/conductor.toml`.

Inspect the repository's tracked code, documentation, configuration, and tests. Produce 2-5 invariants that are specific, true, and reviewable for this repository. Do not invent stack assumptions. Do not contradict the repository's existing contracts or each other.

Return ONLY TOML consisting of `[[conductor.review.invariants]]` blocks. Do not include Markdown fences, prose, headings, or any other TOML section. Do not write or modify any files.

Each block must contain a unique non-empty `id` and a concrete non-placeholder `statement`. It may also contain non-empty `paths` and a boolean `blocks_done` value.

Example shape only:

[[conductor.review.invariants]]
id = "repository-specific-id"
statement = "State a true, repository-derived invariant here."
paths = ["relevant/path/**"]
blocks_done = true
