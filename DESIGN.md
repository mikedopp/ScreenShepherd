---
name: Screen Shepherd
description: The Windows desk as a trial field. Every window accounted for at true scale; strays driven back to their pen.
colors:
  p0: "#0a100d"
  p1: "#0d1410"
  p2: "#111a15"
  p3: "#15211b"
  p4: "#1b2a22"
  p5: "#24372d"
  p6: "#37503f"
  p7: "#6c7f71"
  p8: "#a9b3a5"
  wool: "#ece7d9"
  wool-dim: "#cec8b7"
  ochre: "#dca848"
  ochre-hi: "#ebbd62"
  ochre-ink: "#1c1405"
  flag: "#e0694e"
  flag-ink: "#1e0904"
  heather: "#b9a0dc"
  pasture: "#97c487"
  line: "rgba(236, 231, 217, 0.10)"
  line-2: "rgba(236, 231, 217, 0.18)"
typography:
  run-number:
    fontFamily: "Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI, sans-serif"
    fontSize: "30px"
    fontWeight: 400
    lineHeight: 0.9
  headline:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "18px"
    fontWeight: 650
    lineHeight: 1.45
  title:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 650
    lineHeight: 1.2
    letterSpacing: "-0.01em"
  subtitle:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 650
    lineHeight: 1.45
  body:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.45
  pill:
    fontFamily: "Segoe UI, system-ui, sans-serif"
    fontSize: "11px"
    fontWeight: 650
    lineHeight: 1.3
  numeral:
    fontFamily: "Bahnschrift SemiCondensed, Bahnschrift, Segoe UI, sans-serif"
    fontSize: "inherit"
    fontWeight: 400
    letterSpacing: "0.01em"
    fontFeature: "tnum"
  drive-label:
    fontFamily: "Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    letterSpacing: "0.05em"
rounded:
  tag: "4px"
  control: "6px"
  item: "7px"
  card: "8px"
  pill: "9px"
  float: "10px"
  popout: "14px"
  full: "999px"
spacing:
  hair: "2px"
  xs: "4px"
  sm: "8px"
  md: "12px"
  inset: "14px"
  shell: "18px"
  page: "24px"
components:
  button:
    backgroundColor: "{colors.p4}"
    textColor: "{colors.wool}"
    rounded: "{rounded.control}"
    padding: "0 13px"
    height: "32px"
  button-hover:
    backgroundColor: "{colors.p5}"
  button-primary:
    backgroundColor: "{colors.ochre}"
    textColor: "{colors.ochre-ink}"
    rounded: "{rounded.control}"
    padding: "0 13px"
    height: "32px"
  button-primary-hover:
    backgroundColor: "{colors.ochre-hi}"
  button-primary-calm:
    backgroundColor: "{colors.p4}"
    textColor: "{colors.p8}"
  button-danger:
    backgroundColor: "transparent"
    textColor: "{colors.flag}"
    rounded: "{rounded.control}"
    height: "32px"
  button-danger-armed:
    backgroundColor: "{colors.flag}"
    textColor: "{colors.flag-ink}"
  button-small:
    rounded: "{rounded.control}"
    padding: "0 9px"
    height: "26px"
  button-icon:
    rounded: "{rounded.control}"
    width: "32px"
    height: "32px"
  version-button:
    textColor: "{colors.wool}"
    rounded: "{rounded.full}"
    padding: "0 10px 0 4px"
    height: "26px"
  views-switch:
    backgroundColor: "{colors.p1}"
    rounded: "{rounded.pill}"
    padding: "3px"
  views-tab:
    backgroundColor: "transparent"
    textColor: "{colors.p8}"
    rounded: "{rounded.control}"
    padding: "5px 16px"
  views-tab-selected:
    backgroundColor: "{colors.p5}"
    textColor: "{colors.wool}"
  map-tool:
    backgroundColor: "transparent"
    textColor: "{colors.p8}"
    rounded: "{rounded.item}"
    padding: "0 10px"
    height: "28px"
  search-field:
    backgroundColor: "{colors.p1}"
    textColor: "{colors.wool}"
    rounded: "{rounded.control}"
    padding: "0 10px"
    height: "32px"
  select:
    backgroundColor: "{colors.p1}"
    textColor: "{colors.wool}"
    rounded: "{rounded.control}"
    padding: "0 10px"
    height: "32px"
  pill:
    textColor: "{colors.p8}"
    typography: "{typography.pill}"
    rounded: "{rounded.pill}"
    padding: "0 7px"
    height: "18px"
  tag:
    backgroundColor: "{colors.p4}"
    textColor: "{colors.wool-dim}"
    rounded: "{rounded.tag}"
    padding: "1px 7px"
  flock-row:
    backgroundColor: "transparent"
    textColor: "{colors.wool}"
    padding: "7px 14px"
  verdict:
    backgroundColor: "{colors.p2}"
    textColor: "{colors.wool}"
    padding: "12px 18px 13px"
  monitor-card:
    backgroundColor: "{colors.p2}"
    rounded: "{rounded.card}"
  monitor-card-header:
    backgroundColor: "{colors.p3}"
    padding: "12px 14px"
  switch:
    backgroundColor: "{colors.p5}"
    rounded: "11px"
    width: "38px"
    height: "22px"
  switch-on:
    backgroundColor: "{colors.ochre}"
  popout:
    rounded: "{rounded.popout}"
    width: "720px"
  toast:
    textColor: "{colors.wool}"
    rounded: "{rounded.float}"
    padding: "9px 14px"
