---
name: review
description: Thoroughly review the current implementation for correctness, regressions, architecture issues, and unnecessary complexity.
---

# Review

Review the changes presented.

## Process

1. Understand the intended behavior.
2. Inspect the relevant implementation.
4. Look for:
   - correctness problems
   - regressions
   - unnecessary complexity
   - duplicated logic
   - missing edge cases
   - poor abstractions
6. Do not modify code unless explicitly asked.

## Output

Report findings from highest to lowest importance in a markdown file.

For each issue include:
- file/location
- problem
- why it matters
- recommended fix

If there are no meaningful issues, say so.

Write your findings to a doc named `{Feature_Name}_Review.md` at the root of the project