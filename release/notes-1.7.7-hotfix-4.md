# PCL Lite 1.7.7-hotfix-4

Fixes the persistent white outline around the launcher window introduced by hotfix-3. The extra runtime change to `WindowChrome.GlassFrameThickness` is removed; the original XAML frame and existing DWM initialization are left in place. Adjustable glass frosting/refraction and live desktop sampling remain enabled.

## Validation

- Added a source contract test asserting the configured XAML frame remains `GlassFrameThickness="0"` and glass mode does not mutate the system frame property.
- Focused liquid-glass tests passed (30/30); the launcher CI build passed locally.
- No visible window or screen was opened for testing.
