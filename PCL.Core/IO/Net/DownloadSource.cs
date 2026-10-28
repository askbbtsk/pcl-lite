using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Core.App;

namespace PCL.Core.IO.Net;

/// <summary>
///     下载源的选择与镜像改写。
///     <para>
///         这里是「文件下载源」设置的<b>唯一</b>实现。以前这套逻辑只活在
///         <c>ModDownload</c> 里（<c>Plain Craft Launcher 2</c> 项目），
///         <c>PCL.Core</c> 里的东西（比如 Lunar 的物料下载）够不着，
///         结果就是各自硬编码一份 <c>bmclapi2</c> 地址，既不跟设置走，
///         改镜像域名时还要改好几处。
///     </para>
///     <para>
///         语义与设置项完全一致：
///         <list type="bullet">
///             <item><c>FileSource = 0</c>：优先镜像</item>
///             <item><c>FileSource = 1</c>：自动 —— 官方源探测够快才优先官方</item>
///             <item><c>FileSource = 2</c>：优先官方</item>
///         </list>
///     </para>
/// </summary>
public static class DownloadSource
{
    private const string Bmclapi = "https://bmclapi2.bangbang93.com";

    private const string UnlistedVersionsMirror = "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto";

    private const string UnlistedVersionsOfficial = "https://zkitefly.github.io/unlisted-versions-of-minecraft";

    /// <summary>
    ///     「自动」模式下官方源够不够快的探测结果。由下载版本列表那一侧写入
    ///     （实测官方源加载耗时 &lt; 4s 就算快）。
    /// </summary>
    public static bool OfficialSourceIsFast { get; set; }

    /// <summary>
    ///     下载文件时是否优先使用官方源。读配置，配置服务没起来时按镜像优先。
    /// </summary>
    public static bool PreferMojang
    {
        get
        {
            int mode;
            try
            {
                mode = Config.Download.FileSource;
            }
            catch (Exception)
            {
                // 单元测试 / 独立进程里 Config 的静态构造会失败，按镜像优先处理
                return false;
            }

            return mode == 2 || (mode == 1 && OfficialSourceIsFast);
        }
    }

    /// <summary>
    ///     按设置决定顺序：优先官方时官方在前，否则镜像在前。两边都去重。
    /// </summary>
    public static IEnumerable<string> Order(IEnumerable<string> officialUrls, IEnumerable<string> mirrorUrls) =>
        Order(PreferMojang, officialUrls, mirrorUrls);

    /// <summary>
    ///     排序的纯函数版本，方便单测（不去碰 <see cref="Config" />）。
    /// </summary>
    public static IEnumerable<string> Order(bool preferMojang, IEnumerable<string> officialUrls,
        IEnumerable<string> mirrorUrls) =>
        preferMojang ? officialUrls.Union(mirrorUrls) : mirrorUrls.Union(officialUrls);

    /// <summary>
    ///     原版资源（<c>resources.download.minecraft.net</c>）的候选地址。
    /// </summary>
    public static IReadOnlyList<string> Assets(string original)
    {
        original = original.Replace("http://resources.download.minecraft.net",
            "https://resources.download.minecraft.net");

        return Order([original], [ToMirror(original, "assets")]).ToList();
    }

    /// <summary>
    ///     原版库文件（<c>libraries.minecraft.net</c>）的候选地址。
    /// </summary>
    public static IReadOnlyList<string> Libraries(string original)
    {
        // Forge / Fabric / NeoForge 的构建产物官方源本来就在境外 CDN 上，
        // 加官方源只会白等一轮
        if (new[] { "minecraftforge", "fabricmc", "neoforged" }.Any(key => original.Contains(key)))
            return [ToMirror(original, "maven"), ToMirror(original, "libraries")];

        return Order([original], [ToMirror(original, "maven"), ToMirror(original, "libraries")]).ToList();
    }

    /// <summary>
    ///     启动器本体与版本元数据的候选地址。
    /// </summary>
    public static IReadOnlyList<string> LauncherOrMeta(string original)
    {
        var mirror = original
            .Replace("https://piston-data.mojang.com", Bmclapi)
            .Replace("https://piston-meta.mojang.com", Bmclapi)
            .Replace("https://launcher.mojang.com", Bmclapi)
            .Replace("https://launchermeta.mojang.com", Bmclapi)
            .Replace(UnlistedVersionsOfficial, UnlistedVersionsMirror);

        return Order([original], [mirror, original]).ToList();
    }

    /// <summary>
    ///     把 Mojang 系地址换成 BMCLAPI 的对应路径。
    /// </summary>
    /// <param name="original">原始地址。</param>
    /// <param name="mirrorPath">镜像下的子路径，<c>assets</c> / <c>maven</c> / <c>libraries</c>。</param>
    public static string ToMirror(string original, string mirrorPath) => original
        .Replace("https://piston-data.mojang.com", $"{Bmclapi}/{mirrorPath}")
        .Replace("https://piston-meta.mojang.com", $"{Bmclapi}/{mirrorPath}")
        .Replace("https://resources.download.minecraft.net", $"{Bmclapi}/{mirrorPath}")
        .Replace("https://libraries.minecraft.net", $"{Bmclapi}/{mirrorPath}")
        .Replace(UnlistedVersionsOfficial, UnlistedVersionsMirror);
}
