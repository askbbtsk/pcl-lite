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



    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理，返回字符串。
    ///     调用 NetGetCodeByRequest，会进行重试。
    /// </summary>
    public static string DlModRequest(string url) =>ResourceRequestService.DlModRequest(url);


    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理，返回字符串或 JSON 对象。
    ///     调用 NetGetCodeByRequest，会进行重试。
    /// </summary>
    public static T DlModRequest<T>(string url)
=>ResourceRequestService.DlModRequest<T>(url);


    /// <summary>
    ///     非泛型版本的 DlModRequest，返回 string
    ///     对可能涉及 Mod 镜像源的请求进行处理。
    ///     调用 NetRequest，会进行重试。
    /// </summary>
    public static string DlModRequest(string url, string method, string data, string contentType,
        bool allowMirror = false) =>ResourceRequestService.DlModRequest(url, method, data, contentType, allowMirror);

    
    /// <summary>
    ///     对可能涉及 Mod 镜像源的请求进行处理。
    ///     调用 NetRequest，会进行重试。
    /// </summary>
    public static T DlModRequest<T>(string url, string method, string data, string contentType,
        bool allowMirror = false)
=>ResourceRequestService.DlModRequest<T>(url, method, data, contentType, allowMirror);




    /// <summary>
    ///     下载文件（而非获取版本列表）的时候，是否优先使用官方源。
    ///     <para>
    ///         实际判定在 <see cref="DownloadSource.PreferMojang" />：这里只是转个手，
    ///         让「文件下载源」设置只有一个实现，Lunar 那些在 <c>PCL.Core</c> 里的
    ///         下载也能跟着同一份偏好走。
    ///     </para>
    /// </summary>
    public static bool DlSourcePreferMojang => DownloadSource.PreferMojang;


    /// <summary>
    ///     下载文件（而非获取版本列表）的时候，根据是否优先使用官方源决定使用 Url 的顺序。
    /// </summary>
    public static IEnumerable<string> DlSourceOrder(IEnumerable<string> officialUrls, IEnumerable<string> mirrorUrls)
=>ResourceRequestService.DlSourceOrder(officialUrls, mirrorUrls);


    /// <summary>
    ///     获取版本列表（而非下载文件）的时候，是否优先使用官方源。
    /// </summary>
    public static bool DlVersionListPreferMojang =>
        Config.Download.VersionListSource == 2 ||
        (Config.Download.VersionListSource == 1 && DownloadSource.OfficialSourceIsFast);


    /// <summary>
    ///     获取版本列表（而非下载文件）的时候，根据是否优先使用官方源决定使用 Url 的顺序。
    /// </summary>
    public static IEnumerable<string> DlVersionListOrder(IEnumerable<string> officialUrls,
        IEnumerable<string> mirrorUrls)
=>ResourceRequestService.DlVersionListOrder(officialUrls, mirrorUrls);



    /// <summary>
    ///     下载 Assets 文件。
    /// </summary>
    public static IEnumerable<string> DlSourceAssetsGet(string original) =>ResourceRequestService.DlSourceAssetsGet(original);


    /// <summary>
    ///     下载 Libraries 文件。
    /// </summary>
    public static IEnumerable<string> DlSourceLibraryGet(string original) =>ResourceRequestService.DlSourceLibraryGet(original);


    /// <summary>
    ///     下载 Launcher 或 Meta 文件。
    ///     不应使用它来获取版本列表（因为它只使用文件下载源设置来决定源顺序）。
    /// </summary>
    public static IEnumerable<string> DlSourceLauncherOrMetaGet(string original)
=>ResourceRequestService.DlSourceLauncherOrMetaGet(original);


    /// <summary>
    ///     Mod Api 镜像源
    /// </summary>
    /// <param name="original"></param>
    /// <returns></returns>
    public static string DlSourceModGet(string original)
=>ResourceRequestService.DlSourceModGet(original);


    /// <summary>
    ///     Mod 下载镜像源
    /// </summary>
    /// <param name="original"></param>
    /// <returns></returns>
    public static List<string> DlSourceModDownloadGet(string original)
=>ResourceRequestService.DlSourceModDownloadGet(original);


    // Loader 自动切换
    private static void DlSourceLoader<InputType, OutputType>(ModLoader.LoaderTask<InputType, OutputType> mainLoader,
        List<KeyValuePair<ModLoader.LoaderTask<InputType, OutputType>, int>> loaderList, bool isForceRestart = false)
=>ResourceRequestService.DlSourceLoader<InputType,OutputType>(mainLoader, loaderList, isForceRestart);


    private static void DlSourceLoaderAbort<InputType, OutputType>(
        List<KeyValuePair<ModLoader.LoaderTask<InputType, OutputType>, int>> loaderList)
=>ResourceRequestService.DlSourceLoaderAbort<InputType,OutputType>(loaderList);

}
