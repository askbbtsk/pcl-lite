using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class McInstance
{

        /// <summary>
        ///     加载 Minecraft 实例的详细信息。不使用其缓存，且会更新缓存。
        /// </summary>
        public McInstance Load()
        {
            try
            {
                // 检查实例，若出错则跳过数据确定阶段
                if (!Check())
                    goto ExitDataLoad;


                switch (Info.VanillaName ?? "") // 在获取 Version.Original 对象时会完成它的加载
                {
                    case "Unknown":
                    {
                        state = McInstanceState.Error;
                        break;
                    }
                    case "Old":
                    {
                        state = McInstanceState.Old; // 根据 API 进行筛选
                        break;
                    }

                    default:
                    {
                        var realJson = JsonObject is not null ? JsonObject.ToString() : JsonText;
                        // 愚人节与快照版本
                        if ((JsonObject["type"] ?? "").ToString() == "fool" ||
                            !string.IsNullOrEmpty(McVersionClassifier.GetMcFoolName(Info.VanillaName)))
                            state = McInstanceState.Fool;
                        else if (IsSnapshot()) state = McInstanceState.Snapshot;
                        // OptiFine
                        if (realJson.Contains("optifine"))
                        {
                            state = McInstanceState.OptiFine;
                            Info.HasOptiFine = true;
                            Info.OptiFine = realJson.RegexSeek(RegexPatterns.OptiFineVersion) ??
                                            Lang.Text("Minecraft.Version.Unknown");
                        }

                        // LiteLoader
                        if (realJson.Contains("liteloader"))
                        {
                            state = McInstanceState.LiteLoader;
                            Info.HasLiteLoader = true;
                        }

                        // Fabric、Forge、Quilt、LabyMod、Legacy Fabric
                        if (realJson.Contains("labymod_data"))
                        {
                            state = McInstanceState.LabyMod;
                            Info.HasLabyMod = true;
                            Info.LabyMod = (string)JsonObject["labymod_data"]["version"];
                        }
                        else if (realJson.Contains("net.legacyfabric:intermediary"))
                        {
                            state = McInstanceState.LegacyFabric;
                            Info.HasLegacyFabric = true;
                            Info.LegacyFabric =
                                (realJson.RegexSeek(RegexPatterns.LegacyFabricVersion) ??
                                 Lang.Text("Minecraft.Version.Unknown"))
                                .Replace("+build", "");
                        }
                        else if (realJson.Contains("net.fabricmc:fabric-loader"))
                        {
                            state = McInstanceState.Fabric;
                            Info.HasFabric = true;
                            Info.Fabric =
                                (realJson.RegexSeek(RegexPatterns.FabricVersion) ??
                                 Lang.Text("Minecraft.Version.Unknown")).Replace("+build", "");
                        }
                        else if (realJson.Contains("org.quiltmc:quilt-loader"))
                        {
                            state = McInstanceState.Quilt;
                            Info.HasQuilt = true;
                            Info.Quilt =
                                (realJson.RegexSeek(RegexPatterns.QuiltVersion) ??
                                 Lang.Text("Minecraft.Version.Unknown")).Replace("+build", "");
                        }
                        else if (realJson.Contains("com.cleanroommc:cleanroom:"))
                        {
                            state = McInstanceState.Cleanroom;
                            Info.HasCleanroom = true;
                            Info.Cleanroom =
                                (realJson.RegexSeek(RegexPatterns.CleanroomVersion) ??
                                 Lang.Text("Minecraft.Version.Unknown")).Replace("+build", "");
                        }
                        else if (realJson.Contains("minecraftforge") && !realJson.Contains("net.neoforge"))
                        {
                            state = McInstanceState.Forge;
                            Info.HasForge = true;
                            Info.Forge = realJson.RegexSeek(RegexPatterns.ForgeMainVersion) ??
                                         realJson.RegexSeek(RegexPatterns.ForgeLibVersion) ??
                                         Lang.Text("Minecraft.Version.Unknown");
                        }
                        else if (realJson.Contains("net.neoforge"))
                        {
                            // 1.20.1 JSON 范例："--fml.forgeVersion", "47.1.99"
                            // 1.20.2+ JSON 范例："--fml.neoForgeVersion", "20.6.119-beta"
                            state = McInstanceState.NeoForge;
                            Info.HasNeoForge = true;
                            Info.NeoForge = realJson.RegexSeek(RegexPatterns.NeoForgeVersion) ??
                                            Lang.Text("Minecraft.Version.Unknown");
                        }

                        break;
                    }
                }


                ExitDataLoad: ;

                // 确定实例图标
                Logo = States.Instance.LogoPath[PathInstance];
                // 实例目录中实际存在的自带图标文件（整合包安装或用户设置时复制的 PCL\Logo.png）是最高优先级的依据：
                // 即使配置中的 Logo / LogoCustom 丢失或被默认图标覆盖，也能恢复自定义图标并修正配置
                var hasCustomLogoFile = File.Exists(Path.Combine(PathInstance, "PCL", "Logo.png"));
                if (hasCustomLogoFile)
                {
                    Logo = @"PCL\Logo.png";
                    if (!States.Instance.IsLogoCustom[PathInstance])
                        States.Instance.IsLogoCustom[PathInstance] = true;
                }
                else if (States.Instance.IsLogoCustom[PathInstance] &&
                         Logo?.EndsWith(@"PCL\Logo.png", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // 自定义图标文件已不存在，清理失效的自定义标记并回退到默认图标
                    States.Instance.IsLogoCustom[PathInstance] = false;
                    Logo = null;
                }

                if (string.IsNullOrEmpty(Logo) || !States.Instance.IsLogoCustom[PathInstance])
                    switch (state)
                    {
                        case McInstanceState.Original:
                        {
                            Logo = ModBase.pathImage + "Blocks/Grass.png";
                            break;
                        }
                        case McInstanceState.Snapshot:
                        {
                            Logo = ModBase.pathImage + "Blocks/CommandBlock.png";
                            break;
                        }
                        case McInstanceState.Old:
                        {
                            Logo = ModBase.pathImage + "Blocks/CobbleStone.png";
                            break;
                        }
                        case McInstanceState.Forge:
                        {
                            Logo = ModBase.pathImage + "Blocks/Anvil.png";
                            break;
                        }
                        case McInstanceState.NeoForge:
                        {
                            Logo = ModBase.pathImage + "Blocks/NeoForge.png";
                            break;
                        }
                        case McInstanceState.Cleanroom:
                        {
                            Logo = ModBase.pathImage + "Blocks/Cleanroom.png";
                            break;
                        }
                        case McInstanceState.Fabric:
                        {
                            Logo = ModBase.pathImage + "Blocks/Fabric.png";
                            break;
                        }
                        case McInstanceState.LegacyFabric:
                        {
                            Logo = ModBase.pathImage + "Blocks/Fabric.png";
                            break;
                        }
                        case McInstanceState.Quilt:
                        {
                            Logo = ModBase.pathImage + "Blocks/Quilt.png";
                            break;
                        }
                        case McInstanceState.OptiFine:
                        {
                            Logo = ModBase.pathImage + "Blocks/GrassPath.png";
                            break;
                        }
                        case McInstanceState.LiteLoader:
                        {
                            Logo = ModBase.pathImage + "Blocks/Egg.png";
                            break;
                        }
                        case McInstanceState.Fool:
                        {
                            Logo = ModBase.pathImage + "Blocks/GoldBlock.png";
                            break;
                        }
                        case McInstanceState.LabyMod:
                        {
                            Logo = ModBase.pathImage + "Blocks/LabyMod.png";
                            break;
                        }

                        default:
                        {
                            Logo = ModBase.pathImage + "Blocks/RedstoneBlock.png";
                            break;
                        }
                    }

                // 确定实例描述
                if (state != McInstanceState.Error)
                {
                    Desc = States.Instance.CustomInfo[PathInstance];
                    if ((Desc ?? "") == (GetDefaultDescription() ?? ""))
                        Desc = "";
                }

                // 确定实例收藏状态
                IsStar = States.Instance.Starred[PathInstance];
                // 确定实例显示种类
                displayType = (McInstanceCardType)States.Instance.CardType[PathInstance];
                // 写入缓存
                if (Directory.Exists(PathInstance))
                {
                    States.Instance.State[PathInstance] = (int)state;
                    States.Instance.Info[PathInstance] = Desc;
                    States.Instance.LogoPath[PathInstance] = Logo;
                }

                if (state != McInstanceState.Error)
                {
                    States.Instance.ReleaseTime[PathInstance] = releaseTime.ToString("yyyy'-'MM'-'dd HH':'mm", CultureInfo.InvariantCulture);
                    States.Instance.FabricVersion[PathInstance] = Info.Fabric;
                    States.Instance.LegacyFabricVersion[PathInstance] = Info.LegacyFabric;
                    States.Instance.QuiltVersion[PathInstance] = Info.Quilt;
                    States.Instance.LabyModVersion[PathInstance] = Info.LabyMod;
                    States.Instance.OptiFineVersion[PathInstance] = Info.OptiFine;
                    States.Instance.HasLiteLoader[PathInstance] = Info.HasLiteLoader;
                    States.Instance.ForgeVersion[PathInstance] = Info.Forge;
                    States.Instance.NeoForgeVersion[PathInstance] = Info.NeoForge;
                    States.Instance.CleanroomVersion[PathInstance] = Info.Cleanroom;
                    States.Instance.VanillaVersionName[PathInstance] = Info.VanillaName;
                    States.Instance.VanillaVersion[PathInstance] = Info.vanilla.ToString();
                }
            }
            catch (Exception ex)
            {
                Desc = Lang.Text("Select.Instance.Description.UnknownError") + ": " + ex;
                Logo = ModBase.pathImage + "Blocks/RedstoneBlock.png";
                state = McInstanceState.Error;
                ModBase.Log(
                    ex,
                    Lang.Text("Select.Instance.Error.Load", Name),
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Select.Instance.Error.Load", Name));
            }
            finally
            {
                IsLoaded = true;
            }

            return this;
        }


        private bool IsSnapshot()
        {
            return new[] { "w", "snapshot", "rc", "pre", "experimental", "-" }.Any(s =>
                       Info.VanillaName.ContainsF(s, true)) || Name.ContainsF("combat", true) ||
                   (JsonObject["type"] ?? "").ToString() == "snapshot" ||
                   (JsonObject["type"] ?? "").ToString() == "pending";
        }


        /// <summary>
        ///     获取实例的默认描述。
        /// </summary>
        public string GetDefaultDescription()
        {
            // Mod Loader 信息
            var modLoaderInfo = "";
            if (this.Info.HasForge)
                modLoaderInfo += ", Forge" + (this.Info.Forge == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.Forge);
            if (this.Info.HasNeoForge)
                modLoaderInfo += ", NeoForge" + (this.Info.NeoForge == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.NeoForge);
            if (this.Info.HasCleanroom)
                modLoaderInfo += ", Cleanroom" + (this.Info.Cleanroom == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.Cleanroom);
            if (this.Info.HasLabyMod)
                modLoaderInfo += ", LabyMod" + (this.Info.LabyMod == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.LabyMod);
            if (this.Info.HasFabric)
                modLoaderInfo += ", Fabric" + (this.Info.Fabric == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.Fabric);
            if (this.Info.HasQuilt)
                modLoaderInfo += ", Quilt" + (this.Info.Quilt == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.Quilt);
            if (this.Info.HasLegacyFabric)
                modLoaderInfo += ", Legacy Fabric" +
                                 (this.Info.LegacyFabric == Lang.Text("Minecraft.Version.Unknown")
                                     ? ""
                                     : " " + this.Info.LegacyFabric);
            if (this.Info.HasOptiFine)
                modLoaderInfo += ", OptiFine" + (this.Info.OptiFine == Lang.Text("Minecraft.Version.Unknown")
                    ? ""
                    : " " + this.Info.OptiFine.Replace("-", " ").Replace("_", " "));
            if (this.Info.HasLiteLoader)
                modLoaderInfo += ", LiteLoader";
            // 基础信息
            string info;
            switch (state)
            {
                case McInstanceState.Snapshot:
                case McInstanceState.Original:
                case McInstanceState.Forge:
                case McInstanceState.NeoForge:
                case McInstanceState.Fabric:
                case McInstanceState.OptiFine:
                case McInstanceState.LiteLoader:
                {
                    if (this.Info.VanillaName.ContainsF("pre", true))
                        info = Lang.Text("Select.Instance.Description.PreRelease", this.Info.VanillaName);
                    else if (this.Info.VanillaName.ContainsF("rc", true))
                        info = Lang.Text("Select.Instance.Description.ReleaseCandidate", this.Info.VanillaName);
                    else if (this.Info.VanillaName.Contains("experimental"))
                        info = Lang.Text("Select.Instance.Description.ExperimentalSnapshot", this.Info.VanillaName);
                    else if (this.Info.VanillaName == "pending")
                        info = Lang.Text("Select.Instance.Description.ExperimentalSnapshot.Pending");
                    else if (IsSnapshot())
                        info = this.Info.Reliable ? Lang.Text("Select.Instance.Description.Snapshot", this.Info.VanillaName.Replace("-snapshot", "")) : Lang.Text("Select.Instance.Description.Snapshot.Unknown");
                    else
                        info = this.Info.Reliable ? Lang.Text("Select.Instance.Description.Release", this.Info.VanillaName) : Lang.Text("Select.Instance.Description.Release.Unknown");

                    break;
                }
                case McInstanceState.Old:
                {
                    info = Lang.Text("Select.Instance.Description.Old");
                    break;
                }
                case McInstanceState.Fool:
                {
                    info = Lang.Text("Select.Instance.Description.AprilFools", this.Info.VanillaName);
                    break;
                }
                case McInstanceState.Error:
                {
                    return Desc; // 已有错误信息
                }

                default:
                {
                    return Lang.Text("Select.Instance.Description.ReportUnknownError");
                }
            }

            return (info + modLoaderInfo).Replace("_", "-");
        }


        // 运算符支持
        public override bool Equals(object obj)
        {
            var instance = obj as McInstance;
            return instance is not null && (PathInstance ?? "") == (instance.PathInstance ?? "");
        }


        public override int GetHashCode() => (PathInstance ?? "").GetHashCode(StringComparison.Ordinal);


        public static bool operator ==(McInstance? a, McInstance? b)
        {
            if (a is null && b is null)
                return true;
            if (a is null || b is null)
                return false;
            return (a.PathInstance ?? "") == (b.PathInstance ?? "");
        }


        public static bool operator !=(McInstance? a, McInstance? b)
        {
            return !(a == b);
        }

}
