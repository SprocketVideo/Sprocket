# 03: Keyframed shape masks on a clip's effects

**What to build:** A user draws a rectangle / ellipse / bezier mask on a clip, keyframes it, and limits an effect to inside (or outside) it, with feather — in preview and export alike.

**Blocked by:** 2

**Status:** needs-triage

- [ ] Mask shapes persist and are undoable; keyframes use the existing animatable-value contract
- [ ] An effect can be restricted to a mask with invert and feather
- [ ] Preview and export match (golden-frame test)
- [ ] FEATURES.md row added (❌ undocumented)
