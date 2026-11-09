# Large source refactor

Behavior baseline: `93808e4f` (the published 1.7.5-snapshot-1 source). The original source archive is kept outside this checkout on E:, under `backups/refactor-93808e4f/source.zip`.

## Batch 1: downloads and installation

- `ModDownloadLib`: Minecraft client files and menus, OptiFine, LiteLoader, Forge installer process, Forge installation and menus, Fabric, LabyMod, install task lifecycle and instance installation coordination.
- `ModDownload`: client repair, version catalogs for each loader, and resource requests.
- `ModModpack`: CurseForge, Modrinth, MultiMC, legacy format import and update coordination.
- `PageDownloadInstall` and `PageInstanceInstall`: version catalog presentation, selected component presentation, and loader family UI interactions.
- `PageInstanceExport`: content selection, export configuration and export coordination.
- `ResourceRequestService` owns source selection, retries and resource HTTP request execution; existing entry points forward to it.
- `VersionJsonMerger` owns the actual version JSON merge and LWJGL detection; the old entry point forwards to it.

State and field initialization remain in each original owner in the original order. Partial declarations are used where UI or shared loader state requires the same owner; the service extractions above are independent of those owners' private fields. Existing members retain signatures and default values.

## Verification

`tools/SourceStructure` compares C# syntax tokens with the fixed Git baseline. It checks existing members, attributes, bases, constraints and initializer order. Service moves must be explicitly listed in `relocations.json`: the old member must be the expected forwarding expression and the new member must preserve the original implementation. CI runs this in DEBUG, CI, RELEASE and BETA configurations, then builds the TestAll plugin and runs the existing unit tests. The existing build matrix covers x64 and ARM64.

The tool emits native Codex patches for reviewed member ranges; it never rewrites source files itself. Its report lists all 30 original large files and their current sizes. The final gate will require every target and every added source to be at most 1000 lines.

One separately authorized UI change is tracked explicitly: welcome page composition no longer adds the selected-instance card or recent-world card. Other UI behavior is unchanged.

Passing syntax/contract checks and CI is not proof of full GUI or real Minecraft process equivalence. Manual regression remains necessary for download cancellation, game launching, authentication, resource management, plugins/Mixin, Bedrock/CIK and Lunar interactions.
