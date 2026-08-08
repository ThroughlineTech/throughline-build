# Derive the project toolchain profile

You are configuring an automated build pipeline for the repository in your current working
directory. Interrogate the repository itself. Read the package manifest, project or solution file,
CI workflow, and any contributor or agent guide that states the required build and test gates.
Prefer those real entry points over guesses based on file extensions.

{{profile_rules}}

## Output

Return ONLY the JSON object described above. Do not wrap it in Markdown, a named block, or a
WORKER_RESULT envelope. Save the response as `.build/profile.json`, then run
`build install --profile .build/profile.json`.
