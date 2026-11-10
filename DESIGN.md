# Optional Tahoe appearance

## Direction and surface

Mode: Operate. This is the user's pinned macOS Tahoe-inspired native launcher
appearance, including the first-screen navigation, sidebar and launch actions.
The signature is a transparent lens with visible edge displacement, a thin
directional highlight, and responsive press feedback.

## Material system

- Clear glass: navigation and actions, tint `0.10`; primary actions use an additional
  translucent accent wash. Never cover this layer with an opaque white or blue fill.
- Sidebar tint `0.20`; reading groups use neutral content surfaces, without nested refraction.
- Refraction is `(d/r)^8`, coefficient `0.05`; blur is `2 DIP`, highlight `0.65`,
  dispersion `0.20`. These are public approximation parameters, not Apple internals.
- In desktop mode Windows Graphics Capture supplies real pixels behind this window.
  The captured region is fed to the same eighth-power WPF shader. There is no Acrylic path.
- Exclude the launcher's HWND from capture to prevent feedback; restore its prior
  display affinity on exit, disable or failure. This also excludes it from system screenshots.
- Each sampled source is an independent sibling backdrop. Foreground controls are
  never recursively captured into their own material.
- High-contrast mode uses opaque system colors and a clear outline.

## Composition

- Floating central navigation group; separate compact window-control group.
- Sidebar floats inside the window and clips its content to its rounded boundary.
- Content groups have a quiet reading surface, restrained headings and no colored halo.
- Empty states use typography and space rather than an extra nested panel.
- No authored wallpaper or decorative blue background. Desktop mode hides the
  configured launcher wallpaper without deleting it; image/video configuration is retained.

## Type and state

- Default Windows font: Segoe UI Variable Text, Microsoft YaHei UI fallback.
  Respect an explicit custom font and restore original resources when disabled.
- Use neutral readable text; accent identifies actions and selection.
- Body/action labels retain the existing native sizes; section headings use semibold.
- Buttons have visible hover, press, disabled and keyboard-focus states.
- Android press motion retains its established parameters and stops rendering when at rest.
- Capture is frame driven, reads back only monitor intersections with the window,
  throttles to 30 Hz, skips unchanged images and pauses when hidden or minimized.

## Reference work and limitations

New online research: `BarredEwe/LiquidGlass` (`81d9218`), Swift/Metal source,
and a fresh fetch of the previously supplied `xiaofangstudio/liquid-glass-web`.
Existing Android reference: `Kyant0/AndroidLiquidGlass` (`65ab177`).
Their source shows SDF boundaries, separated material/highlight layers, backdrop
sampling and event-driven rendering. The Windows implementation keeps the user's
eighth-power profile rather than copying another refraction curve.

Browser access to developer.apple.com was denied this turn. Apple comparisons
use the already cached official transcript and images. No private Apple shader,
Apple font, wallpaper artwork or complete macOS pixel-parity claim is included.
