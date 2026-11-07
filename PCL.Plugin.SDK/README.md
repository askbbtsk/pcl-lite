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
dotnet run --project PCL.Plugin.Tools -- pack .\bin\Release\net10.0-windows .\plugin-signing.pfx .\plugin.pclplugin
```

The tool prompts for the PFX password without echoing it. Keep the PFX and password private and backed up. A certificate can be used to sign packages, but a self-signed certificate does not establish the author's real-world identity.

The package signature covers a normalized manifest and the SHA-256 hash of every payload file. The signed time is supplied by the signer, so it is not a trusted timestamp. When an install is linked to a GitHub Release asset, PCL Lite independently verifies the current asset's SHA-256 against GitHub's release record and shows its publication time. That check corroborates the current artifact and release record; it does not authenticate the plugin author or prove the signature's exact signing time.

Unsigned packages require explicit confirmation. Invalid signatures, changed files, invalid manifests, and unsafe archive paths are rejected. A plugin can access the same user resources available to the launcher, so install only plugins you trust.
