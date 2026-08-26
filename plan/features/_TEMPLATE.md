# <Feature name>

❌ **Not started.** <One-line scope.> Tracked in [PLAN.md](../../PLAN.md) Open work
(add the todo entry + a ledger row if the feature gets a step number). Relative links
resolve from the repo root.

## Why / product context

<The user problem, and how leading editors (Premiere / Resolve / FCP) do the equivalent —
prefer their behavior, defaults, naming, and shortcuts (CLAUDE.md rule); note deliberate
departures and why.>

## Existing seams to build on

<Concrete files/types this lands on — per ARCHITECTURE §17, features land on existing seams,
not rewrites. Name the reusable pieces found by exploration.>

## Implementation sketch

1. <Model (Core) — pure data, undoable commands, no native/UI types.>
2. <Render/Media/Audio — respect §1 (no managed pixels per frame) and the §2 dependency
   directions.>
3. <Persistence — additive + nullable DTO fields; pre-existing files load unchanged.>
4. <UI (App) — composition-root wiring; STYLE_GUIDE.md for any new dialog/popup.>
5. <Docs/inventory — add the FEATURES.md row (starts ❌) in the shipping change; check
   README Features/Roadmap granularity.>

## Tests

<Headless-first: Core command/model tests, golden frames for render changes,
round-trip for persistence. Name the test project(s).>

## On completion

Flip this feature's PLAN.md ledger row / check its todo, append the DONE log to the matching
`plan/history/steps-*.md` entry (or add a new `## Step N` there if it got a number), and
update FEATURES.md — then delete or archive the no-longer-open parts of this file.
