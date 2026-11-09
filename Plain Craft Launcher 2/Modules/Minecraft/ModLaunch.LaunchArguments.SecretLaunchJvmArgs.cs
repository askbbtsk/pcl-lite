using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Backups;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Minecraft.PersonalOptions;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Launch.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;
using PCL.Network;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using System.Globalization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

public static partial class ModLaunch
{



    internal static void SecretLaunchJvmArgs(ref List<string> dataList)
    {
        var dataJvmCustom = Config.Instance.JvmArgs[ModInstanceList.McMcInstanceSelected?.PathInstance];
        dataList.Insert(0,
            string.IsNullOrEmpty(dataJvmCustom)
                ? Config.Launch.JvmArgs
                : dataJvmCustom); // 可变 JVM 参数
        switch (Config.Launch.PreferredIpStack)
        {
            case JvmPreferredIpStack.PreferV4:
            {
                dataList.Add("-Djava.net.preferIPv4Stack=true");
                dataList.Add("-Djava.net.preferIPv4Addresses=true");
                break;
            }
            case JvmPreferredIpStack.PreferV6:
            {
                dataList.Add("-Djava.net.preferIPv6Stack=true");
                dataList.Add("-Djava.net.preferIPv6Addresses=true");
                break;
            }
        }

        double availableGb = KernelInterop.GetAvailablePhysicalMemoryBytes() / 1073741824.0;
        ModLaunch.McLaunchLog($"当前剩余内存：{availableGb.ToString("N1", CultureInfo.InvariantCulture)}G");
        double totalRamMb = PageInstanceSetup.GetRam(ModInstanceList.McMcInstanceSelected) * 1024d;
        var maxHeapArg = Math.Floor(totalRamMb).ToString(CultureInfo.InvariantCulture);
        dataList.Add("-Xmn" + Math.Floor(totalRamMb * 0.15).ToString(CultureInfo.InvariantCulture) + "m");
        dataList.Add("-Xmx" + maxHeapArg + "m");
        // 若 dataList 中已存在 -Xms（例如用户自定义参数已设）则跳过，避免重复/冲突。
        if (!dataList.Any(d => d.Contains("-Xms", StringComparison.OrdinalIgnoreCase)))
        {
            if (Config.Launch.LockMemory)
            {
                // #3282: 固定堆大小时追加 -Xms 使其等于 -Xmx（复用同一数值以保持一致），隐式禁用内存归还降低延迟抖动、利于 ZGC。
                dataList.Add("-Xms" + maxHeapArg + "m");
            }
            else
            {
                // #3511: 支持自定义初始堆大小（-Xms），可低于最大堆。
                var initialRam = PageInstanceSetup.GetInitialRam(ModInstanceList.McMcInstanceSelected);
                if (initialRam.HasValue)
                    dataList.Add("-Xms" + Math.Floor(initialRam.Value * 1024d).ToString(CultureInfo.InvariantCulture) + "m");
            }
        }
        if (!dataList.Any(d => d.Contains("-Dlog4j2.formatMsgNoLookups=true")))
            dataList.Add("-Dlog4j2.formatMsgNoLookups=true");
    }


    public class LaunchArgument
    {
        private readonly List<string> _features = new();

        public LaunchArgument(McInstance minecraft)
        {
            var curArgu = string.Empty;
            if (minecraft.IsOldJson)
                _features = minecraft.JsonObject["minecraftArguments"].ToString().Split(' ').ToList();
            else
                foreach (var item in minecraft.JsonObject["arguments"]["game"].AsArray())
                    if (item.GetValueKind() == JsonValueKind.String)
                        _features.Add(item.ToString());
                    else if (item.GetValueKind() == JsonValueKind.Object)
                    {
                        var valueNode = item["value"];
                        if (valueNode.GetValueKind() == JsonValueKind.Array)
                            _features.AddRange(valueNode.AsArray().Select(x => x.ToString()));
                        else if (valueNode.GetValueKind() == JsonValueKind.String)
                            _features.Add(valueNode.ToString());
                    }
        }

