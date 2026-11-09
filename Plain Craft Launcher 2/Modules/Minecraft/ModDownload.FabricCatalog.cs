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



    public struct DlFabricListResult
    {
        /// <summary>
        ///     数据来源名称，如“Official”，“BMCLAPI”。
        /// </summary>
        public string sourceName;

        /// <summary>
        ///     是否为官方的实时数据。
        /// </summary>
        public bool isOfficial;

        /// <summary>
        ///     获取到的数据。
        /// </summary>
        public JsonObject Value;
    }


    private static void DlFabricListMain(ModLoader.LoaderTask<int, DlFabricListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlFabricListResult>, int>>
                        { new(dlFabricListBmclapiLoader, 30), new(dlFabricListOfficialLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlFabricListResult>, int>>
                        { new(dlFabricListOfficialLoader, 5), new(dlFabricListBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlFabricListResult>, int>>
                        { new(dlFabricListOfficialLoader, 60), new(dlFabricListBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlFabricListOfficialMain(ModLoader.LoaderTask<int, DlFabricListResult> loader)
    {
        var result = (JsonObject)Requester.FetchJson("https://meta.fabricmc.net/v2/versions");
        try
        {
            var output = new DlFabricListResult
                { isOfficial = true, sourceName = Lang.Text("Download.Source.FabricOfficial"), Value = result };
            if (output.Value["game"] is null || output.Value["loader"] is null || output.Value["installer"] is null)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Fabric", result));
            loader.output = output;
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Fabric", result), ex);
        }
    }


    private static void DlFabricListBmclapiMain(ModLoader.LoaderTask<int, DlFabricListResult> loader)
    {
        var result = (JsonObject)Requester.FetchJson("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions");
        try
        {
            var output = new DlFabricListResult { isOfficial = false, sourceName = "BMCLAPI", Value = result };
            if (output.Value["game"] is null || output.Value["loader"] is null || output.Value["installer"] is null)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Fabric BMCLAPI",
                    result));
            loader.output = output;
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Fabric BMCLAPI", result), ex);
        }
    }

}
