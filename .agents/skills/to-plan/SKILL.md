---
name: to-plan
description: "Turn the current spec into a fully formed plan."
---

# Create a plan markdown doc

1. Review the presented spec file and create a plan.
2. Explore the relevant code to inform the plan.
  - Propose using existing systems as much as possible.  If an asks yields a net new system, but can comfortably fit within an existing system with some tweaks or minor tradeoffs, explicitly bring up that system and ask about using in while accepting the tradeoffs.
3. Ensure the plan is well formed enough that a fresh agent can execute it in isolation.  Include explicit integration points in the code.  Do not include anything in the `Agents.md` file.
4. Write the plan to a file called `<Feature_Name>_Plan.md` at the root of the project