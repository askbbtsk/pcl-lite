# PCL Lite Plugin SDK

The SDK targets .NET 10 for Windows and exposes the versioned `PCL.Plugin.SDK` API. Plugins run inside the launcher process with the same permissions as the launcher; package signatures do not sandbox plugins.

## Create a plugin

Reference `PCL.Plugin.SDK.csproj`, implement `IPclPlugin`, and add a `plugin.json` file to the build output. The manifest's `entryAssembly` and `entryType` identify the implementation. Set the optional `releaseAssetUrl` to the final GitHub Release asset URL when you want installation to show independent GitHub hash and publication-time corroboration.

```csharp
using PCL.Plugin.SDK;

public sealed class ExamplePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Example plugin started");
        context.Ui.RegisterPage("home", "Example", () => new System.Windows.Controls.TextBlock
        {
            Text = "Hello from a plugin"
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`IPluginContext` provides plugin metadata, logging, plugin-specific files and settings, UI notifications and pages, typed events, HTTP, launcher information, and Minecraft instance query/launch methods. `PluginGameLaunchingEvent`, `PluginGameStartedEvent`, and `PluginGameExitedEvent` report game lifecycle changes.

## Sign and package

Build `PCL.Plugin.Tools`, then create a private signing certificate and package the build output directory:

```powershell
dotnet run --project PCL.Plugin.Tools -- cert "CN=Your Plugin Name" .\plugin-signing.pfx
dotnet run --project PCL.Plugin.Tools -- pack .\staging .\plugin-signing.pfx .\plugin.pclplugin
```

The tool prompts for the PFX password without echoing it. Keep the PFX and password private and backed up. A certificate can be used to sign packages, but a self-signed certificate does not establish the author's real-world identity.

Create a clean Release staging directory containing only the files the plugin needs at runtime, then pass that directory to `pack`. The packer includes every regular file under the input directory (except an existing `signature.json`); it does not automatically exclude PDBs, XML documentation, test assemblies/data, or large assets. All included files are signed and count toward package limits.

The package signature covers a normalized manifest and the SHA-256 hash of every payload file. The signed time is supplied by the signer, so it is not a trusted timestamp. When an install is linked to a GitHub Release asset, PCL Lite independently verifies the current asset's SHA-256 against GitHub's release record and shows its publication time. That check corroborates the current artifact and release record; it does not authenticate the plugin author or prove the signature's exact signing time.

Unsigned packages require explicit confirmation. Invalid signatures, changed files, invalid manifests, and unsafe archive paths are rejected. A plugin can access the same user resources available to the launcher, so install only plugins you trust.

## Compiled Mixin patches

The SDK also defines C# Mixin attributes for compiled assemblies that patch launcher types, methods, and IL directly. This is executable code in the launcher's process, not a script or sandbox. The available operation set includes injection, overwrite, redirect, argument/constant/variable modification, accessors/invokers, shadows, unique members, slices, priorities, and match-count constraints. Do not assume complete semantic parity with PCL2-Nex; verify the current runtime behavior and tests for each operation and target launcher version.

Manifest `experimentalFeatures` group Mixin configuration files into separately selectable features. Each group defaults off and only applies after the user enables it and restarts the launcher. If a group fails to load, the host rolls back that group's already-applied configurations, records the error, and persists that group as disabled while allowing the plugin entrypoint and other groups to continue. This rollback is not process isolation and cannot undo arbitrary code that has already run.
