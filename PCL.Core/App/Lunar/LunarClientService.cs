using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Lunar.Social;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.App.Lunar;

/// <summary>
///     Lunar Client 启动物料里的一条附件。
/// </summary>
/// <param name="Name">文件名，落到目标目录时用这个。</param>
/// <param name="Sha1">服务端给的 SHA-1，用于校验与增量判断。</param>
/// <param name="Url">下载地址。</param>
/// <param name="Type"><c>CLASS_PATH</c> / <c>EXTERNAL_FILE</c> / <c>NATIVES</c>。</param>
/// <param name="Size">字节数，仅用于显示进度。</param>
public sealed record LunarArtifact(string Name, string Sha1, string Url, string Type, long Size)
{
    public bool IsClassPath => string.Equals(Type, "CLASS_PATH", StringComparison.OrdinalIgnoreCase);
    public bool IsNatives => string.Equals(Type, "NATIVES", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     某个子版本对应的原版资源索引。
///     <para>
///         它**不在** <c>/launcher/launch</c> 的响应里，而是在
///         <c>/launcher/metadata/versions/lunar</c> 的
///         <c>versions[].subversions[].assets</c> 中。启动器把它塞进
///         <c>--assetIndex</c>，Genesis 再转成原版的 <c>--assets_index_name</c>。
///     </para>
/// </summary>
/// <param name="Id">索引名，1.8.9 是 <c>1.8</c>。</param>
/// <param name="Url">Mojang 的索引文件地址。</param>
/// <param name="Sha1">索引文件的 SHA-1。</param>
public sealed record LunarAssetIndex(string Id, string Url, string Sha1);

/// <summary>
///     一份「索引 + 文件 CDN」形式的物料源。
///     <para>
///         官方启动器里 <c>textures</c> 与 <c>ui.assets</c> 都是这个形状：
///         先去 <paramref name="IndexUrl" /> 下一个纯文本索引（每行
///         <c>&lt;相对路径&gt; &lt;sha1&gt; [大小] [mtime]</c>），
///         再按 <paramref name="BaseUrl" /> + sha1 逐个取文件。
///     </para>
/// </summary>
/// <param name="IndexUrl">索引文件地址。</param>
/// <param name="IndexSha1">索引文件的 SHA-1，用来判断本地那份还能不能用。</param>
/// <param name="BaseUrl">文件 CDN 前缀，拼上 sha1 就是完整地址。</param>
public sealed record LunarIndexedSource(string IndexUrl, string IndexSha1, string BaseUrl);

/// <summary>
///     <c>licenses</c> 目录里的一份许可文件。
/// </summary>
/// <param name="File">相对 <c>~/.lunarclient/licenses/</c> 的路径。</param>
/// <param name="Sha1">内容 SHA-1。</param>
/// <param name="Url">下载地址。</param>
/// <param name="Size">字节数，仅用于进度显示。</param>
public sealed record LunarLicense(string File, string Sha1, string Url, long Size);

/// <summary>
///     <c>POST /launcher/launch</c> 的解析结果。
/// </summary>
public sealed class LunarLaunchData
{
    public required string MainClass { get; init; }
    public required IReadOnlyList<LunarArtifact> Artifacts { get; init; }

    /// <summary>防篡改/灰度令牌，原样透传给游戏。</summary>
    public string CanaryToken { get; init; } = string.Empty;

    /// <summary>
    ///     JRE 目录的校验和，同时也是 <c>~/.lunarclient/jre/</c> 下的子目录名。
    ///     有了它就不必自己决定该用哪个 Java。
    /// </summary>
    public string JreFolderChecksum { get; init; } = string.Empty;

    /// <summary>JRE 压缩包地址，本机没装过对应版本时用它补齐。</summary>
    public string JreDownloadUrl { get; init; } = string.Empty;

    /// <summary>JRE 压缩包的备用地址，官方 CDN 拉不动时用。</summary>
    public string JreDownloadFallbackUrl { get; init; } = string.Empty;

    /// <summary>压缩包内 <c>javaw.exe</c> 的相对路径，仅用于确认解压结果。</summary>
    public IReadOnlyList<string> JreExecutablePath { get; init; } = [];

    /// <summary>
    ///     Lunar 官方启动器额外传给 JVM 的参数。
    ///     <para>
    ///         1.21+ 与 26.x 必需：<c>--add-opens java.base/java.io=ALL-UNNAMED</c>、
    ///         <c>--enable-native-access=ALL-UNNAMED</c>、<c>--add-modules jdk.naming.dns</c>
    ///         这些缺了会在启动早期就报模块访问错误。1.8.9 也有几条
    ///         （<c>-Dlunar.webosr.url</c> 就是游戏内 UI 的入口），照抄最稳。
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> JvmExtraArguments { get; init; } = [];

    /// <summary>资源索引名，1.8.9 是 <c>1.8</c>。</summary>
    public string AssetIndex { get; init; } = string.Empty;

    /// <summary>资源索引的完整信息（含下载地址），拿不到时为 <c>null</c>。</summary>
    public LunarAssetIndex? AssetIndexInfo { get; init; }

    /// <summary>
    ///     界面资源的 SHA-1。Lunar 把界面资源放在 <c>ui/&lt;sha1&gt;/</c> 下，
    ///     <c>--uiDir</c> 必须指到那一层——只给 <c>ui/</c> 的话游戏内菜单整块不显示。
    /// </summary>
    public string UiSha1 { get; init; } = string.Empty;

    /// <summary>界面资源压缩包地址。</summary>
    public string UiUrl { get; init; } = string.Empty;

    /// <summary>
    ///     界面资源的索引式物料源（<c>ui.assets</c>）。
    ///     <para>
    ///         官方启动器解压完 <c>source.zip</c> 之后还会跑一遍这个索引下载，
    ///         把 HUD、模组菜单那些运行时才用到的资源补齐。只解 zip 也能进游戏，
    ///         但界面里会缺图标。
    ///     </para>
    /// </summary>
    public LunarIndexedSource? UiAssets { get; init; }

    /// <summary>
    ///     皮肤 / 披风 / 表情这些外观资源的索引式物料源。
    ///     落在 <c>~/.lunarclient/textures/</c>，缺了游戏内其他玩家是默认皮肤。
    /// </summary>
    public LunarIndexedSource? Textures { get; init; }

    /// <summary>许可文件清单，落在 <c>~/.lunarclient/licenses/</c>。</summary>
    public IReadOnlyList<LunarLicense> Licenses { get; init; } = [];

    public IReadOnlyList<LunarArtifact> ClassPathArtifacts => Artifacts.Where(x => x.IsClassPath).ToList();
    public IReadOnlyList<LunarArtifact> NativesArtifacts => Artifacts.Where(x => x.IsNatives).ToList();
    public IReadOnlyList<LunarArtifact> ExternalArtifacts =>
        Artifacts.Where(x => !x.IsClassPath && !x.IsNatives).ToList();
}

/// <summary>
///     拼接 Lunar Client 启动命令所需的全部外部输入。
/// </summary>
public sealed class LunarLaunchOptions
{
    /// <summary>Lunar 数据根目录，通常是 <c>%USERPROFILE%\.lunarclient</c>。</summary>
    public required string LunarRoot { get; init; }

    /// <summary>游戏实例目录（<c>--gameDir</c>）。</summary>
    public required string GameDirectory { get; init; }

    public required string VersionName { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerUuid { get; init; }

    /// <summary>Minecraft 的 accessToken，正版验证就靠它。</summary>
    public required string AccessToken { get; init; }

    public string XboxUid { get; init; } = string.Empty;
    public int AllocatedMemoryMb { get; init; } = 2048;
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public bool Fullscreen { get; init; }
    public string? Server { get; init; }
    /// <summary>
    ///     传给 Genesis 的 <c>--launcherVersion</c>。
    ///     <para>
    ///         <b>这是「游戏侧」的版本号，与上报给 Lunar 服务端的
    ///         <c>launcherVersion.semver</c> 是两回事</b>：后者校验下限、必须跟着官方
    ///         启动器往上涨（见 <see cref="LunarSocialProtocol.ResolveLauncherVersion" />）；
    ///         这个参数只被游戏侧用来打日志，PCL 从 1.6.x 起一直填 <c>3.3.0</c> 且
    ///         各版本启动均正常，没有理由跟着改。
    ///     </para>
    /// </summary>
    public string GameLauncherVersion { get; init; } = "3.3.0";

    public string InstallationId { get; init; } = string.Empty;

    /// <summary>
    ///     本地 IPC 端口（<c>--ipcPort</c>）。
    ///     <para>
    ///         0 表示不启用：游戏会跳过社交面板与实时设置同步，账号过期也不会来要续期。
    ///         非 0 时游戏会连 <c>ws://127.0.0.1:&lt;port&gt;</c>，
    ///         由 <see cref="LunarIpcServer" /> 或 Lunar 启动器应答。
    ///     </para>
    /// </summary>
    public int IpcPort { get; init; }

    /// <summary>
    ///     额外 JVM 参数，会被插在<b>所有</b>内置与官方参数之后。
    ///     <para>
    ///         JVM 对重复参数取最后一次，所以放最后才能让用户写的 <c>-Xmx</c>、
    ///         <c>-XX:+UseZGC</c> 盖掉前面那些默认值。
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
}

/// <summary>
///     Lunar Client 的物料获取与启动命令拼装。
///     <para>
///         这里刻意**不做**下载以外的任何决策：版本、模块、分支由调用方给定，
///         物料清单完全以服务端返回为准。协议细节见 <c>docs/LUNAR.md</c>。
///     </para>
/// </summary>
public static partial class LunarClientService
{
    private const string ApiBase = "https://api.lunarclientprod.com/launcher";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    ///     原版资源对象的 CDN。启动器里写死成 <c>uze</c> 这个常量。
    /// </summary>
    private const string MinecraftResourcesBase = "https://resources.download.minecraft.net";
}
