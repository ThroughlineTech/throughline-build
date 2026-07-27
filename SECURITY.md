# Security policy

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability or accidental
credential exposure. Use GitHub's private vulnerability reporting flow:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Include affected versions or commits, reproduction steps, impact, and any
   suggested mitigation.

You should receive an acknowledgement within seven days. A fix timeline will
depend on severity and reproducibility.

## Supported versions

Throughline Build is currently source-distributed and pre-1.0. Security fixes
target the latest commit on `main`; older commits and local forks are not
maintained as separate release lines.

Never include live API tokens, backend configuration, raw worker transcripts,
or private event logs in a report attachment. Use synthetic reproductions or
redacted excerpts.