---

# Design System: Screen Shepherd

## Overview

**Creative North Star: "The Trial Field"**

Screen Shepherd draws the Windows desk as a sheepdog trial course at dusk. The ground is night pasture on a single moss tonal ramp; monitors are pens drawn to their real pixel geometry and ringed by a fence line with posts; app windows are wool-white tiles at true scale; a window no display can reach sits in a hatched band beyond the fence with an ochre drive line back to the pen Rescue will send it to. Everything else (status line, the flock list, health verdicts, the Health view, the version pop-out) is the same ground with the same few signals.

The window is dense and quiet, set at 13px Segoe UI with Bahnschrift numerals for coordinates, counts and modes, like a show programme. Colour does very little work until something is wrong or something is about to move, and then each colour has exactly one job. Depth comes from steps on the moss ramp, not from shadows. The only floating surfaces (map toolbar, selection bar, drag readout, Send-to menu, version pop-out, toast) are frosted glass over the desk, and they go solid whenever Windows or the user asks.

This world deliberately turns away from the dark admin dashboard built of stat strips, card grids and wide tables, and from gamer or RGB overlay looks. The map is the product; lists and cards are its index.

**Key Characteristics:**
- One moss tonal ramp (p0 to p8) for every surface, line and secondary text.
- Colour leaves the ramp only for state: ochre for where things go, flag red for stranded, heather for partly off a display, pasture green for healthy.
- A to-scale desk map with a fence, posts, pens, wool tiles and drive lines.
- Bahnschrift SemiCondensed tabular numerals for every coordinate, count and mode; Bahnschrift SemiBold Condensed run numbers for displays.
- Glass only on surfaces that float over the desk, with a solid fallback Windows can force.
- Motion only when state changes: the drive line plays once when a rescue runs.

## Colors

A near-black green-grey ramp carries the whole interface, with four small, saturated state colours used only to mean one thing each.

### Primary
- **Timber Ochre** (`ochre`): where things go. The primary action (Rescue stranded with its count, Drive home), move targets on the map (the dashed pen a stray is driven to, the solid target outline and ghost while dragging), the drive line and its label, the selected window tile and row, checked switches, slider thumbs, the focus ring and the text caret. `ochre-hi` is its hover; `ochre-ink` is the only text colour placed on solid ochre (8.5:1).

### Secondary
- **Flag Red** (`flag`): stranded, and destructive. Stray tiles (dashed outline over a faint red wash), the Stranded group head and count, error pills, error toasts and log lines, the danger button outline, and the armed second click of Gather all, which fills flag red with `flag-ink` text.
- **Heather** (`heather`): partly off a display, review, worth a look. Partial window outlines, warn pills and verdict dots, the Health tab's flag dot, warning toasts and reasons.
- **Pasture Green** (`pasture`): healthy. Healthy verdict dots, ok pills, the "none stranded" status, ok log levels.

