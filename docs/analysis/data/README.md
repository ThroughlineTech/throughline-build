# Public analysis data

This directory contains the minimized inputs used by the analysis scripts. It
does not contain complete runtime logs or raw model transcripts.

The publication sanitizer keeps only fields needed for the reported event
counts, usage totals, timing, verdicts, and side-effect classifications. It
removes backend identifiers, workspace names, filesystem paths, commit SHAs,
review prose, and raw command output. Session identifiers and build versions
are replaced with stable synthetic labels such as `session-0001` and
`build-01`.

From `docs/analysis/scripts`, verify that the checked-in corpus is already in
its canonical public form:

```sh
python sanitize_publication.py
```

The command must report that it would rewrite zero rows. Use `--write` only
when importing newly approved source material, then rerun all quantitative
scripts and review the resulting diff.
