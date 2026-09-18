---
name: plan
description: Create a detailed implementation plan from an existing feature specification.
---

# Plan

Create an implementation plan for the requested feature based primarily on its specification.

Do NOT implement the feature.

## Inputs

The user will provide a spec markdown file, and potentially additional planning constraints.

## Process

1. Read the entire specification.
2. Inspect the existing repository and relevant code before designing the plan.
3. Understand the current architecture, patterns, abstractions, dependencies, and conventions.
4. Identify which existing systems can be reused and which must change.
5. Resolve implementation details from the actual codebase rather than assuming how the project works.
6. Build a concrete implementation plan that another coding agent could execute without needing to rediscover the architecture.
7. Prefer the simplest solution that satisfies the specification.
8. Avoid speculative abstractions or unnecessary future-proofing unless explicitly asked for it.
9. Explicitly identify dependencies between implementation steps.
10. Include validation and testing work.

## Plan requirements

Where applicable, include:

- files/modules to create
- files/modules to modify
- existing systems to reuse
- data model changes
- API/interface changes
- state ownership
- lifecycle/flow changes
- networking behavior
- persistence behavior
- migration concerns
- edge cases
- tests
- manual validation
- cleanup/removal of superseded code

For each significant change, explain:

- where it happens
- what changes
- why it is needed

Reference concrete files, classes, methods, systems, or directories whenever they can be determined from the repository.

## Structure

Organize the work into logical implementation stages.

For example:

# {Feature Name} Implementation Plan

## Context

Brief summary of the existing system and the approach being taken.

## Implementation

### 1. {First logical change}

- Files:
  - `path/to/File.cs`
- Changes:
  - ...
- Notes:
  - ...

### 2. {Next logical change}

...

## Testing

- Automated:
  - ...
- Manual:
  - ...

## Risks / Considerations

Only include meaningful risks or unresolved implementation concerns.

## Acceptance Checklist

A concise checklist derived from the specification's acceptance criteria.

## Output

Determine the feature name from the specification filename when possible.

For:

`Player_Controller_Spec.md`

write:

`Player_Controller_Plan.md`

In general:

`{Feature_Name}_Plan.md`

Write the plan to that Markdown file in the current working directory.

Do not merely print the plan in chat. Create or update the Markdown file.