### Neutral
- **The moss ramp** (`p0` to `p8`), dark to light:
  - `p0` the far edge of the field gradient; `p1` the body ground, search and select wells, the views switch track, the run log; `p2` the flock column, status line, verdict strip, cards and tables; `p3` card headers, table heads, monitor fills on the map, and the solid fallback for glass; `p4` resting buttons, tags, kbd, silhouettes; `p5` button hover, the selected view tab, switch and slider tracks, scrollbar thumbs; `p6` the fence and monitor outlines, scrollbar hover.
  - `p7` posts, the minimized tile outline, unknown-size silhouettes, verdict chevrons: lines and marks only, not small text (4.2:1 on `p2`).
  - `p8` all secondary text: subtitles, meta, captions, inactive tabs and tools (8.2:1 on `p2`).
- **Wool** (`wool`): primary text, window tile outlines (70%) and fills (8%, 14% on hover), monitor names, run numbers on the map (40%). **Wool Dim** (`wool-dim`): quieter primary text such as tags, dependency locations, next steps, legend window swatch, silhouette outlines, GPU run labels.
- **Lines** (`line`, `line-2`): wool at 10% for every divider and card border; wool at 18% for control borders and the glass rim.

### Named Rules
**The One Ramp Rule.** Every surface, border and secondary text comes from the moss ramp or from wool at a stated alpha. A new surface picks a ramp step; it never introduces a new grey.

**The One Job Rule.** Ochre means "goes here or do this", flag means "stranded or destructive", heather means "partly off or worth a look", pasture means "healthy". A colour never borrows another's meaning, and none of them is decoration. Every state colour is paired with words (a pill label, a count, a legend entry), never colour alone.

**The Ink Rule.** Text on a solid state fill uses that fill's own dark ink (`ochre-ink` on ochre, `flag-ink` on flag), never wool and never black.

## Typography

**Body Font:** Segoe UI (with system-ui, sans-serif)
**Numeral Font:** Bahnschrift SemiCondensed (with Bahnschrift, Segoe UI), tabular figures
**Run Number Font:** Bahnschrift SemiBold Condensed (with Bahnschrift, Segoe UI)

**Character:** Segoe UI keeps the window a native Windows tool; Bahnschrift's condensed DIN numerals read like a trial programme's run numbers and keep coordinates and counts aligned in columns.

