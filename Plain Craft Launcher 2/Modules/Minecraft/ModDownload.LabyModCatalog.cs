using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public static partial class ModDownload
{



    public struct DlLabyModListResult
    {
        /// <summary>
        ///     获取到的数据。
        /// </summary>
        public JsonObject Value;
    }


    private static void DlLabyModListMain(ModLoader.LoaderTask<int, DlLabyModListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLabyModListResult>, int>>
                        { new(dlLabyModListOfficialLoader, 30), new(dlLabyModListOfficialLoader, 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLabyModListResult>, int>>
                        { new(dlLabyModListOfficialLoader, 5), new(dlLabyModListOfficialLoader, 35) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLabyModListResult>, int>>
                        { new(dlLabyModListOfficialLoader, 60), new(dlLabyModListOfficialLoader, 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlLabyModListOfficialMain(ModLoader.LoaderTask<int, DlLabyModListResult> loader)
    {
        JsonObject resultProduction;
        using (var productionResponse = HttpRequest
                   .Create("https://releases.r2.labymod.net/api/v1/manifest/production/latest.json")
                   .WithHttpVersionOption(HttpVersion.Version20)
                   .SendAsync()
                   .GetAwaiter()
                   .GetResult())
        {
            resultProduction = (JsonObject)ModBase.GetJson(productionResponse.AsString());
        }

        JsonObject resultSnapshot;
        using (var snapshotResponse = HttpRequest
                   .Create("https://releases.r2.labymod.net/api/v1/manifest/snapshot/latest.json")
                   .WithHttpVersionOption(HttpVersion.Version20)
                   .SendAsync()
                   .GetAwaiter()
                   .GetResult())
        {
            snapshotResponse.EnsureSuccessStatusCode();
            resultSnapshot = (JsonObject)ModBase.GetJson(snapshotResponse.AsString());
        }

        var result = new JsonObject();
        result.Add("production", resultProduction);
        result.Add("snapshot", resultSnapshot);
        try
        {
            var output = new DlLabyModListResult { Value = result };
            if (output.Value["production"]["labyModVersion"] is null ||
                output.Value["snapshot"]["labyModVersion"] is null)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LabyMod",
                    result));
            loader.output = output;
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LabyMod", result),
                ex);
        }
    }

}
