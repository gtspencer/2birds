---
name: review-plan
description: Review an existing implementation plan against its specification and the current codebase, then write a plan review.
---

# Review Plan

Critically review the current implementation plan before implementation begins.

Do NOT implement the feature.

Do NOT rewrite the plan unless explicitly asked.

## Inputs

The user will provide a plan markdown file.

Then locate its corresponding:

`{Feature_Name}_Spec.md`

when available.

## Process

1. Read the entire implementation plan.
2. Read the corresponding specification.
3. Inspect the relevant existing codebase.
4. Verify that assumptions made by the plan match the actual repository.
5. Check that every important requirement in the specification is addressed.
6. Identify missing work, incorrect assumptions, unnecessary complexity, architectural conflicts, sequencing problems, and testing gaps.
7. Look specifically for opportunities to simplify the plan while preserving the specification.
8. Distinguish blocking issues from optional improvements.

## Review criteria

Evaluate the plan for:

- correctness
- completeness
- consistency with the specification
- consistency with the existing architecture
- unnecessary complexity
- duplicated systems or logic
- poor ownership boundaries
- incorrect assumptions about existing code
- missing dependencies
- implementation ordering
- migration/state-transition issues
- concurrency/networking issues
- persistence issues
- performance concerns
- edge cases
- testing coverage
- validation strategy
- cleanup of replaced code

Do not criticize hypothetical problems that are not relevant to the actual feature or codebase.  Do not invent issues; it is okay to say there are none.

## Findings

Categorize findings as:

### Blocking

Issues that should be resolved before implementation.

### Important

Meaningful issues that are unlikely to completely block implementation but should be corrected.

### Suggestions

Useful simplifications or improvements that are optional.

For each finding include:

- relevant plan section
- relevant file/system when applicable
- issue
- why it matters
- recommended change

## Requirement coverage

Include a brief check of whether the plan covers the major requirements from the specification.

Highlight any specification requirement that is absent or inadequately addressed.

## Conclusion

End with one of:

- `Plan is ready for implementation.`
- `Plan is ready after minor revisions.`
- `Plan needs revision before implementation.`

Follow that with a concise explanation of the remaining work.

## Output

Derive the feature name from the plan filename.

For:

`Player_Controller_Plan.md`

write:

`Player_Controller_Plan_Review.md`

In general:

`{Feature_Name}_Plan_Review.md`

Write the review to that Markdown file in the current working directory.

Do not merely print the review in chat. Create or update the Markdown file.