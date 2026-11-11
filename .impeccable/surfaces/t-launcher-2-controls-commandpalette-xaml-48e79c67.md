---
version: 1
slug: "t-launcher-2-controls-commandpalette-xaml-48e79c67"
primary_target: "Plain Craft Launcher 2/Controls/CommandPalette.xaml"
related_targets: ["Plain Craft Launcher 2/FormMain.CommandPalette.cs"]
---

# Command palette

Mode: Operate. Local extension of the existing native WPF launcher; code-led, no replacement visual world or generated imagery.

## Direction contract

THESIS: Reach existing launcher destinations and selected-instance mods from one keyboard-focused search surface.

OWN-WORLD: Inherit LaunchFontFamily, ColorBrushBackground, ColorBrush1 and theme accents. Use the existing search input and floating search-button vocabulary; no new wallpaper or glass material.

STORY: Open with Ctrl+K or the visible search button, type, select with arrows, open with Enter, dismiss with Esc or outside click. Instance results open management; installed mods locate local files. The user explicitly chose current selected instance mods.

FIRST VIEWPORT: Center a 600-DIP maximum-width surface within the existing window. Title and close action above a 40-DIP search field, virtualized results below with title/description/category, then readable status and keyboard hints. Fit the launcher's 810-by-470 minimum window.

FORM: Narrow existing-surface extension, no concept tournament; seed: none (pinned). User accepted the proposed Ctrl+K command panel and selected “当前选中实例的已安装模组”. Preserve existing DESIGN.md.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Evidence scope

Six local native WPF RenderTargetBitmap captures cover the compiled CommandPalette control in an isolated test window using launcher application styles. Catalog data in the screenshots is synthetic. Dark resources are simulated; this is not proof of the full running launcher, current game instances, explorer actions, or real IME input. The scanner smoke checks separately parse a local synthetic Fabric jar using the production mod parser. No raster assets ship with this feature.
