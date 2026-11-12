# PCL Lite 1.7.7-hotfix-3

This hotfix adds adjustable liquid-glass materials and corrects audited launch, instance, update, network and UI defects. It builds on 1.7.7-hotfix-2, which removed the legacy square shadow visible outside the glass corners.

## Liquid glass

- Added a glass-frosting slider (0–24 DIP, default 8). It applies a smooth Gaussian blur to the sampled glass background; text, icons and controls remain sharp.
- Added an edge-refraction slider (100%–300%). The minimum and default preserve the previous refraction strength.
- Kept desktop sampling live while moving the launcher and capped sampling at 60 FPS during a move. Sampling tracks the real window independently of delayed WPF position updates and keeps the existing capture-exclusion behavior.
- Removed the legacy outer shadow while glass is enabled and restore its prior state when glass is disabled. Kept the DWM frame and native sampler aligned with the rounded window viewport to prevent black corner bleed and stale-frame offsets.
- Kept background opacity separate from the sampled desktop, glass, text and launcher logo.

## Minecraft instances and mod migration

- Corrected Minecraft version comparisons used for personal game-option synchronization and Java compatibility limits, including Java 10, 15 and 18 upper bounds and the Forge 1.16 Java 8 update boundary.
- Avoided re-merging ancestors already included in an inherited version JSON. Renaming an instance now preserves separators in setup paths, handles case-only renames without colliding with the natives directory, and resolves paths against the selected Minecraft root.
- Fixed LabyMod selection and its per-resource-type directory paths.
- Planned resource migration using the destination filename, preventing temporary source-name collisions. Dependency planning now accounts for a Fabric API already selected by the installer.
- Corrected update planning when an author reuses an old path for a new file, while retaining the recorded author baseline for files modified by the player.
- Validated relative Java paths against the launcher directory.
- Made corrupt personal-option lock handling conservative: ambiguous ownership preserves existing user files and skips synchronization rather than overwriting them.
- Recreated the saves watcher after reopening its page, released the screenshot pagination lock on early exits, and cleared prior server cards before refreshing results.

## Network, updates and account handling

- Limited URI-keyed HTTP body caching to eligible anonymous GET 200 responses. Preserved status and headers, and skipped responses that vary, set cookies, are private, or prohibit caching; old body-only entries are ignored.
- Kept caller-owned HTTP request content available for retries and fallbacks, disposed of ping and connection resources on cancellation, and made IPv6 discovery inspect all eligible adapters.
- Corrected DNS SRV record serialization and added field and buffer bounds checks.
- Added an explicit DNS-resolution network-purpose check to the DoH path; denied requests fall back to the system resolver without treating valid empty DNS answers as errors.
- Selected update executables by architecture, stopped retrying a failed source repeatedly within the same operation, and fixed temporary-path accumulation when a download task is rerun.
- Preserved newly issued Lunar refresh tokens, supported Lunar-only account refresh through the existing identity store, and hardened UUID parsing and asynchronous client disposal.
- Avoided null callbacks for announcement buttons that do not exist.

## UI and interaction

- Fixed startup opacity being applied twice, restored the intended focus after confirmation countdowns, and corrected textbox and icon color updates.
- Connected settings-page parent/child visibility controls to their events and corrected video-background error-handler unsubscription.
- Fixed modal ownership so a popup is removed from the modal stack exactly once.
- Added the missing disabled-state colors for glass-mode combo boxes.

## Validation

- Added regression coverage for glass range limits, frost smoothing and source release, live capture movement and window-corner composition.
- Added targeted tests for HTTP cache isolation/status preservation, DNS SRV serialization, update asset selection, personal-option synchronization, instance inheritance, pack update planning, resource-copy safety, Lunar token handling, and UI state transitions.
- The launcher CI build and focused tests passed locally. The release workflow runs Windows UI smoke, x64 and ARM64 builds, full unit tests, signing and artifact verification before publishing.
- Desktop interaction was not manually exercised during this hotfix.

