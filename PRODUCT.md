# PCL Lite

## Product and platform

Minecraft launcher for Windows, implemented in C# / WPF on .NET 10. Users select
accounts and instances, launch games, download resources and configure the launcher.
This task changes the optional visual theme, preserving these workflows.

## Confirmed constraints

- Native WPF controls and HLSL; the user explicitly rejected HTML and WebView replacements.
- Optional appearance in Settings → Personalization; disabling it restores the original theme.
- The user pinned macOS Tahoe toolbars, capsule buttons and floating navigation as the visual target.
- Preserve the requested eighth-power refraction with full-source UV coefficient `0.05`.
- Preserve AndroidLiquidGlass press/drag feedback (spring damping ratio `0.5`, stiffness `300`).
- Keep text and interactive controls outside the sampled and blurred background layer.
- Use actual launcher controls and real data, with simplified Chinese, traditional Chinese and English.
- Keep user wallpaper, custom home content and installed games intact.
- The user rejected the authored wallpaper and Acrylic substitution. In desktop mode,
  capture the real scene behind the window and apply the existing eighth-power shader.
  Hide configured wallpaper while sampling; preserve all original wallpaper files/settings.
- Desktop mode excludes the launcher from OS capture to prevent recursive sampling.
  Restore its original capture affinity when the mode stops.

## Evidence

- User screenshots in the conversation document the rejected flat/opaque treatment.
- Apple WWDC25 “Meet Liquid Glass” transcript and official Tahoe reference images
  are already cached under `artifacts/tahoe-reference/official/references/`.
- Public reference implementations are cached under `artifacts/reference-sources/`.
- SF Pro is not bundled. Honor the user's selected font; the default uses installed Windows UI fonts.