        public object HasArguments(string key)
        {
            return _features.Contains(key);
        }
    }


    /// <summary>
    ///     释放 Java Wrapper 并返回完整文件路径。
    /// </summary>
    public static string ExtractJavaWrapper()
    {
        var wrapperPath = Path.Combine(ModBase.pathPure, "JavaWrapper.jar");
        ModBase.Log("[Java] 选定的 Java Wrapper 路径：" + wrapperPath);
        lock (extractJavaWrapperLock) // 避免 OptiFine 和 Forge 安装时同时释放 Java Wrapper 导致冲突
        {
            try
            {
                WriteJavaWrapper(wrapperPath);
            }
            catch (Exception ex)
            {
                if (File.Exists(wrapperPath))
                {
                    // 因为未知原因 Java Wrapper 可能变为只读文件（#4243）
                    ModBase.Log(ex, "Java Wrapper 文件释放失败，但文件已存在，将在删除后尝试重新生成", ModBase.LogLevel.Developer);
                    try
                    {
                        File.Delete(wrapperPath);
                        WriteJavaWrapper(wrapperPath);
                    }
                    catch (Exception ex2)
                    {
                        ModBase.Log(ex2, "Java Wrapper 文件重新释放失败，将尝试更换文件名重新生成", ModBase.LogLevel.Developer);
                        wrapperPath = Path.Combine(ModBase.pathPure, "JavaWrapper2.jar");
                        try
                        {
                            WriteJavaWrapper(wrapperPath);
                        }
                        catch (Exception ex3)
                        {
                            throw new FileNotFoundException("释放 Java Wrapper 最终尝试失败", ex3);
                        }
                    }
                }
                else
                {
                    throw new FileNotFoundException("释放 Java Wrapper 失败", ex);
                }
            }
        }

        return wrapperPath;
    }


    private static void WriteJavaWrapper(string path)
    {
        ModBase.WriteFile(path, ModBase.GetResourceStream("Resources/java-wrapper.jar"));
    }


    /// <summary>
    ///     释放 linkd 并返回完整文件路径。
    /// </summary>
    public static string ExtractLinkD()
    {
        var linkDPath = Path.Combine(ModBase.pathPure, "linkd.exe");
        lock (extractLinkDLock) // 避免 OptiFine 和 Forge 安装时同时释放 Java Wrapper 导致冲突
        {
            try
            {
                WriteLinkD(linkDPath);
            }
            catch (Exception ex)
            {
                if (File.Exists(linkDPath))
                {
                    ModBase.Log(ex, "linkd 文件释放失败，但文件已存在，将在删除后尝试重新生成", ModBase.LogLevel.Developer);
                    try
                    {
                        File.Delete(linkDPath);
                        WriteLinkD(linkDPath);
                    }
                    catch (Exception ex2)
                    {
                        throw new FileNotFoundException("释放 linkd 失败", ex2);
                    }
                }
                else
                {
                    throw new FileNotFoundException("释放 linkd 失败", ex);
                }
            }
        }

        return linkDPath;
    }


    private static void WriteLinkD(string path)
    {
        ModBase.WriteFile(path, ModBase.GetResourceStream("Resources/linkd.exe"));
    }


    /// <summary>
    /// 判断是否使用 LegacyFix。
    /// </summary>
    private static bool McLaunchNeedsLegacyFix(McInstance mc)
    {
        if (Config.Launch.DisableLF || Config.Instance.DisableLF[mc.PathInstance])
        {
            ModBase.Log("[Launch] LegacyFix 已被禁用");
            return false;
        }
        if (mc.releaseTime < new DateTime(2013, 6, 25) && mc.releaseTime.Year > 2000)
        {
            return true;
        }
        return false;
    }


