# Style Guide — Sprocket UI surfaces

Normative rules for coloring UI surfaces in `Sprocket.App`. [UI.md](UI.md) §1 describes the design
language the mockup implies; this document is the contract new dialogs, popups, and controls must
follow so surfaces never drift apart again.

## Source of truth

- **`src/Sprocket.App/Palette.cs`** is the single source of truth for the shared color tokens.
  `App.axaml` re-exposes them as XAML brush resources (`{x:Static app:Palette.*}`), so the XAML
  shell, the code-built dialogs, and the custom-drawn Skia surfaces all consume one definition.
- **Never hardcode a hex color** in a dialog, popup, or control for anything Palette already
  names. A raw hex value in review is a flag: either use the token, or (for a genuinely
  component-local color, e.g. clip fills) keep it local to that control with a comment.

## Tokens

| Token | Value | Used for |
| --- | --- | --- |
| `WindowBg` | `#0E0E12` | Window and dialog backgrounds, **and every popup surface** — context menus, MenuFlyouts, menu-bar dropdowns, ComboBox dropdowns, plain Flyouts, ToolTips. |
| `PanelBg` | `#16161C` | Docked panes, input fills (TextBox/ComboBox closed state), secondary buttons. |
| `RaisedBg` | `#22222B` | Elevation-1 chrome: pane headers, transport buttons, caption-button hover. |
| `SectionBg` | `#101015` | Inset cards (Inspector section cards) — a step darker than the panel they sit on. |
| `Edge` | `#2A2A33` | The universal 1 px border: panes, **popup borders**, separators. |
| `InputEdge` | `#5A6270` | Borders on editable fields sitting on `SectionBg` (Edge is invisible there). |
| `Text` / `MutedText` / `FaintText` | `#D5DBE6` / `#9AA4B2` / `#8A93A3` | Text ramp; see contrast floors below. |
| `Accent` / `AccentHover` | `#6C5CE7` / `#5B4BD6` | The one indigo accent: primary buttons, hover/selection fills (always with white text), playhead, selection outlines. |
| `Good` / `Warn` / `Bad` | `#3FB950` / `#D29922` / `#F85149` | Semantic status (status dot, playback-stats health). |
| `Danger` | `#C0392B` | Destructive action (close-button hover). |

## Surface rules

- **Dialog windows** (all are code-built): `Background = Palette.WindowBgBrush`; inputs and
  secondary buttons on `PanelBg`; the primary action button on `Accent` (`Button.primary`).
- **Popups — menus, flyouts, dropdowns, tooltips**: `WindowBg` surface + 1 px `Edge` border,
  item hover = `Accent` with white text, pressed = `AccentHover`. This is implemented **once**,
  as Fluent theme-resource overrides in `App.axaml` `Application.Resources`
  (`MenuFlyoutPresenter*` / `MenuFlyoutItem*` / `FlyoutPresenter*` / `ComboBoxDropDown*` /
  `ComboBoxItem*` / `ToolTip*` keys) — creation sites must **not** set popup backgrounds locally.
  When adding a new popup-like control (e.g. `AutoCompleteBox`, `DatePicker`), check whether its
  Fluent brush keys are covered by that block and extend it there, not at the call site.
- **Deliberate departure from pro-NLE convention**: Resolve/Premiere draw menus one elevation
  step *above* the window background. Sprocket instead keeps popups at `WindowBg`, matching the
  dialogs, for a uniform near-black look — separation comes from the `Edge` border and the OS
  popup drop shadow. Don't "fix" popups back to a lighter surface.
- **Fluent accent**: `App.axaml` pins `SystemAccentColor` (+ its Dark1–3/Light1–3 ramp) to the
  Sprocket indigo so Fluent's accent-tinted states (CheckBox, Slider fill, text selection,
  ComboBox selection) match `Palette.Accent` instead of the OS accent color.

## Contrast floors

- Every step of the text ramp clears **WCAG AA (≥ 4.5:1)** on the surfaces above at label sizes;
  `FaintText` is the AA floor (5.8:1 on `PanelBg`). See the notes in `Palette.cs`.
- White text on `Accent` is 4.86:1; `AccentHover` darkens (not lightens) so white stays ≥ 4.5:1
  on hover. Never introduce a lighter hover fill behind white text.

## Checklist for a new dialog or popup

1. Window background: `Palette.WindowBgBrush`. No local hex.
2. Inputs/secondary buttons: `PanelBgBrush`; primary action: `Button.primary` / `AccentBrush`.
3. Any popup the dialog opens (ComboBox, flyout, tooltip): confirm its Fluent keys are in the
   `App.axaml` popup-overrides block; add missing keys there.
4. Text: use the ramp tokens; check contrast if placing text on a new fill.
