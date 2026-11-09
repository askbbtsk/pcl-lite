# Large source refactor

## Complete source-size inventory

| Original source | Before | Current entry | Status |
|---|---:|---:|---|
| PCL.Core/App/Lunar/LunarClientService.cs | 1320 | 229 | split |
| PCL.Core/App/Lunar/Social/LunarSocialProtocol.cs | 1249 | 122 | split |
| PCL.Core/Mixin/MixinRuntime.cs | 1025 | 196 | split |
| PCL.Core/Plugins/PluginRuntimeManager.cs | 2016 | 180 | split |
| Plain Craft Launcher 2/Controls/MyListItem.xaml.cs | 1120 | 153 | split |
| Plain Craft Launcher 2/FormMain.xaml.cs | 2559 | 352 | split |
| Plain Craft Launcher 2/Modules/Base/ModAnimation.cs | 1518 | 49 | split |
| Plain Craft Launcher 2/Modules/Base/ModBase.cs | 3769 | 274 | split |
| Plain Craft Launcher 2/Modules/Base/ModLoader.cs | 1037 | 24 | split |
| Plain Craft Launcher 2/Modules/Lunar/ModLunar.cs | 2081 | 207 | split |
| Plain Craft Launcher 2/Modules/Minecraft/McInstance.cs | 1079 | 57 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModBedrockGdk.cs | 1052 | 210 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModComp.cs | 3835 | 371 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModDownload.cs | 2484 | 209 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.cs | 2968 | 196 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModLocalComp.cs | 3053 | 167 | split |
| Plain Craft Launcher 2/Modules/Minecraft/ModModpack.cs | 2075 | 354 | split |
| Plain Craft Launcher 2/Modules/ModMain.cs | 1198 | 127 | split |
| Plain Craft Launcher 2/Pages/PageDownload/Comp/PageDownloadCompDetail.xaml.cs | 1185 | 71 | split |
| Plain Craft Launcher 2/Pages/PageDownload/ModDownloadLib.cs | 4606 | 68 | split |
| Plain Craft Launcher 2/Pages/PageDownload/PageDownloadInstall.xaml.cs | 2372 | 397 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceCompResource.xaml.cs | 3120 | 229 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceExport.xaml.cs | 1315 | 294 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceInstall.xaml.cs | 2602 | 452 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceOverall.xaml.cs | 1100 | 57 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceSaves/PageInstanceSavesDatapack.xaml.cs | 1696 | 140 | split |
| Plain Craft Launcher 2/Pages/PageInstance/PageInstanceSetup.xaml.cs | 1103 | 84 | split |
| Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchLeft.xaml.cs | 1038 | 76 | split |
| Plain Craft Launcher 2/Pages/PageLunar/PageLunarLaunch.xaml.cs | 1240 | 73 | split |
| Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml.cs | 1401 | 61 | split |

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

## Batch 2: resource catalogs and resource pages

`ModComp` now separates favorites, clipboard handling, the catalog database, project queries, project metadata and resource downloads. `ModLocalComp.LocalCompFile` separates each schematic format, metadata lookup, Jar-in-Jar state and file loading without duplicating model state. Resource and datapack pages separate folder navigation, selection, sorting/filtering, presentation and file actions. `ResourceProjectRequestService` owns platform project fetches; `ResourceFolderNames` owns resource-directory naming. Resource detail page setup and result presentation are split from its original entry.

## Batch 3: launch, instance state and foundation

Launch methods are organized into precheck/coordinator, authentication families, Java selection, JVM/game arguments, natives and process lifecycle files. Instance selection and settings separate Java configuration and presentation. The base module separates value types, numeric algorithms, filesystem/INI operations, text, search, OS interop, UI dispatch and diagnostics. Loader/animation definitions retain their original owning types and initialization order. `MessageNoticeCore` now owns dialog execution and `NumericUtilities` owns numeric algorithms; the former names forward to these implementations. TestAll 0.1.1 adds a read-only module/notice-entry probe to the UI and one-click suite.

## Batch 4: window, platform and runtime responsibilities

Window input/startup, Win32 messages, navigation, drag/drop and update presentation have distinct files. Lunar configuration/accounts, social session and game-process handling are separated; backend request encoding, version negotiation and message decoding are separated, with decoding implementation owned by `LunarMessageCodec`. Plugin installation/market/lifecycle, package preparation/recovery and host services have distinct files. Mixin application/validation is separated from plan construction/rollback. Settings binding is separated from themes/homepage composition; list-item input, layout and selection animation are separated. GDK/XVC containers, decoding, keys and binary-format declarations now have separate files with unchanged names and layouts.

All 30 original large sources and every new C# source pass the 1000-line gate. Mechanical moves preserve type identity and state; they improve source boundaries but intentionally do not redesign every shared static dependency. Independent services are explicitly identified above, so partial-file organization is not presented as full architectural isolation.

## Verification

`tools/SourceStructure` compares C# syntax tokens with the fixed Git baseline. It checks existing members, attributes, bases, constraints and initializer order. Service moves must be explicitly listed in `relocations.json`: the old member must be the expected forwarding expression and the new member must preserve the original implementation. CI runs this in DEBUG, CI, RELEASE and BETA configurations, then builds the TestAll plugin and runs the existing unit tests. The existing build matrix covers x64 and ARM64.

The tool emits native Codex patches for reviewed member ranges; it never rewrites source files itself. Its report lists all 30 original large files and their current sizes. CI now requires every target and every added source to be at most 1000 lines. Syntax diagnostics also reject dangling preprocessor regions; the final GDK split exposed this case and it has been corrected.

TestAll's new read-only refactor probe also invokes numeric boundary cases and checks the Mod resource folder mapping. It does not display notices or start games as part of that probe.

One separately authorized UI change is tracked explicitly: welcome page composition no longer adds the selected-instance card or recent-world card. Other UI behavior is unchanged.

Passing syntax/contract checks and CI is not proof of full GUI or real Minecraft process equivalence. Manual regression remains necessary for download cancellation, game launching, authentication, resource management, plugins/Mixin, Bedrock/CIK and Lunar interactions.
