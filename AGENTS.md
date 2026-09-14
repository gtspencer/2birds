Use the Unity CLI when possible.  If not possible, fall back to the Unity MCP.

Do not write verbose comments.  If you think a comment is stricly necessary, keep it short and concise.

If you are working of a spec in /Specs/Generated, once done, move the spec to /Specs/Done.  Do not attempt to explore /Specs/Done to speculate on previous work; everything you need to know should be inside of the code.

### Networking prioritization
Prioritize simulation, visualization, and consistency across clients.  Trust clients and what they report (don't worry about game security/cheating).  Propose and implement solutions that prioritize responsiveness on each client, even if it means the gameplay is not server authoritative.  Keep network messages as small as possible, while maintaining consistency across clients.

### Machine and Stack
Windows, Unity 6000.5.7f1, FishNet 4.7.3; AMD Ryzen 9 3900X (12 cores), NVIDIA RTX 3080 Ti