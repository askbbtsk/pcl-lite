using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

using static PCL.ModDownloadLib;

namespace PCL;

internal static class VersionJsonMerger
{

    /// <summary>
    ///     将多个实例 JSON 进行合并，如果目标已存在则直接覆盖。失败会抛出异常。
    /// </summary>
    internal static void MergeJson(string outputFolder, string minecraftFolder, string optiFineFolder = null,
        bool optiFineAsMod = false, string forgeFolder = null, string forgeVersion = null, string neoForgeFolder = null,
        string neoForgeVersion = null, string cleanroomFolder = null, string cleanroomVersion = null,
        string fabricFolder = null, string labyModFolder = null,
        string labyModChannel = null, string liteLoaderFolder = null, ModModpack.MMCPackInfo mMCPackInfo = null,
        string legacyFabricFolder = null)
    {
        ModBase.Log("[Download] 开始进行实例合并，输出：" + outputFolder + "，Minecraft：" + minecraftFolder +
                    (optiFineFolder is not null ? "，OptiFine：" + optiFineFolder : "") +
                    (forgeFolder is not null ? "，Forge：" + forgeFolder : "") +
                    (neoForgeFolder is not null ? "，NeoForge：" + neoForgeFolder : "") +
                    (cleanroomFolder is not null ? "，Cleanroom：" + cleanroomFolder : "") +
                    (liteLoaderFolder is not null ? "，LiteLoader：" + liteLoaderFolder : "") +
                    (fabricFolder is not null ? "，Fabric：" + fabricFolder : "") +
                    (legacyFabricFolder is not null ? "，LegacyFabric：" + legacyFabricFolder : "") +
                    (labyModFolder is not null ? "，LabyMod：" + labyModFolder : ""));
        Directory.CreateDirectory(outputFolder);

        var hasOptiFine = optiFineFolder is not null && !optiFineAsMod;
        var hasForge = forgeFolder is not null;
        var hasLegacyFabric = legacyFabricFolder is not null;
        var hasNeoForge = neoForgeFolder is not null;
        var hasCleanroom = cleanroomFolder is not null;
        var hasLiteLoader = liteLoaderFolder is not null;
        var hasFabric = fabricFolder is not null;
        var hasLabyMod = labyModFolder is not null;
        string outputName;
        string minecraftName;
        string optiFineName;
        string forgeName;
        string neoForgeName;
        string cleanroomName;
        string liteLoaderName;
        string fabricName;
        string legacyFabricName;
        string labyModName;
        string outputJsonPath;
        string minecraftJsonPath;
        string optiFineJsonPath = null;
        string forgeJsonPath = null;
        string neoForgeJsonPath = null;
        string cleanroomJsonPath = null;
        string liteLoaderJsonPath = null;
        string fabricJsonPath = null;
        string labyModJsonPath = null;
        string legacyFabricJsonPath = null;
        string outputJar;
        string minecraftJar;


        if (!outputFolder.EndsWithF(@"\"))
            outputFolder += @"\";
        outputName = ModBase.GetFolderNameFromPath(outputFolder);
        outputJsonPath = Path.Combine(outputFolder, outputName + ".json");
        outputJar = Path.Combine(outputFolder, outputName + ".jar");

        if (!minecraftFolder.EndsWithF(@"\"))
            minecraftFolder += @"\";
        minecraftName = ModBase.GetFolderNameFromPath(minecraftFolder);
        minecraftJsonPath = Path.Combine(minecraftFolder, minecraftName + ".json");
        minecraftJar = Path.Combine(minecraftFolder, minecraftName + ".jar");

        if (hasOptiFine)
        {
            if (!optiFineFolder.EndsWithF(@"\"))
                optiFineFolder += @"\";
            optiFineName = ModBase.GetFolderNameFromPath(optiFineFolder);
            optiFineJsonPath = Path.Combine(optiFineFolder, optiFineName + ".json");
        }

        if (hasForge)
        {
            if (!forgeFolder.EndsWithF(@"\"))
                forgeFolder += @"\";
            forgeName = ModBase.GetFolderNameFromPath(forgeFolder);
            forgeJsonPath = Path.Combine(forgeFolder, forgeName + ".json");
        }

        if (hasNeoForge)
        {
            if (!neoForgeFolder.EndsWithF(@"\"))
                neoForgeFolder += @"\";
            neoForgeName = ModBase.GetFolderNameFromPath(neoForgeFolder);
            neoForgeJsonPath = Path.Combine(neoForgeFolder, neoForgeName + ".json");
        }

        if (hasCleanroom)
        {
            if (!cleanroomFolder.EndsWithF(@"\"))
                cleanroomFolder += @"\";
            cleanroomName = ModBase.GetFolderNameFromPath(cleanroomFolder);
            cleanroomJsonPath = Path.Combine(cleanroomFolder, cleanroomName + ".json");
        }

        if (hasLiteLoader)
        {
            if (!liteLoaderFolder.EndsWithF(@"\"))
                liteLoaderFolder += @"\";
            liteLoaderName = ModBase.GetFolderNameFromPath(liteLoaderFolder);
            liteLoaderJsonPath = Path.Combine(liteLoaderFolder, liteLoaderName + ".json");
        }

        if (hasFabric)
        {
            if (!fabricFolder.EndsWithF(@"\"))
                fabricFolder += @"\";
            fabricName = ModBase.GetFolderNameFromPath(fabricFolder);
            fabricJsonPath = Path.Combine(fabricFolder, fabricName + ".json");
        }

        if (hasLegacyFabric)
        {
            if (!legacyFabricFolder.EndsWithF(@"\"))
                legacyFabricFolder += @"\";
            legacyFabricName = ModBase.GetFolderNameFromPath(legacyFabricFolder);
            legacyFabricJsonPath = Path.Combine(legacyFabricFolder, legacyFabricName + ".json");
        }

        if (hasLabyMod)
        {
            if (!labyModFolder.EndsWithF(@"\"))
                labyModFolder += @"\";
            labyModName = ModBase.GetFolderNameFromPath(labyModFolder);
            labyModJsonPath = Path.Combine(labyModFolder, labyModName + ".json");
        }


        JsonObject outputJson;
        JsonObject minecraftJson = null;
        JsonObject optiFineJson = null;
        JsonObject forgeJson = null;
        JsonObject neoForgeJson = null;
        JsonObject legacyFabricJson = null;
        JsonObject cleanroomJson = null;
        JsonObject liteLoaderJson = null;
        JsonObject fabricJson = null;
        JsonObject labyModJson = null;


        var minecraftJsonText = ModBase.ReadFile(minecraftJsonPath);
        if (!hasLabyMod)
        {
            if (!minecraftJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "Minecraft", minecraftJsonPath,
                    minecraftJsonText.Substring(0, Math.Min(minecraftJsonText.Length, 1000))));
            minecraftJson = (JsonObject)ModBase.GetJson(minecraftJsonText);
        }

        if (hasOptiFine)
        {
            var optiFineJsonText = ModBase.ReadFile(optiFineJsonPath);
            if (!optiFineJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "OptiFine", optiFineJsonPath,
                    optiFineJsonText.Substring(0, Math.Min(optiFineJsonText.Length, 1000))));
            optiFineJson = (JsonObject)ModBase.GetJson(optiFineJsonText);
        }

        if (hasForge)
        {
            var forgeJsonText = ModBase.ReadFile(forgeJsonPath);
            if (!forgeJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "Forge", forgeJsonPath,
                    forgeJsonText.Substring(0, Math.Min(forgeJsonText.Length, 1000))));
            forgeJson = (JsonObject)ModBase.GetJson(forgeJsonText);
        }

        if (hasNeoForge)
        {
            var neoForgeJsonText = ModBase.ReadFile(neoForgeJsonPath);
            if (!neoForgeJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "NeoForge", neoForgeJsonPath,
                    neoForgeJsonText.Substring(0, Math.Min(neoForgeJsonText.Length, 1000))));
            neoForgeJson = (JsonObject)ModBase.GetJson(neoForgeJsonText);
        }

        if (hasCleanroom)
        {
            var cleanroomJsonText = ModBase.ReadFile(cleanroomJsonPath);
            if (!cleanroomJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "Cleanroom", cleanroomJsonPath,
                    cleanroomJsonText.Substring(0, Math.Min(cleanroomJsonText.Length, 1000))));
            cleanroomJson = (JsonObject)ModBase.GetJson(cleanroomJsonText);
        }

        if (hasLiteLoader)
        {
            var liteLoaderJsonText = ModBase.ReadFile(liteLoaderJsonPath);
            if (!liteLoaderJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "LiteLoader", liteLoaderJsonPath,
                    liteLoaderJsonText.Substring(0, Math.Min(liteLoaderJsonText.Length, 1000))));
            liteLoaderJson = (JsonObject)ModBase.GetJson(liteLoaderJsonText);
        }

        if (hasFabric)
        {
            var fabricJsonText = ModBase.ReadFile(fabricJsonPath);
            if (!fabricJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "Fabric", fabricJsonPath,
                    fabricJsonText.Substring(0, Math.Min(fabricJsonText.Length, 1000))));
            fabricJson = (JsonObject)ModBase.GetJson(fabricJsonText);
        }

        if (hasLegacyFabric)
        {
            var legacyFabricJsonText = ModBase.ReadFile(legacyFabricJsonPath);
            if (!legacyFabricJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "Legacy Fabric", fabricJsonPath,
                    legacyFabricJsonText.Substring(0, Math.Min(legacyFabricJsonText.Length, 1000))));
            legacyFabricJson = (JsonObject)ModBase.GetJson(legacyFabricJsonText);
        }

        if (hasLabyMod)
        {
            var labyModJsonText = ModBase.ReadFile(labyModJsonPath);
            if (!labyModJsonText.StartsWithF("{"))
                throw new Exception(Lang.Text("Minecraft.Download.Error.JsonInvalid", "LabyMod", labyModJsonPath,
                    labyModJsonText.Substring(0, Math.Min(labyModJsonText.Length, 1000))));
            labyModJson = (JsonObject)ModBase.GetJson(labyModJsonText);
        }



        // 获取 minecraftArguments
        var allArguments = (minecraftJson is not null ? (minecraftJson["minecraftArguments"] ?? " ").ToString() : " ") +
                           " " +
                           (labyModJson is not null ? (labyModJson["minecraftArguments"] ?? " ").ToString() : " ") +
                           " " +
                           (optiFineJson is not null ? (optiFineJson["minecraftArguments"] ?? " ").ToString() : " ") +
                           " " + (forgeJson is not null ? (forgeJson["minecraftArguments"] ?? " ").ToString() : " ") +
                           " " +
                           (neoForgeJson is not null ? (neoForgeJson["minecraftArguments"] ?? " ").ToString() : " ") +
                           " " + (liteLoaderJson is not null
                               ? (liteLoaderJson["minecraftArguments"] ?? " ").ToString()
                               : " ") + " " + (cleanroomJson is not null
                               ? (cleanroomJson["minecraftArguments"] ?? " ").ToString()
                               : " ");
        // 分割参数字符串
        var rawArguments = allArguments.Split(" ").Where(l => !string.IsNullOrEmpty(l)).Select(l => l.Trim()).ToList();
        var splitArguments = new List<string>();
        for (int i = 0, loopTo = rawArguments.Count - 1; i <= loopTo; i++)
            if (rawArguments[i].StartsWithF("-"))
                splitArguments.Add(rawArguments[i]);
            else if (splitArguments.Any() && splitArguments.Last().StartsWithF("-") &&
                     !splitArguments.Last().Contains(" "))
                splitArguments[splitArguments.Count - 1] = splitArguments.Last() + " " + rawArguments[i];
            else
                splitArguments.Add(rawArguments[i]);

        var realArguments = splitArguments.Distinct().ToList().Join(" ");
        // 合并
        // 相关讨论见 #2801
        if (mMCPackInfo is not null)
        {
            if (mMCPackInfo.isMinecraftOverrided)
            {
                ModBase.Log("[Download] 当前实例的 MC 核心已被修改，使用对应的 MMC 整合包参数");
                // 以原版 JSON 为基础，按 JSON-Patches 的语义将补丁提供的字段整体覆盖，
                // 同时保留 assets、releaseTime、downloads、clientVersion 等补丁未提供的字段
                outputJson = minecraftJson is not null ? (JsonObject)minecraftJson.DeepClone() : new JsonObject();
                foreach (var patchProperty in mMCPackInfo.overridedJson)
                    outputJson[patchProperty.Key] = patchProperty.Value?.DeepClone();
            }
            else
            {
                ModBase.Log("[Download] 存在无修改 MC 核心文件的 MMC 整合包信息，应用相关参数");
                outputJson = minecraftJson;
                // 合并来自 MultiMC 的 JSON
                outputJson.Merge(mMCPackInfo.overridedJson);
            }
        }
        else
        {
            outputJson = minecraftJson;
        }

        if (hasOptiFine)
        {
            // 合并 OptiFine
            optiFineJson.Remove("releaseTime");
            optiFineJson.Remove("time");
            outputJson.Merge(optiFineJson);
        }

        if (hasForge)
            if (mMCPackInfo is null || !mMCPackInfo.isForgeOverrided)
            {
                // 合并 Forge
                forgeJson.Remove("releaseTime");
                forgeJson.Remove("time");
                outputJson.Merge(forgeJson);
            }

        if (hasNeoForge)
            if (mMCPackInfo is null || !mMCPackInfo.isNeoForgeOverrided)
            {
                // 合并 NeoForge
                neoForgeJson.Remove("releaseTime");
                neoForgeJson.Remove("time");
                outputJson.Merge(neoForgeJson);
            }

        if (hasCleanroom)
            if (mMCPackInfo is null || !mMCPackInfo.isCleanroomOverrided)
            {
                // 合并 Cleanroom
                cleanroomJson.Remove("releaseTime");
                cleanroomJson.Remove("time");
                outputJson.Merge(cleanroomJson);
            }

        if (hasLiteLoader)
        {
            // 合并 LiteLoader
            liteLoaderJson.Remove("releaseTime");
            liteLoaderJson.Remove("time");
            outputJson.Merge(liteLoaderJson);
        }

        if (hasFabric)
            if (mMCPackInfo is null || !mMCPackInfo.isFabricOverrided)
            {
                // 合并 Fabric
                fabricJson.Remove("releaseTime");
                fabricJson.Remove("time");
                outputJson.Merge(fabricJson);
            }

        if (hasLegacyFabric)
            if (mMCPackInfo is null || !mMCPackInfo.isFabricOverrided)
            {
                // 合并 Fabric
                legacyFabricJson.Remove("releaseTime");
                legacyFabricJson.Remove("time");
                outputJson.Merge(legacyFabricJson);
            }

        if (hasLabyMod)
        {
            // 合并 LabyMod
            labyModJson.Remove("releaseTime");
            labyModJson.Remove("time");
            if (outputJson is null)
                outputJson = new JsonObject();
            outputJson.Merge(labyModJson);

            var labyModLib =
                (JsonObject)Requester.FetchJson(
                    $"https://releases.r2.labymod.net/api/v1/libraries/{labyModChannel}.json", RequestParam.WithRetry);
            var labyModCore = (JsonObject)Requester.FetchJson(
                $"https://releases.r2.labymod.net/api/v1/manifest/{labyModChannel}/latest.json", RequestParam.WithRetry);
            var outputLibraries = new JsonArray();
            var isolatedLibraries = new Dictionary<string, bool>();
            var minecraftVersion = labyModJson["_minecraftVersion"];

            foreach (var Library in labyModLib["isolated_libraries"].AsArray())
                if (((JsonArray)Library["versions"]).Contains(minecraftVersion))
                    isolatedLibraries.Add(Library["name"].ToString(), true);

            foreach (var Library in labyModJson["libraries"].AsArray())
            {
                var regexMatchResult = Library["name"].ToString().RegexSeek(RegexPatterns.CatchLwjglInLib);
                if (regexMatchResult is null ||
                    !isolatedLibraries.Contains(new KeyValuePair<string, bool>(regexMatchResult, true)))
                    outputLibraries.Add(Library.DeepClone());
            }

            foreach (var Library in labyModLib["libraries"].AsArray())
            {
                var libraryUrl = Library?["url"]?.ToString() ?? "";
                outputLibraries.Add(new JsonObject
                {
                    ["name"] = Library?["name"]?.ToString(),
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = libraryUrl.Substring(libraryUrl.LastIndexOfF("https://releases.r2.labymod.net/libraries/") + 42),
                            ["sha1"] = Library?["sha1"]?.ToString(),
                            ["size"] = Library?["size"]?.DeepClone(),
                            ["url"] = libraryUrl
                        }
                    }
                });
            }

            var labyModCommitReference = labyModCore["commitReference"]?.ToString() ?? "";
            outputLibraries.Add(new JsonObject
            {
                ["name"] = "net.labymod:LabyMod:4",
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = "net/labymod/LabyMod/4/LabyMod-4.jar",
                        ["sha1"] = labyModCore["sha1"]?.ToString(),
                        ["size"] = labyModCore["size"]?.DeepClone(),
                        ["url"] = $"https://releases.r2.labymod.net/api/v1/download/labymod4/{labyModChannel}/{labyModCommitReference}.jar"
                    }
                }
            });
            outputJson["libraries"] = outputLibraries;
            outputJson.Add("labymod_data", new JsonObject
            {
                ["channelType"] = labyModChannel,
                ["commitReference"] = labyModCommitReference,
                ["version"] = labyModCore["labyModVersion"]?.ToString(),
                ["versionType"] = "release"
            });
        }

        // 整合包通过 JSON Patches 引入 LWJGL3（例如 GTNH 的 lwjgl3ify）时，
        // 移除与之冲突的 LWJGL2 支持库，参考 MultiMC 中 org.lwjgl3 组件与 org.lwjgl 组件的 conflicts 关系
        if (outputJson["libraries"] is JsonArray finalLibs && finalLibs.Any(IsLwjgl3Library))
        {
            for (var i = finalLibs.Count - 1; i >= 0; i--)
                if (IsLwjgl2Library(finalLibs[i]))
                {
                    ModBase.Log("[Download] 已移除与 LWJGL3 冲突的 LWJGL2 支持库：" + finalLibs[i]?["name"]);
                    finalLibs.RemoveAt(i);
                }
        }

        // 修改
        if (realArguments is not null && !string.IsNullOrEmpty(realArguments.Replace(" ", "")))
            outputJson["minecraftArguments"] = realArguments;
        if (mMCPackInfo is not null && mMCPackInfo.isMcArgsEdited)
            outputJson.Remove("minecraftArguments");
        outputJson.Remove("_comment_");
        outputJson.Remove("inheritsFrom");
        outputJson.Remove("jar");
        outputJson["id"] = outputName;



        ModBase.WriteFile(outputJsonPath, outputJson.ToString());
        if ((minecraftJar ?? "") != (outputJar ?? "")) // 可能是同一个文件
        {
            if (File.Exists(outputJar))
                File.Delete(outputJar);
            ModBase.CopyFile(minecraftJar, outputJar);
        }

        ModBase.Log("[Download] 实例合并 " + outputName + " 完成");

    }



    /// <summary>
    ///     判断支持库是否为 LWJGL 3.x（Maven 坐标形如 org.lwjgl:lwjgl:3.x.y）。
    /// </summary>
    internal static bool IsLwjgl3Library(JsonNode library)
    {
        if (library is not JsonObject lib) return false;
        var segments = (lib["name"]?.ToString() ?? "").Split(':');
        return segments.Length >= 3 && segments[0] == "org.lwjgl" &&
               segments[2].StartsWith("3.", StringComparison.Ordinal);
    }



    /// <summary>
    ///     判断支持库是否为与 LWJGL 3 冲突的 LWJGL 2.x。
    /// </summary>
    internal static bool IsLwjgl2Library(JsonNode library)
    {
        if (library is not JsonObject lib) return false;
        var segments = (lib["name"]?.ToString() ?? "").Split(':');
        return segments.Length >= 3 &&
               (segments[0] == "org.lwjgl.lwjgl" ||
                (segments[0] == "org.lwjgl" && segments[2].StartsWith("2.", StringComparison.Ordinal)));
    }

}
