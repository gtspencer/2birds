### General Rules
- Use the Unity CLI when possible.  If not possible, fall back to the Unity MCP.
- Do not write verbose comments.  If you think a comment is stricly necessary, keep it short and concise.
- Do not write .meta files; let Unity generate them automatically
- As much as you can, do not edit the game scene; if an prefab needs to be created, a component added to a gameobject, or asset needs to be created, let the user know immediately.
- **This is not a work log.** Do not add status, session findings, what is or is not verified,
or a narrative of what changed and why.
- Do not attempt to validate yourself unless the user explicitly asks for it.  At the end of your message, describe visual validation the user must make.
- Cache values/references on start when possible.  Do not repeatedly set a reference in the update loop (for example, when finding the main camera)
- Do not overly rely on existing systems; if a plan or ask conflicts with an existing system, or renders the existing system unnecessary, throw away the existing system in favor of the more appropriate way to accomplish the task.
- Do not create one off tools for migrations.  Apply the atomic migrations yourself, or ask the user to make them, but do not create editor tools for one time migrations unless explicitly asked.

### Networking prioritization
Prioritize simulation, visualization, and consistency across clients.  Trust clients and what they report (don't worry about game security/cheating).  Propose and implement solutions that prioritize responsiveness on each client, even if it means the gameplay is not server authoritative.  Keep network messages as small as possible, while maintaining consistency across clients.

## How to work here

**Think before coding.** State assumptions explicitly; if two readings of the request lead to
different work, ask rather than picking silently. If a simpler approach exists, say so.

**Simplicity first.** The minimum code that solves the problem, nothing speculative. No
abstractions for single-use code, no configurability that wasn't requested, no error handling
for impossible scenarios. If you wrote 200 lines and it could be 50, rewrite it.

**Surgical changes.** Every changed line should trace to the request. Don't improve adjacent
code, don't refactor what isn't broken, match existing style even where you'd differ. Remove
imports and variables *your* change orphaned; mention pre-existing dead code rather than
deleting it.

### Stack
Unity 6000.5.7f1
FishNet 4.7.3 for networking
Unity UI Toolkit for UI
New Input System for input

### Hardware
OS: Windows
Processor: AMD Ryzen 9 3900X (12 cores)
Graphics: NVIDIA RTX 3080 Ti