    /// <summary>
    /// 获取实例所依赖的 LWJGL 版本
    /// </summary>
    private static string McLaunchGetLwjglVersion(McInstance mc)
    {
        foreach (ModLibrary.McLibToken library in ModLibrary.McLibListGet(mc, false))
        {
            if (string.IsNullOrWhiteSpace(library.OriginalName))
                continue;

            string[] parts = library.OriginalName.Split(':');
            if (parts.Length >= 3 &&
                parts[0].Equals("org.lwjgl", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("lwjgl", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }
        }

        return null;
    }


    /// <summary>
    /// 判断是否启用了针对 Minecraft 26.1 的性能问题补丁
    /// </summary>
    private static bool McLaunchUsesLwjglUnsafeAgent(McInstance mc)
    {
        if (McLaunchGetLwjglVersion(mc) == "3.4.1")
        {
            bool globalDisabled = Config.Launch.DisableLwjglUnsafeAgent;
            bool instanceDisabled = Config.Instance.DisableLwjglUnsafeAgent[mc.PathInstance];

            return !globalDisabled && !instanceDisabled;
        }
        else
        {
            return false;
        }
    }


    // 主方法，合并 Jvm、Game、Replace 三部分的参数数据
    private static void McLaunchArgumentMain(ModLoader.LoaderTask<string, List<ModLibrary.McLibToken>> loader)
    {
        McLaunchLog("开始获取 Minecraft 启动参数");
        // 获取基准字符串与参数信息
        string arguments;
        if (ModInstanceList.McMcInstanceSelected.JsonObject["arguments"] is not null &&
            ModInstanceList.McMcInstanceSelected.JsonObject["arguments"]["jvm"] is not null)
        {
            McLaunchLog("获取新版 JVM 参数");
            arguments = McLaunchArgumentsJvmNew(ModInstanceList.McMcInstanceSelected);
            McLaunchLog("新版 JVM 参数获取成功：");
            McLaunchLog(arguments);
        }
        else
        {
            McLaunchLog("获取旧版 JVM 参数");
            arguments = McLaunchArgumentsJvmOld(ModInstanceList.McMcInstanceSelected);
            McLaunchLog("旧版 JVM 参数获取成功：");
            McLaunchLog(arguments);
        }

        foreach (var arg in currentLaunchOptions.AdditionalJvmArguments)
            arguments += " " + arg.Trim();

        if (!string.IsNullOrEmpty(
                (string)ModInstanceList.McMcInstanceSelected.JsonObject["minecraftArguments"])) // 有的实例 JSON 中是空字符串
        {
            McLaunchLog("获取旧版 Game 参数");
            arguments += " " + McLaunchArgumentsGameOld(ModInstanceList.McMcInstanceSelected);
            McLaunchLog("旧版 Game 参数获取成功");
        }

        if (ModInstanceList.McMcInstanceSelected.JsonObject["arguments"] is not null &&
            ModInstanceList.McMcInstanceSelected.JsonObject["arguments"]["game"] is not null)
        {
            McLaunchLog("获取新版 Game 参数");
            arguments += " " + McLaunchArgumentsGameNew(ModInstanceList.McMcInstanceSelected);
            McLaunchLog("新版 Game 参数获取成功");
        }

        // 编码参数（#4700、#5892、#5909）
        if (mcLaunchJavaSelected.Installation.MajorVersion > 8)
        {
            if (!arguments.Contains("-Dstdout.encoding="))
                arguments = "-Dstdout.encoding=UTF-8 " + arguments;
            if (!arguments.Contains("-Dstderr.encoding="))
                arguments = "-Dstderr.encoding=UTF-8 " + arguments;
        }

        if (mcLaunchJavaSelected.Installation.MajorVersion >= 18)
            if (!arguments.Contains("-Dfile.encoding="))
                arguments = "-Dfile.encoding=COMPAT " + arguments;
        // MJSB
        arguments = arguments.Replace(" -Dos.name=Windows 10", " -Dos.name=\"Windows 10\"");
        // 全屏
        if (Config.Launch.GameWindowMode == 0)
            arguments += " --fullscreen";
        // 由 Option 传入的额外参数
        foreach (var arg in currentLaunchOptions.ExtraArgs)
            arguments += " " + arg.Trim();
        // 自定义参数
        var argumentGame = Config.Instance.GameArgs[ModInstanceList.McMcInstanceSelected?.PathInstance];
        arguments = arguments + " " + (string.IsNullOrEmpty(argumentGame) ? Config.Launch.GameArgs : argumentGame);
        // 替换参数
        var replaceArguments = McLaunchArgumentsReplace(ModInstanceList.McMcInstanceSelected, ref loader);
        if (string.IsNullOrWhiteSpace(replaceArguments["${version_type}"]))
        {
            // 若自定义信息为空，则去掉该部分
            arguments = arguments.Replace(" --versionType ${version_type}", "");
            replaceArguments["${version_type}"] = "\"\"";
        }

        var finalArguments = "";
        foreach (var argumentRaw in arguments.Split(" "))
        {
            var argument = argumentRaw;
            foreach (var entry in replaceArguments)
                argument = argument.Replace(entry.Key, entry.Value);
            if ((argument.Contains(" ") || argument.Contains(@":\")) && !argument.EndsWithF("\""))
                argument = $"\"{argument}\"";
            finalArguments += argument + " ";
        }

        finalArguments = finalArguments.TrimEnd();
        // 进存档
        var worldName = currentLaunchOptions.WorldName;
        if (worldName is not null) finalArguments += $" --quickPlaySingleplayer \"{worldName}\"";
        // 进服
        var server = string.IsNullOrEmpty(currentLaunchOptions.ServerIp)
            ? Config.Instance.ServerToEnter[ModInstanceList.McMcInstanceSelected?.PathInstance]
            : currentLaunchOptions.ServerIp;
        if (string.IsNullOrWhiteSpace(worldName) && !string.IsNullOrWhiteSpace(server))
        {
            if (ModInstanceList.McMcInstanceSelected.releaseTime > new DateTime(2023, 4, 4))
            {
                // QuickPlay
                finalArguments += $" --quickPlayMultiplayer \"{server}\"";
            }
            else
            {
                // 老版本
                if (server.Contains(":"))
                    // 包含端口号
                    finalArguments += " --server " + server.Split(":")[0] + " --port " + server.Split(":")[1];
                else
                    // 不包含端口号
                    finalArguments += " --server " + server + " --port 25565";
                if (ModInstanceList.McMcInstanceSelected.Info.HasOptiFine)
                    HintService.Hint(Lang.Text("Minecraft.Launch.Error.OptiFineAutoJoinWarning"), HintType.Error);
            }
        }

        // 输出
        McLaunchLog("Minecraft 启动参数：");
        McLaunchLog(finalArguments);
        mcLaunchArgument = finalArguments;
    }


    // Jvm 部分（第一段）
    private static string McLaunchArgumentsJvmOld(McInstance instance)
    {
        // 存储以空格为间隔的启动参数列表
        var dataList = new List<string>();

        // 输出固定参数
        dataList.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
        var argumentJvm = Config.Instance.JvmArgs[ModInstanceList.McMcInstanceSelected?.PathInstance];
        if (string.IsNullOrEmpty(argumentJvm))
            argumentJvm = Config.Launch.JvmArgs;
        if (!argumentJvm.Contains("-Dlog4j2.formatMsgNoLookups=true"))
            argumentJvm += " -Dlog4j2.formatMsgNoLookups=true";
        argumentJvm = argumentJvm.Replace(" -XX:MaxDirectMemorySize=256M", ""); // #3511 的清理
        dataList.Insert(0, argumentJvm); // 可变 JVM 参数
        dataList.Add("-Xmn" +
                     Math.Floor(PageInstanceSetup.GetRam(ModInstanceList.McMcInstanceSelected,
                         !mcLaunchJavaSelected.Installation.Is64Bit) * 1024d * 0.15d) + "m");
        var maxHeapArg = Math.Floor(PageInstanceSetup.GetRam(ModInstanceList.McMcInstanceSelected,
            !mcLaunchJavaSelected.Installation.Is64Bit) * 1024d);
        dataList.Add("-Xmx" + maxHeapArg + "m");
        // 若 dataList 中已存在 -Xms（例如用户自定义参数已设）则跳过，避免重复/冲突。
        if (!dataList.Any(d => d.Contains("-Xms", StringComparison.OrdinalIgnoreCase)))
        {
            if (Config.Launch.LockMemory)
            {
                // #3282: 固定堆大小时追加 -Xms 使其等于 -Xmx（复用同一数值以保持一致），隐式禁用内存归还降低延迟抖动、利于 ZGC。
                dataList.Add("-Xms" + maxHeapArg + "m");
            }
            else
            {
                // #3511: 支持自定义初始堆大小（-Xms），可低于最大堆。
                var initialRam = PageInstanceSetup.GetInitialRam(ModInstanceList.McMcInstanceSelected,
                    !mcLaunchJavaSelected.Installation.Is64Bit);
                if (initialRam.HasValue)
                    dataList.Add("-Xms" + Math.Floor(initialRam.Value * 1024d).ToString(CultureInfo.InvariantCulture) + "m");
            }
        }
        dataList.Add("\"-Djava.library.path=" + GetNativesFolder() + "\"");
        dataList.Add("-cp ${classpath}"); // 把支持库添加进启动参数表

        // Authlib-Injector
        if (mcLoginLoader.output.Type == "Auth")
        {
            if (mcLaunchJavaSelected.Installation.MajorVersion >= 6)
                dataList.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT"); // 信任系统根证书（Meloong-Git/#5252）
            var server = mcLoginAuthLoader.input.BaseUrl.Replace("/authserver", "");
            try
            {
                var response = Requester.FetchString(server);
                dataList.Insert(0,
                    "-javaagent:\"" + Path.Combine(ModBase.pathPure, "authlib-injector.jar") + "\"=" + server +
                    " -Dauthlibinjector.side=client" + " -Dauthlibinjector.yggdrasil.prefetched=" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(response)));
            }
            catch (WebException ex)
            {
                throw new Exception(
                    Lang.Text("Minecraft.Launch.Error.CannotConnectAuthServerWithDetail", server ?? null) + ex.InnerException, ex);
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Launch.Error.CannotConnectAuthServer", server ?? null), ex);
            }
        }

        if (Config.Instance.UseDebugLof4j2Config[instance.PathIndie])
        {
            if (ModInstanceList.McMcInstanceSelected.releaseTime.Year >= 2017)
                dataList.Insert(0, "-Dlog4j.configurationFile=\"" + LaunchEnvUtils.ExtractDebugLog4j2Config() + "\"");
            else
                dataList.Insert(0,
                    "-Dlog4j.configurationFile=\"" + LaunchEnvUtils.ExtractLegacyDebugLog4j2Config() + "\"");
        }

        // 渲染器
        var renderer = 0;
        var instanceRenderer = Config.Instance.Renderer[ModInstanceList.McMcInstanceSelected?.PathInstance];
        if (instanceRenderer != 0)
            renderer = instanceRenderer - 1;
        else
            renderer = Config.Launch.Renderer;
        var mesaLoaderWindowsTargetFile =
            Path.Combine(ModBase.pathPure, "mesa-loader-windows", mesaLoaderWindowsVersion, "Loader.jar");

        if (renderer != 0)
            dataList.Insert(0,
                "-javaagent:\"" + mesaLoaderWindowsTargetFile + "\"=" +
                (renderer == 1 ? "llvmpipe" : renderer == 2 ? "d3d12" : "zink"));

        // 设置代理
        if (Config.Instance.UseProxy[instance.PathIndie] && Config.Network.HttpProxy.Type.Equals(2) &&
            !string.IsNullOrWhiteSpace(Config.Network.HttpProxy.CustomAddress))
            try
            {
                var proxyAddress = new Uri(Config.Network.HttpProxy.CustomAddress);
                dataList.Add(
                    $"-D{(proxyAddress.Scheme.StartsWithF("https:") ? "https" : "http")}.proxyHost={proxyAddress.AbsoluteUri}");
                dataList.Add(
                    $"-D{(proxyAddress.Scheme.StartsWithF("https:") ? "https" : "http")}.proxyPort={proxyAddress.Port}");
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Minecraft.Launch.Error.Proxy"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.Proxy"));
            }

        // 添加 LegacyFix 相关参数
        if (McLaunchNeedsLegacyFix(instance))
        {
            var legacyFixPath = Path.Combine(ModBase.pathPure, "legacyfix.jar");
            dataList.Add("-javaagent:\"" + legacyFixPath + "\"");

            // Beta 1.6 以前版本需要添加的参数
            if (instance.releaseTime < new DateTime(2011, 5, 25))
            {
                dataList.Add("-Djava.util.Arrays.useLegacyMergeSort=true");
            }
        }

        // 添加 Java Wrapper 作为主 Jar
        if (ModBase.IsUtf8CodePage() && !Config.Launch.DisableJlw &&
            !Config.Instance.DisableJlw[ModInstanceList.McMcInstanceSelected?.PathInstance])
        {
            if (mcLaunchJavaSelected.Installation.MajorVersion >= 9)
                dataList.Add("--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
            dataList.Add("-Doolloo.jlw.tmpdir=\"" + ModBase.pathPure.TrimEnd('\\') + "\"");
            dataList.Add("-jar \"" + ExtractJavaWrapper() + "\"");
        }

        // 添加 MainClass
        if (instance.JsonObject["mainClass"] is null) throw new Exception(Lang.Text("Minecraft.Launch.Error.MissingMainClass"));

        dataList.Add((string)instance.JsonObject["mainClass"]);

        return dataList.Join(" ");
    }


    private static string McLaunchArgumentsJvmNew(McInstance instance)
    {
        var dataList = new List<string>();

        // 获取 Json 中的 DataList
        var currentInstance = instance;
        while (true)
        {
            if (currentInstance.JsonObject["arguments"] is not null &&
                currentInstance.JsonObject["arguments"]["jvm"] is not null)
                foreach (var subJson in currentInstance.JsonObject["arguments"]["jvm"].AsArray())
                    if (subJson.GetValueKind() == JsonValueKind.String)
                    {
                        // 字符串类型
                        dataList.Add(subJson.ToString());
                    }
                    // 非字符串类型
                    else if (ModLibrary.McJsonRuleCheck(subJson["rules"]))
                    {
                        // 满足准则
                        if (subJson["value"].GetValueKind() == JsonValueKind.String)
                            dataList.Add(subJson["value"].ToString());
                        else
                            foreach (var value in subJson["value"].AsArray())
                                dataList.Add(value.ToString());
                    }

            if (string.IsNullOrEmpty(currentInstance.InheritInstanceName))
                break;

            currentInstance = new McInstance(currentInstance.InheritInstanceName);
        }

        // 内存、Log4j 防御参数等
        SecretLaunchJvmArgs(ref dataList);

        // Authlib-Injector
        if (mcLoginLoader.output.Type == "Auth")
        {
            if (mcLaunchJavaSelected.Installation.MajorVersion >= 6)
                dataList.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT"); // 信任系统根证书（Meloong-Git/#5252）
            var server = mcLoginAuthLoader.input.BaseUrl.Replace("/authserver", "");
            try
            {
                var response = ModNet.NetGetCodeByRequestRetry(server, Encoding.UTF8)?.ToString();
                dataList.Insert(0,
                    "-javaagent:\"" + Path.Combine(ModBase.pathPure, "authlib-injector.jar") + "\"=" + server +
                    " -Dauthlibinjector.side=client" + " -Dauthlibinjector.yggdrasil.prefetched=" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(response)));
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Launch.Error.CannotConnectAuthServer", server ?? null), ex);
            }
        }
        
        // LWJGL Unsafe Agent
        if (McLaunchUsesLwjglUnsafeAgent(ModInstanceList.McMcInstanceSelected))
        {
            ModBase.Log($"获取到的 LWJGL 版本：{McLaunchGetLwjglVersion(ModInstanceList.McMcInstanceSelected)}");
            dataList.Insert(0, $"-javaagent:\"{ModBase.pathPure}lwjgl-unsafe-agent.jar\"");
        }

        if (Config.Instance.UseDebugLof4j2Config[instance.PathIndie])
        {
            if (ModInstanceList.McMcInstanceSelected.releaseTime.Year >= 2017)
                dataList.Insert(0, "-Dlog4j.configurationFile=\"" + LaunchEnvUtils.ExtractDebugLog4j2Config() + "\"");
            else
                dataList.Insert(0,
                    "-Dlog4j.configurationFile=\"" + LaunchEnvUtils.ExtractLegacyDebugLog4j2Config() + "\"");
        }

        // 渲染器
        var renderer = 0;
        var instanceRenderer = Config.Instance.Renderer[ModInstanceList.McMcInstanceSelected?.PathInstance];
        if (instanceRenderer != 0)
            renderer = instanceRenderer - 1;
        else
            renderer = Config.Launch.Renderer;
        var mesaLoaderWindowsTargetFile =
            Path.Combine(ModBase.pathPure, "mesa-loader-windows", mesaLoaderWindowsVersion, "Loader.jar");

        if (renderer != 0)
            dataList.Insert(0,
                "-javaagent:\"" + mesaLoaderWindowsTargetFile + "\"=" +
                (renderer == 1 ? "llvmpipe" : renderer == 2 ? "d3d12" : "zink"));

        // 设置代理
        if (Config.Instance.UseProxy[instance.PathIndie] && Config.Network.HttpProxy.Type.Equals(2) &&
            !string.IsNullOrWhiteSpace(Config.Network.HttpProxy.CustomAddress))
            try
            {
                var proxyAddress = new Uri(Config.Network.HttpProxy.CustomAddress);
                dataList.Add(
                    $"-D{(proxyAddress.Scheme.StartsWithF("https:") ? "https" : "http")}.proxyHost={proxyAddress.AbsoluteUri}");
                dataList.Add(
                    $"-D{(proxyAddress.Scheme.StartsWithF("https:") ? "https" : "http")}.proxyPort={proxyAddress.Port}");
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Minecraft.Launch.Error.Proxy"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.Proxy"));
            }

        // 添加 Java Wrapper 作为主 Jar
        if (ModBase.IsUtf8CodePage() && !Config.Launch.DisableJlw &&
            !Config.Instance.DisableJlw[ModInstanceList.McMcInstanceSelected?.PathInstance])
        {
            if (mcLaunchJavaSelected.Installation.MajorVersion >= 9)
                dataList.Add("--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
            dataList.Add("-Doolloo.jlw.tmpdir=\"" + ModBase.pathPure.TrimEnd('\\') + "\"");
            dataList.Add("-jar \"" + ExtractJavaWrapper() + "\"");
        }


        // 将 "-XXX" 与后面 "XXX" 合并到一起
        // 如果不合并，会导致 Forge 1.17 启动无效，它有两个 --add-exports，进一步导致其中一个在后面被去重
        var deDuplicateDataList = new List<string>();
        for (int i = 0, loopTo = dataList.Count - 1; i <= loopTo; i++)
        {
            var currentEntry = dataList[i];
            if (dataList[i].StartsWithF("-"))
                while (i < dataList.Count - 1)
                {
                    if (dataList[i + 1].StartsWithF("-")) break;

                    i += 1;
                    currentEntry += " " + dataList[i];
                }

            deDuplicateDataList.Add(currentEntry.Trim().Replace("McEmu= ", "McEmu="));
        }

        // #3511 的清理
        deDuplicateDataList.Remove("-XX:MaxDirectMemorySize=256M");

        // 去重
        var result = deDuplicateDataList.Distinct().ToList().Join(" ");

        // 添加 MainClass
        if (instance.JsonObject["mainClass"] is null) throw new Exception(Lang.Text("Minecraft.Launch.Error.MissingMainClass"));

        result += " " + instance.JsonObject["mainClass"];

        return result;
    }

}
