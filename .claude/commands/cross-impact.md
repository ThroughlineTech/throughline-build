# cross-impact

Run `build sop brief cross-impact --json` from the repository you are operating on and follow the returned SOP text exactly.

If `build` is missing from PATH, is not executable, exits nonzero, reports an unknown SOP, reports an unsatisfied `conductor.min_build_version`, reports that doctor failed, or omits `data.sopText`, stop and report the failure. Do not use cached prose and do not improvise a fallback.