### Hierarchy
- **Run number** (Bahnschrift SemiBold Condensed, 30px, line-height 0.9): the display's number on its map pen (wool at 40%) and on its Health card. 17px in flock group heads, 16px in the Send-to menu, 15px with 0.06em tracking for the GPU and V adapter labels.
- **Headline** (650, 18px): the Health view heading only.
- **Title** (650, 16px, -0.01em): the app name in the shell, pop-out section headings.
- **Subtitle** (650, 14px): Health column headings and monitor card names; the verdict values sit at 650, 15px.
- **Body** (400, 13px, line-height 1.45): everything else. Explanatory paragraphs cap at 70 to 75ch.
- **Label** (12px): secondary lines, meta, small buttons, map tools, settings captions. Legend text 11.5px; window meta on the map 11px.
- **Pill** (650, 11px): status pills.
- **Numerals** (Bahnschrift SemiCondensed, tabular, +0.01em): every coordinate, size, count, refresh rate, version, time and driver version, applied as a class on the number itself so it inherits size and weight from its line.
- **Drive label** (Bahnschrift SemiBold Condensed, 13px, 0.05em, capitals because it carries the display's own name, e.g. DRIVE TO DISPLAY 1): only on the map, beside the drive line head.

### Named Rules
**The Programme Numerals Rule.** If it is a number the operator might compare or copy, it is set in Bahnschrift SemiCondensed with tabular figures. Words stay in Segoe UI.

**The Three Weights Rule.** 400 for reading, 600 for controls, 650 for names and headings. Nothing heavier except the 700 log level badge.

## Layout

The window is a three-row grid: shell header, status line, then the active view filling the rest. It is designed for the 1380 x 900 default and holds at the 1020 x 680 minimum.

- **Desk view:** a two-column grid, the map field fluid on the left and the flock column fixed at 372px on the right (330px under 1240px), with the four-cell verdict strip spanning both columns along the bottom.
- **Map field:** the desk is fitted to the panel at one scale for every monitor, centred, with 46px side padding, 112px top (room for monitor names and meta above each pen) and 70px bottom, growing to 168px when strays need the beyond-the-fence band. The map toolbar floats 14px from the top left; the legend sits 16px from the right, 12px from the bottom.
- **Health view:** a scrolling page with 22px/24px padding, a heading block capped at 980px, then two columns (monitors 1fr, graphics 1.12fr, 28px gap) that stack to one under 1240px.
- **Pop-out:** 720px wide (capped to the viewport less 32px), up to 600px tall, a 196px nav rail and a scrolling body; it opens under the version orb.
- **Rhythm:** 14px is the column inset (flock rows, search, card bodies), 18px the shell and status inset, 12px the card and verdict vertical padding, 8px the default gap between controls. Lists separate rows with 1px `line` dividers, not gaps.
- **Narrow:** under 1100px "Move previous app here" collapses to its icon (it keeps its tooltip and label) and verdict explanations hide, leaving key and value.

## Elevation & Depth

Depth is tonal. Anything resting on the window is flat and separated by a ramp step and a 1px `line`; only surfaces floating over the desk lift, and they lift as glass.

### Shadow Vocabulary
- **Float** (`box-shadow: 0 12px 32px rgba(0,0,0,.42), inset 0 1px 0 rgba(236,231,217,.08)`): every glass surface, giving one soft ambient drop and a hairline top highlight.
- **Tab nub** (`box-shadow: 0 1px 2px rgba(0,0,0,.4)`): the selected view tab only.

### Glass
- **Material:** `p3` at the glass opacity, `backdrop-filter: blur(<blur>) saturate(140%)`, a `line-2` rim, the Float shadow.
- **Defaults:** opacity 62% and blur 22px (the Chassis shell's defaults since 1.7; settings carried over from 1.6 keep their own values); the user can set opacity 25 to 95% and blur 0 to 40px, or turn glass off, with Reset glass.
- **Where:** the map toolbar, the selection bar, the drag readout, the Send-to menu, the version pop-out and the toast. Nothing else.
- **Solid fallback:** glass becomes solid `p3` when glass is off, when Windows transparency effects are off, when a high-contrast theme or forced colors are on (forced colors use Canvas with a CanvasText rim), under `prefers-reduced-transparency`, or where backdrop-filter is unsupported. The Appearance panel says which reason applies.

### Named Rules
**The Floating-Only Glass Rule.** Glass is for surfaces that hover over the desk. The map, lists, tables, cards and the shell stay solid and readable.

**The Flat Ground Rule.** Resting surfaces never cast shadows; a new panel steps up the ramp instead.

## Shapes

Small, even corners that read as hardware rather than cards: 6px for controls, 7px for list items inside rails and menus, 8px for cards and tables, 9px for pills and the views switch, 10px for small glass floats, 14px for the pop-out, and full round only for the version orb button and status dots. Tags and kbd keys use 4px. On the map, pens are 3px and window tiles 2px, so the geometry reads as measured, not designed.

The fence is the recurring silhouette: a 1.4px `p6` outline 14px outside the desk, with short 2px `p7` posts every ~70px, crossing the line. Dashes carry meaning and are reserved: a dashed ochre pen is a destination, a dashed flag tile is a stray, a short-dashed `p7` tile is a minimized window's restore spot, a dashed `p7` silhouette is a panel that doesn't report its size, a dashed ochre line is a drive line.

## Components

### Buttons
Quiet ramp blocks with one loud exception.
- **Shape:** gently squared (6px), 32px tall, 13px side padding, 600 weight, 8px gap to a 16px stroke icon.
- **Default:** `p4` fill, `line-2` border, wool text; hover steps to `p5` (150ms).
- **Primary:** ochre fill, transparent border, `ochre-ink` text, hover `ochre-hi`. Carries an optional count capsule (20px, ink at 16%). One primary per context: Rescue stranded in the shell, Drive home on a stray.
- **Calm:** the primary goes calm (`p4` fill, `p8` text, `p5` count) when there is nothing to rescue, so ochre never shouts about zero.
- **Danger:** transparent with a flag outline at 40% and flag text; hover adds a faint flag wash. **Armed:** the second-click confirmation fills flag with `flag-ink` text and relabels itself; it disarms after five seconds.
- **Small:** 26px, 9px padding, 12px text, for per-row actions. **Icon:** 32px square.
- **Focus:** a 2px ochre ring offset 2px on every focusable element.

### Shell Header
A `p4` to `p3` vertical gradient bar with a `line` bottom: icon (34px), app name and the dry-humor tagline in `p8`, the version orb button, the Desk/Health views switch, then the action group pushed right (Move previous app here, Rescue stranded, refresh). Below it, the status line on `p2`: counts in wool numerals, the stranded count in flag, the update stamp pushed right.

### Version Orb Button and Pop-out
The version button is a 26px full-round capsule from the vendored badge kit with its live orb (aurora preset, idle/thinking/success/error driven by real state) and the build's version in numerals. Its border turns ochre at 70% while the pop-out is open. The pop-out is a glass dialog: a nav rail (icon, name, version line, then Settings, Appearance, Dependencies, Help, Diagnostics as 7px items, `wool` at 6% on hover, 11% when current) and a body with 16px headings, settings rows, data tables, disclosure details and the run log.

### Views Switch
A segmented control: a `p1` track with a `line` border, 3px padding, 9px corners; tabs are transparent `p8` text, the selected tab `p5` with wool text and the tab nub shadow. A heather (or flag) 7px dot on a tab means there is something to look at there.

### Map Field (signature)
- **Ground:** a radial gradient from `p2` to `p0`.
- **Pens:** monitors as `p3` rectangles with a `p6` outline at true pixel geometry, a faint wool band where the taskbar takes the work area, the run number inside the top-left corner, the display name and identity in 13px 650 above, and connection, resolution and refresh in numerals beneath it.
- **Fence and posts:** as in Shapes.
- **Window tiles:** wool 8% fill, wool 70% hairline, 2px corners, largest drawn first so small windows stay on top. A name prints inside the bottom-left only when at least four readable letters fit; otherwise it is left off (tooltip, list and selection bar still name it). Overlapping names fold into "+N" on the top label.
- **Tile states:** hover wool 14%; partial heather outline; stray flag dashed outline with a 9% flag wash; minimized short-dashed `p7` with no fill (shown only with the Minimized tool); selected ochre 2px outline with a 14% ochre fill; dragging at 35% opacity with an ochre ghost at the drop spot and a glass numeric readout.
- **Beyond the fence:** a hatched band (wool 16% lines at 45 degrees, 55% opacity) under the fence, labelled "Beyond the fence: outside every display", holding off-screen strays left to right at true size where they fit, each with its real coordinates beside it.
- **Drive line:** a dashed ochre curve with an arrowhead from each stray to its pen, labelled DRIVE TO DISPLAY n. The pen it points at is dashed ochre.
- **Grid:** an optional 120-pixel grid per monitor (wool 7%, every fourth line 13%) that drops snap to.
- **Toolbar and legend:** a glass toolbar of 28px toggle tools (pressed = wool 11%) with a separator before Wake displays; a legend of the four marks in `p8`.

### Selection Bar
A glass bar (10px corners) floated beside the selected tile: the window's name (truncated at 220px) and its display and bounds in `p8`, then small actions (Drive home, Send to n, Center).

### Flock List
The right column, on `p2`: a search well (`p1`, `line-2` border turning ochre at 60% on focus, `/` to focus), then sticky group heads (Stranded first in flag, then each display with its run number, name, identity and count), then rows. A row is a two-column grid: process name with pills and the title in `p8` on the left; position and size in numerals on the right. Rows divide with `line`; hover wool 4%; selected ochre 12%; stray rows sit on a 6% flag wash and show their actions inline. The Gather block closes the column with its explanation, a target select and the danger button.

### Pills and Tags
Pills are 18px capsules at 11px 650 in their state colour over a 13 to 15% wash of the same colour: neutral (`p8`), ok (pasture), warn (heather), bad (flag), go (ochre). In the dependency table they wrap onto two lines instead of truncating. Tags are 4px `p4` chips with `wool-dim` text for capability lists.

### Verdict Strip
Four equal cells along the bottom of the Desk view on `p2`, divided by `line`: a `p8` key with a `p7` chevron, a 15px 650 verdict with an 8px status dot (pasture, heather, flag, or `wool-dim` for information), and a one-line `p8` explanation. Each cell opens the Health view.

### Health Cards
Monitor and adapter cards: 8px corners, `p2` body, `p3` header with a `line` divider. The header holds the 30px run number (or GPU/V label), a silhouette, the name and connection, and a health pill. Silhouettes draw every panel at one shared centimetre scale (`p4` fill, `wool-dim` outline, diagonal caption in numerals below); a panel that doesn't report its size is a dashed `p7` outline captioned "size ?". The body is a 110px label column of facts with `p8` terms and small `p8` qualifiers.

### Checks List
An ordered list in a bordered `p2` block: each check is a grid of a 96px pill column, the title and evidence with the next step in `wool-dim` and the source in small text, and an optional small action button on the right.

### Settings Controls
- **Settings row:** a full-width label with a 600 title, a 12px `p8` caption and the control on the right, divided by `line`.
- **Switch:** 38 x 22px, `p5` track with a `p8` knob; on, an ochre track with an `ochre-ink` knob that slides 16px (200ms).
- **Range slider:** 170px, a 4px `p5` track and a 16px ochre thumb ringed in `ochre-ink`, with its value in numerals at the right; disabled at 45% when glass is off.

### Toast
A glass notice (10px corners, 600 weight) centred near the bottom; it rises 10px and fades in, stays 3.6 seconds, and takes heather text for warnings and flag text for errors.

### Icon
Three screens and a drive line: a hollow grey-green stray screen top left, two wool screens, and an ochre arrow from the stray home, on a moss tile. The 32px-and-up master adds a soft top light, wool and ochre gradients and a lift shadow under the screens; the 16 to 24px master is flat fills with heavier strokes so edges land on whole pixels. In-app glyphs are 16px stroke icons (1.7 stroke, round caps) from one inline sprite.

### Motion
- **Drive line:** plays once (900ms, the shared ease-out `cubic-bezier(.16, 1, .3, 1)`) when a rescue or drive runs; it is static otherwise.
- **Pop-out entrance:** 220ms fade with a 6px drop and 98.5% scale.
- **Controls:** 150ms colour transitions; switches 200ms; the toast 200ms fade and 300ms rise.
- **Reduced motion:** all animations and transitions are removed and the drive line is not replayed.

## Do's and Don'ts

### Do:
- **Do** draw everything spatial to the real pixel geometry at one scale, and say when a tile is drawn smaller to fit.
- **Do** pick surfaces from the moss ramp and dividers from `line`; step up the ramp for a new layer.
- **Do** keep ochre for the primary action and move targets, and calm the primary when its count is zero.
- **Do** pair every state colour with words: a pill, a count, a legend entry, a status line.
- **Do** set coordinates, counts, modes, versions and times in Bahnschrift SemiCondensed tabular numerals.
- **Do** keep glass to floating surfaces and honour every solid-fallback trigger.
- **Do** guard broad actions with the danger button's armed second click.

### Don't:
- **Don't** use ochre, flag, heather or pasture as decoration or for a meaning other than its own.
- **Don't** put glass on the map, lists, tables, cards or the shell.
- **Don't** cast shadows from resting surfaces.
- **Don't** use `p7` for small text; it is for posts, marks and outlines. Secondary text is `p8`.
- **Don't** print window names that can't show at least four readable letters; leave them to the tooltip and list.
- **Don't** animate anything that isn't a state change; the drive line plays once per rescue, never on a loop.
- **Don't** grow the Desk view into a dashboard: beyond the one status line and the one verdict strip, add no stat strips, card grids or wide tables there; the map is the product.
