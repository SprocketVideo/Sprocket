# 19: Descriptor flags replace effect-id checks

**What to build:** A plugin effect can be geometric, carry a placement rule, or modify time, just like a
built-in, because those behaviours come from descriptor metadata instead of checks against specific effect ids.

**Blocked by:** 02, 18

**Status:** ready-for-agent

- [ ] The descriptor carries: is-geometric (replaces the Transform/Stabilization check), a placement rule (the rule from 02 becomes data), and a time-modifier strategy (replaces the PosterizeTime check in the clip model)
- [ ] The id-specific checks are deleted
- [ ] A test plugin that declares itself geometric and time-modifying behaves like the equivalent built-in
