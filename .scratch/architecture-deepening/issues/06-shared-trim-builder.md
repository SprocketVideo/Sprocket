# 06: Shared trim builder in Core

**What to build:** Trimming a clip edge produces the same result from a timeline drag and from MCP, including
for reversed, speed-ramped and held clips. MCP gains trims of ramped clips (it rejects them today).

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The source-span and timeline-span maths for reversed, ramped and held clips moves out of TimelineControl into a Core trim builder
- [ ] Timeline edge-trim commits and MCP trim both call it; MCP's own trim builder is deleted
- [ ] Core tests cover normal, reversed, ramped and held clips, including clamping to available media
- [ ] An MCP test trims a ramped clip successfully
