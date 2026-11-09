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

using static PCL.ModDownload;

namespace PCL;

internal static class ResourceRequestService
{



    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理，返回字符串。
    ///     调用 NetGetCodeByRequest，会进行重试。
    /// </summary>
    public static string DlModRequest(string url) => DlModRequest<string>(url);



    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理，返回字符串或 JSON 对象。
    ///     调用 NetGetCodeByRequest，会进行重试。
    /// </summary>
    public static T DlModRequest<T>(string url)
    {
        var urls = new List<KeyValuePair<string, int>>();
        var mcimUrl = DlSourceModGet(url);
        if ((mcimUrl ?? "") != (url ?? ""))
            switch (Config.Download.Comp.CompSourceSolution)
            {
                case 0:
                {
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 5));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    break;
                }
                case 1:
                {
                    urls.Add(new KeyValuePair<string, int>(url, 5));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 5));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    break;
                }

                default:
                {
                    urls.Add(new KeyValuePair<string, int>(url, 5));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    break;
                }
            }

        var exs = "";
        foreach (var Source in urls)
            try
            {
                var json = Requester.FetchString(Source.Key, new RequestParam
                {
                    Timeout = Source.Value * 1000,
                    UseBrowserUserAgent = true
                });
                if (typeof(T) == typeof(string)) return (T)(object)json;
                return (T)(object)ModBase.GetJson(json);
            }
            catch (Exception ex)
            {
                // 镜像源可能随机爆炸，忽略就好
                if (!ex.Message.ContainsF("mcimirror")) exs += ex.Message + "\r\n";
            }

        throw new Exception(exs);
    }



    /// <summary>
    ///     非泛型版本的 DlModRequest，返回 string
    ///     对可能涉及 Mod 镜像源的请求进行处理。
    ///     调用 NetRequest，会进行重试。
    /// </summary>
    public static string DlModRequest(string url, string method, string data, string contentType,
        bool allowMirror = false) => DlModRequest<string>(url, method, data, contentType, allowMirror);


    
    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理。
    ///     调用 NetRequest，会进行重试。
    /// </summary>
    public static T DlModRequest<T>(string url, string method, string data, string contentType,
        bool allowMirror = false)
    {
        var urls = new List<KeyValuePair<string, int>>();
        var mcimUrl = DlSourceModGet(url);
        if ((mcimUrl ?? "") != (url ?? ""))
            switch (allowMirror ? Config.Download.Comp.CompSourceSolution : 2)
            {
                case 0:
                {
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 5));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    break;
                }
                case 1:
                {
                    urls.Add(new KeyValuePair<string, int>(url, 5));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 5));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    break;
                }

                default:
                {
                    urls.Add(new KeyValuePair<string, int>(url, 5));
                    urls.Add(new KeyValuePair<string, int>(url, 15));
                    urls.Add(new KeyValuePair<string, int>(mcimUrl, 10));
                    break;
                }
            }

        var exs = "";
        foreach (var Source in urls)
            try
            {
                string json = Requester.Fetch(Source.Key, new FetchParam
                {
                    Method = method,
                    Content = data, 
                    ContentType = contentType,
                    Timeout = Source.Value * 1000
                });
                if (typeof(T) == typeof(string)) return (T)(object)json; // 沟槽的，为什么不能写 T is string
                return (T)(object)ModBase.GetJson(json);
            }
            catch (Exception ex)
            {
                if (!ex.Message.ContainsF("mcimirror")) exs += ex.Message + "\r\n";
            }

        throw new Exception(exs);
    }



    /// <summary>
    ///     下载文件（而非获取版本列表）的时候，根据是否优先使用官方源决定使用 Url 的顺序。
    /// </summary>
    public static IEnumerable<string> DlSourceOrder(IEnumerable<string> officialUrls, IEnumerable<string> mirrorUrls)
    {
        return DownloadSource.Order(officialUrls, mirrorUrls);
    }



    /// <summary>
    ///     获取版本列表（而非下载文件）的时候，根据是否优先使用官方源决定使用 Url 的顺序。
    /// </summary>
    public static IEnumerable<string> DlVersionListOrder(IEnumerable<string> officialUrls,
        IEnumerable<string> mirrorUrls)
    {
        return DlVersionListPreferMojang ? officialUrls.Union(mirrorUrls) : mirrorUrls.Union(officialUrls);
    }




    /// <summary>
    ///     下载 Assets 文件。
    /// </summary>
    public static IEnumerable<string> DlSourceAssetsGet(string original) => DownloadSource.Assets(original);



    /// <summary>
    ///     下载 Libraries 文件。
    /// </summary>
    public static IEnumerable<string> DlSourceLibraryGet(string original) => DownloadSource.Libraries(original);



    /// <summary>
    ///     下载 Launcher 或 Meta 文件。
    ///     不应使用它来获取版本列表（因为它只使用文件下载源设置来决定源顺序）。
    /// </summary>
    public static IEnumerable<string> DlSourceLauncherOrMetaGet(string original)
    {
        if (original is null)
            throw new Exception(Lang.Text("Minecraft.Download.Error.NoJsonDownloadAddress"));
        return DownloadSource.LauncherOrMeta(original);
    }



    /// <summary>
    ///     Mod Api 镜像源
    /// </summary>
    /// <param name="original"></param>
    /// <returns></returns>
    public static string DlSourceModGet(string original)
    {
        return original.Replace("https://api.modrinth.com", "https://mod.mcimirror.top/modrinth")
            .Replace("https://api.curseforge.com", "https://mod.mcimirror.top/curseforge");
    }



    /// <summary>
    ///     Mod 下载镜像源
    /// </summary>
    /// <param name="original"></param>
    /// <returns></returns>
    public static List<string> DlSourceModDownloadGet(string original)
    {
        var res = new List<string>();
        var mirrorDl = original.Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top")
            .Replace("https://edge.forgecdn.net",
                "https://mod.mcimirror.top"); // like https://cdn.modrinth.com/data/P7dR8mSH/versions/X2hTodix/fabric-api-0.129.0%2B1.21.8.jar
        // like https://edge.forgecdn.net/files/6767/951/jei-1.21.5-neoforge-21.4.0.27.jar
        switch (Config.Download.Comp.CompSourceSolution)
        {
            case 0: // 镜像源
            {
                res.Add(mirrorDl);
                res.Add(mirrorDl);
                break;
            }
            case 1: // 平衡
            {
                res.Add(original);
                res.Add(mirrorDl);
                break;
            }
            case 2: // 官方源
            {
                res.Add(original);
                res.Add(original); // 错误
                break;
            }

            default:
            {
                Config.Download.Comp.CompSourceSolution = 1;
                res.Add(original);
                break;
            }
        }

        res.Add(original);
        return res;
    }



    // Loader 自动切换
    internal static void DlSourceLoader<InputType, OutputType>(ModLoader.LoaderTask<InputType, OutputType> mainLoader,
        List<KeyValuePair<ModLoader.LoaderTask<InputType, OutputType>, int>> loaderList, bool isForceRestart = false)
    {
        var waitCycle = 0;
        while (true)
        {
            // 检查状态
            var beforeLoadersAllFailed = true;
            foreach (var SubLoader in loaderList)
            {
                if (waitCycle == 0) // 判断是否可以不加载，直接使用已经加载好的结果
                {
                    if (isForceRestart)
                        continue; // 强制刷新，不行
                    if (SubLoader.Key.input is null ^ mainLoader.input is null || (SubLoader.Key.input is not null &&
                            !SubLoader.Key.input.Equals(mainLoader.input)))
                        continue; // 父子加载器的输入不一样，也不行
                }

                if (SubLoader.Key.State != ModBase.LoadState.Failed)
                    beforeLoadersAllFailed = false;
                if (SubLoader.Key.State == ModBase.LoadState.Finished)
                {
                    // 检查加载器成功
                    mainLoader.output = SubLoader.Key.output;
                    DlSourceLoaderAbort(loaderList);
                    return;
                }

                if (beforeLoadersAllFailed)
                    // 此前的加载器全部失败，直接启动后续加载器
                    if (waitCycle < SubLoader.Value * 100)
                        waitCycle = SubLoader.Value * 100;
            }

            // 第一轮时：既然不直接使用已经加载好的结果，那就启动第一个加载器
            if (waitCycle == 0)
            {
                loaderList.First().Key.Start(mainLoader.input, isForceRestart);
                foreach (var Loader in loaderList.Skip(1))
                    Loader.Key.State = ModBase.LoadState.Waiting; // 将其他源标记为未启动，以确保可以切换下载源（#184）
            }

            // 检查加载器失败或超时
            for (int i = 0, loopTo = loaderList.Count - 1; i <= loopTo; i++)
            {
                if (waitCycle != loaderList[i].Value * 100)
                    continue;
                if (i < loaderList.Count - 1 && !loaderList.All(l => l.Key.State == ModBase.LoadState.Failed))
                {
                    // 若还有下一个源，则启动下一个源
                    loaderList[i + 1].Key.Start(mainLoader.input, isForceRestart);
                }
                else
                {
                    // 若没有，则失败
                    Exception errorInfo = null;
                    for (int ii = 0, loopTo1 = loaderList.Count - 1; ii <= loopTo1; ii++)
                    {
                        loaderList[ii].Key.input = default; // 重置输入，以免以同样的输入“重试加载”时直接失败
                        if (loaderList[ii].Key.Error is null) continue;
                        if (errorInfo is null || loaderList[ii].Key.Error.Message
                                .Contains(Lang.Text("Minecraft.Download.Error.NotFound")))
                            errorInfo = loaderList[ii].Key.Error;
                    }

                    errorInfo ??= new TimeoutException(Lang.Text("Minecraft.Download.Error.Timeout"));
                    DlSourceLoaderAbort(loaderList);
                    throw errorInfo;
                }

                break;
            }

            // 计时
            Thread.Sleep(10);
            waitCycle += 1;
            // 检查父加载器中断
            if (mainLoader.IsAborted)
            {
                DlSourceLoaderAbort(loaderList);
                return;
            }
        }
    }



    internal static void DlSourceLoaderAbort<InputType, OutputType>(
        List<KeyValuePair<ModLoader.LoaderTask<InputType, OutputType>, int>> loaderList)
    {
        foreach (var Loader in loaderList)
            if (Loader.Key.State == ModBase.LoadState.Loading)
                Loader.Key.Abort();
    }

}
