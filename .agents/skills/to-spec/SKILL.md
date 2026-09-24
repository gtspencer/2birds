---
name: to-spec
description: "Turn the current conversation into a spec and publish it to a markdown file.  No interview, just synthesis of what you've already discussed."
---

This skill takes the current conversation context and codebase understanding and produces a spec. Do NOT interview the user; just synthesize what you already know.

## Process

1. Explore the repo to understand the current state of the codebase, if you haven't already. Use the project's domain glossary vocabulary throughout the spec.

2. Don't reference the brainstorm file, or any initial markdown document(s).  Ensure the spec is a standalone description of the decisions made.

3. Write the spec to a markdown file with the format `<Feature_Name>_Spec.md` at the root of the project.