using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PCL.Core.Utils;

namespace PCL;

public static partial class ModLaunch
{
    internal sealed record JavaConstraints(Version Minimum, Version Maximum, string? RecommendedComponent,
        IReadOnlySet<int>? CompatibleMajors);

    // Shared by ordinary launch and fresh migration/import validation. Keep the original rule order.
    internal static JavaConstraints ResolveJavaConstraints(McInstance instance)
    {
        var minVer = new Version(0, 0, 0, 0);
        var maxVer = new Version(999, 999, 999, 999);

        // MC 大版本检测
        if ((!instance.Info.Valid &&
             instance.releaseTime >= new DateTime(2024, 4, 2)) ||
            (instance.Info.Valid &&
             instance.Info.vanilla >= new Version(20, 0, 5)))
        {
            // 1.20.5+ (24w14a+)：至少 Java 21
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.20.5+ (24w14a+) 要求至少 Java 21");
            minVer = new Version(21, 0, 0, 0);
        }
        else if ((!instance.Info.Valid &&
                  instance.releaseTime >= new DateTime(2021, 11, 16)) ||
                 (instance.Info.Valid &&
                  instance.Info.vanilla.Major >= 18))
        {
            // 1.18 pre2+：至少 Java 17
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.18 pre2+ 要求至少 Java 17");
            minVer = new Version(17, 0, 0, 0);
        }
        else if ((!instance.Info.Valid &&
                  instance.releaseTime >= new DateTime(2021, 5, 11)) ||
                 (instance.Info.Valid &&
                  instance.Info.vanilla.Major >= 17))
        {
            // 1.17+ (21w19a+)：至少 Java 16
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.17+ (21w19a+) 要求至少 Java 16");
            minVer = new Version(16, 0, 0, 0);
        }
        else if (instance.releaseTime.Year >= 2017) // Minecraft 1.12 与 1.11 的分界线正好是 2017 年，太棒了
        {
            // 1.12+：至少 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.12+ 要求至少 Java 8");
            minVer = new Version(1, 8, 0, 0);
        }
        else if (instance.releaseTime <= new DateTime(2013, 5, 1) &&
                 instance.releaseTime.Year >= 2001) // 避免某些版本写个 1960 年
        {
            // 1.5.2-：最高 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.5.2- 要求最高 Java 12");
            maxVer = new Version(1, 8, 999, 999);
        }

        // 原版 26+：获取 Mojang 要求的 Java 版本
        string recommendedComponent = null;
        var recommendedCode =
            instance.JsonObject?["javaVersion"]?["majorVersion"]?.ToObject<int>() ??
            instance.JsonVersion?["java_version"]?.ToObject<int>() ?? 0;
        if (recommendedCode >= 22)
        {
            McLaunchLog("Mojang 要求至少使用 Java " + recommendedCode);
            minVer = new Version(recommendedCode, 0, 0, 0);
            recommendedComponent =
                instance.JsonObject?["javaVersion"]?["component"]?.ToString() ??
                instance.JsonVersion?["java_component"]?.ToString();
            if (string.IsNullOrEmpty(recommendedComponent))
                recommendedComponent = null;
        }

        // OptiFine 检测
        if (instance.Info.HasOptiFine && instance.Info.Valid) // 不管非标准版本
        {
            if (instance.Info.vanilla.Major < 7)
            {
                // <1.7：至多 Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (instance.Info.vanilla.Major >= 8 &&
                     instance.Info.vanilla.Major < 12)
            {
                // 1.8 - 1.11：必须恰好 Java 8
                minVer = new Version(1, 8, 0, 0);
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (instance.Info.vanilla.Major == 12)
            {
                // 1.12：最高 Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
        }

        // Forge 检测
        if (instance.Info.HasForge)
        {
            if (instance.Info.vanilla >= new Version(6, 0, 1) &&
                instance.Info.vanilla <= new Version(7, 0, 2))
            {
                // 1.6.1 - 1.7.2：必须 Java 7
                minVer = new Version(1, 7, 0, 0) > minVer ? new Version(1, 7, 0, 0) : minVer;
                maxVer = new Version(1, 7, 999, 999) < maxVer ? new Version(1, 7, 999, 999) : maxVer;
            }
            else if (instance.Info.vanilla.Major <= 12 ||
                     !instance.Info.Valid) // 非标准版本
            {
                // <=1.12：Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (instance.Info.vanilla.Major <= 14)
            {
                // 1.13 - 1.14：Java 8 - 10
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
                maxVer = new Version(1, 10, 999, 999) < maxVer ? new Version(1, 10, 999, 999) : maxVer;
            }
            else if (instance.Info.vanilla.Major == 15)
            {
                // 1.15：Java 8 - 15
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
                maxVer = new Version(1, 15, 999, 999) < maxVer ? new Version(1, 15, 999, 999) : maxVer;
            }
            else if (McVersionComparer.CompareVersionGe(instance.Info.Forge, "34.0.0") &&
                     McVersionComparer.CompareVersionGe("36.2.25", instance.Info.Forge))
            {
                // 1.16，Forge 34.X ~ 36.2.25：最高 Java 8u321
                maxVer = new Version(1, 8, 0, 320) < maxVer ? new Version(1, 8, 0, 321) : maxVer;
            }
            else if (instance.Info.vanilla.Major >= 18 &&
                     instance.Info.vanilla.Major < 19 &&
                     instance.Info.HasOptiFine) // #305
            {
                // 1.18：若安装了 OptiFine，最高 Java 18
                maxVer = new Version(1, 18, 999, 999) < maxVer ? new Version(1, 18, 999, 999) : maxVer;
            }
        }

        // Cleanroom 检测
        if (instance.Info.HasCleanroom)
        {
            if (!Version.TryParse(instance.Info.Cleanroom.Split('-')[0], out Version cleanroomVersion))
                throw new FormatException("无法解析 Cleanroom 版本号：" + instance.Info.Cleanroom);
            if (cleanroomVersion < new Version(0, 5, 0, 0))
            {
                if (ModBase.modeDebug) ModBase.Log("[Launch] [Debug] Cleanroom 版本低于 0.5，要求至少 Java 21");
                minVer = new Version(21, 0, 0, 0) > minVer ? new Version(21, 0, 0, 0) : minVer;
            }
            else
            {
                if (ModBase.modeDebug) ModBase.Log("[Launch] [Debug] Cleanroom 版本高于 0.5，要求至少 Java 25");
                minVer = new Version(25, 0, 0, 0) > minVer ? new Version(25, 0, 0, 0) : minVer;
            }
        }

        // Fabric 检测
        if (instance.Info.HasFabric && instance.Info.Valid) // 不管非标准版本
        {
            if (instance.Info.vanilla.Major >= 15 &&
                instance.Info.vanilla.Major <= 16)
                // 1.15 - 1.16：Java 8+
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
            else if (instance.Info.vanilla.Major >= 18)
                // 1.18+：Java 17+
                minVer = new Version(1, 17, 0, 0) > minVer ? new Version(1, 17, 0, 0) : minVer;
        }

        // LiteLoader 检测
        if (instance.Info.HasLiteLoader && instance.Info.Valid)
        {
            // 最高 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] LiteLoader 要求最高 Java 8");
            maxVer = new Version(8, 999, 999, 999) < maxVer ? new Version(8, 999, 999, 999) : maxVer;
        }

        // LabyMod 检测
        if (instance.Info.HasLabyMod)
        {
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] LabyMod 要求至少 Java 21");
            minVer = new Version(21, 0, 0, 0) > minVer ? new Version(21, 0, 0, 0) : minVer;
            maxVer = new Version(999, 999, 999, 999);
        }

        // JSON 中要求的版本
        if (instance.JsonObject["javaVersion"] is not null)
        {
            // Val 无法解析 JsonNode（会返回 0），必须先转为字符串
            var majorVersion = ModBase.Val(instance.JsonObject["javaVersion"]["majorVersion"]
                ?.ToString());
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] JSON 中参数要求至少 Java " + majorVersion);
            if (majorVersion <= 8d)
                minVer = new Version(1, (int)Math.Round(majorVersion), 0, 0) > minVer
                    ? new Version(1, (int)Math.Round(majorVersion), 0, 0)
                    : minVer;
            else
                minVer = new Version((int)Math.Round(majorVersion), 0, 0, 0) > minVer
                    ? new Version((int)Math.Round(majorVersion), 0, 0, 0)
                    : minVer;

            if (maxVer < minVer)
                maxVer = new Version(999, 999, 999, 999);
        }

        // compatibleJavaMajors 声明的兼容主版本集合（选择 Java 时按成员资格判断，而非连续区间）
        IReadOnlySet<int> compatibleJavaMajorsSet = null;
        // JSON 中要求的兼容 Java 主版本（例如 MultiMC 整合包的 compatibleJavaMajors）
        if (instance.JsonObject["compatibleJavaMajors"] is JsonArray compatibleMajors &&
            compatibleMajors.Count > 0)
        {
            var majors = compatibleMajors
                // Val 无法解析 JsonNode（会返回 0），必须先转为字符串
                .Select(major => (int)Math.Round(ModBase.Val(major?.ToString())))
                .Where(major => major > 0)
                .Distinct()
                .OrderBy(major => major)
                .ToList();
            if (majors.Count > 0)
            {
                static Version ToCompatibleVersion(int major, bool isMax) => major <= 8
                    ? new Version(1, major, isMax ? 999 : 0, isMax ? 999 : 0)
                    : new Version(major, isMax ? 999 : 0, isMax ? 999 : 0, isMax ? 999 : 0);
                var compatibleMinVer = ToCompatibleVersion(majors.Min(), false);
                var compatibleMaxVer = ToCompatibleVersion(majors.Max(), true);
                if (ModBase.modeDebug)
                    ModBase.Log("[Launch] [Debug] JSON 中要求的兼容 Java 主版本：" + string.Join(", ", majors));
                // compatibleJavaMajors 具有最高优先级
                // 先与其他规则取交集；若无交集（例如旧版本的 Java 8 上限规则），则直接采用 compatibleJavaMajors 声明的范围
                var intersectMin = compatibleMinVer > minVer ? compatibleMinVer : minVer;
                var intersectMax = compatibleMaxVer < maxVer ? compatibleMaxVer : maxVer;
                if (intersectMax < intersectMin)
                {
                    intersectMin = compatibleMinVer;
                    intersectMax = compatibleMaxVer;
                }

                minVer = intersectMin;
                maxVer = intersectMax;
                // 记录原始集合：自动选择时按成员资格过滤，避免接受集合未声明的主版本（例如 [8, 17] 不接受 9 - 16）
                compatibleJavaMajorsSet = majors.ToHashSet();
            }
        }
        return new(minVer, maxVer, recommendedComponent, compatibleJavaMajorsSet);
    }
}
