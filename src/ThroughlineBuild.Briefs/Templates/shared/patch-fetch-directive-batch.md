{{reason}}

The batch branch is checked out in your working directory. You MUST read the
changes yourself before judging - for each file in the list above, view its diff:

```
git diff {{from_ref}}...{{to_ref}} -- <path>
```

Use the per-ticket commit ranges above to scope each ticket. You may also
`git show`, `git log`, or read files directly. Do not run any git command that
writes (no stash/checkout/reset/rebase).
