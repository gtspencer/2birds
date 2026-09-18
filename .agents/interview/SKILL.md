---
name: interview
description: Interview the user about a feature or project and produce a detailed implementation-ready specification.
---

# Interview

Your job is to turn an idea into a clear, implementation-ready specification.

Do NOT begin implementation and do NOT create an implementation plan.

## Process

1. Understand the feature or project the user wants to build.
2. Inspect the existing codebase/project when relevant before asking questions.
3. Identify important unknowns, ambiguities, constraints, and design decisions.
4. Interview the user to resolve them.
5. Ask focused questions rather than asking for information that can be learned from the repository.
6. Continue until the specification is sufficiently complete that another agent could create an implementation plan without needing to guess about product behavior or requirements.
7. When choices have meaningful tradeoffs, briefly explain the options before asking the user to choose.
8. Do not invent requirements. Clearly record unresolved items if the user intentionally leaves something undecided.

## Specification

The final specification should include the sections that are relevant to the feature, such as:

- Overview
- Goals
- Non-goals
- User experience
- Functional requirements
- Behavior and rules
- Data/state requirements
- Interfaces and interactions
- Error and edge-case behavior
- Performance requirements
- Networking requirements
- Persistence requirements
- Compatibility constraints
- Testing/acceptance criteria
- Open questions

Do not include irrelevant sections merely to satisfy a template.  Add sections you feel are missing.

The specification should describe WHAT must be built and the important constraints, not prescribe detailed implementation steps unless an implementation choice is itself a requirement.

## Output

Write the completed specification to a Markdown file in the current working directory.

Determine a short descriptive feature name from the request.

Convert it to Pascal-style words separated by underscores:

`Player Controller` → `Player_Controller`

Name the file:

`{Feature_Name}_Spec.md`

Examples:

- `Player_Controller_Spec.md`
- `Networked_Rocks_Spec.md`
- `BirdDex_Spec.md`

At the top of the document include:

# {Feature Name} Specification

Then include the completed specification.

Do not merely print the specification in chat. Create or update the Markdown file.