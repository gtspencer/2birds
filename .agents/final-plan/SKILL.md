---

name: final-plan
description: Create the final implementation plan by reconciling the feature specification, initial implementation plan, and plan review.
-----------------------------------------------------------------------------------------------------------------------------------------

# Final Plan

Create the final implementation plan for the requested feature.

Use the feature specification, initial implementation plan, and plan review together to produce a single authoritative implementation plan.

Do NOT implement the feature.

## Inputs

The user will provide a plan markdown file, and a plan review markdown file, and optionally the original spec.

## Process

1. Read the entire specification.
2. Read the entire initial implementation plan.
3. Read the entire plan review.
4. Inspect the relevant repository/codebase where necessary to verify assumptions or resolve conflicts.
5. Treat the specification as the source of truth for required behavior.
6. Use the initial plan as the starting implementation approach.
7. Evaluate every substantive finding from the plan review.
8. Incorporate review findings that improve correctness, completeness, simplicity, sequencing, or alignment with the specification.
9. Resolve contradictions using the specification and actual codebase.
10. Remove obsolete, superseded, duplicated, or incorrect instructions.
11. Produce a self-contained final plan that can be understood and executed without reading any other planning document.
12. Prefer the simplest implementation that fully satisfies the specification.

Do not blindly apply review feedback. If a recommendation conflicts with the specification or actual repository architecture, use the correct approach instead.

## Standalone requirement

The final plan must be completely standalone.

Do NOT:

* reference the initial plan
* reference the plan review
* refer to "the previous plan"
* refer to "the review"
* say that something was "changed from the original plan"
* describe how the final plan differs from earlier documents
* require the reader to consult any other planning document
* include reconciliation notes, revision history, or review-response commentary

The initial plan and review are inputs only.

Their content should be fully absorbed into the final plan so that the reader sees only the resulting implementation approach.

The final plan should read as though it were written correctly in its final form from the beginning.

## Final plan requirements

The final plan should contain everything necessary for another coding agent to implement the feature.

Where applicable, include:

* files/modules to create
* files/modules to modify
* existing systems to reuse
* classes/methods/components affected
* data model changes
* API/interface changes
* state ownership
* lifecycle/flow changes
* networking behavior
* persistence behavior
* migration concerns
* dependencies between steps
* edge cases
* error handling
* performance considerations
* automated tests
* manual validation
* cleanup/removal of superseded code

Reference concrete files, classes, methods, systems, and directories whenever they can be determined from the repository.

For each significant implementation step, explain:

* where the change happens
* what changes
* why it is needed
* any dependencies on earlier steps

## Structure

Use this general structure, adapting it where appropriate:

# {Feature Name} Final Implementation Plan

## Context

Briefly summarize:

* the feature being implemented
* the relevant existing architecture
* the implementation approach

## Implementation

### 1. {First logical change}

* Files:

  * `path/to/File.cs`
* Changes:

  * ...
* Rationale:

  * ...
* Dependencies:

  * ...

### 2. {Next logical change}

...

Order implementation steps so they can be executed sequentially with minimal ambiguity.

## Testing

### Automated

* ...

### Manual

* ...

Include testing for important edge cases and acceptance criteria from the specification.

## Risks / Considerations

Include only meaningful implementation risks, constraints, or unresolved concerns.

Do not include historical discussion about issues that were already resolved while creating the final plan.

## Acceptance Checklist

Create a concise checklist covering the specification's requirements and expected behavior.

Example:

* [ ] Core behavior implemented
* [ ] Required state is synchronized correctly
* [ ] Relevant edge cases handled
* [ ] Automated tests pass
* [ ] Manual acceptance criteria verified
* [ ] Superseded code removed

## Output

Derive the feature name from the source filenames when possible.

For:

`Player_Controller_Spec.md`

`Player_Controller_Plan.md`

`Player_Controller_Plan_Review.md`

write:

`Player_Controller_Plan_Final.md`

In general:

`{Feature_Name}_Plan_Final.md`

Write the complete final implementation plan to that Markdown file in the current working directory.

The final document must be standalone and authoritative.

Do not merely print the final plan in chat. Create or update the Markdown file.
