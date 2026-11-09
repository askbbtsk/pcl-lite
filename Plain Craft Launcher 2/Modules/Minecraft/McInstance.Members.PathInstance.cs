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
        ///     该实例的实例文件夹，以“\”结尾。
        /// </summary>
        public string PathInstance { get; }


        /// <summary>
        ///     应用版本隔离后，该实例所对应的 Minecraft 根文件夹，以“\”结尾。
        /// </summary>
        public string PathIndie
        {
            get
            {
                if (Config.Instance.IndieV2Config.IsDefault(PathInstance))
                {
                    if (!IsLoaded)
                        Load();

                    // 决定该实例是否应该被隔离
                    bool ShouldBeIndie()
                    {
                        // 从老的实例独立设置中迁移：-1 未决定，0 使用全局设置，1 手动开启，2 手动关闭
                        if (!Config.Instance.IndieV1Config.IsDefault(PathInstance) && Config.Instance.IndieV1[PathInstance] > 0)
                        {
                            ModBase.Log($"[Minecraft] 版本隔离初始化（{Name}）：从老的实例独立设置中迁移");
                            return Config.Instance.IndieV1[PathInstance] == 1;
                        }

                        // 若实例文件夹下包含 mods 或 saves 文件夹，则自动开启版本隔离
                        var modFolder = new DirectoryInfo(PathInstance + @"mods\");
                        var saveFolder = new DirectoryInfo(PathInstance + @"saves\");
                        if ((modFolder.Exists && modFolder.EnumerateFiles().Any()) ||
                            (saveFolder.Exists && saveFolder.EnumerateDirectories().Any()))
                        {
                            ModBase.Log($"[Minecraft] 版本隔离初始化（{Name}）：实例文件夹下存在 mods 或 saves 文件夹，自动开启");
                            return true;
                        }

                        // 根据全局的默认设置决定是否隔离
                        var isRelease = state != McInstanceState.Fool && state != McInstanceState.Old &&
                                        state != McInstanceState.Snapshot;
                        ModBase.Log(
                            $"[Minecraft] 版本隔离初始化（{Name}）：从全局默认设置中（{Config.Launch.IndieSolutionV2}）判断，State {ModBase.GetStringFromEnum(state)}，IsRelease {isRelease}，Modable {Modable}");
                        
                        return Config.Launch.IndieSolutionV2 switch
                        {
                            0 => false, // 关闭
                            1 => Info.HasLabyMod || Modable, // 仅隔离可安装 Mod 的实例
                            2 => !isRelease, // 仅隔离非正式版
                            3 => Info.HasLabyMod || Modable || !isRelease, // 隔离非正式版与可安装 Mod 的实例
                            _ => true // 隔离所有实例
                        };
                    }
                    
                    Config.Instance.IndieV2[PathInstance] = ShouldBeIndie();
                }

                return Config.Instance.IndieV2[PathInstance] ? PathInstance : ModFolder.mcFolderSelected;
            }
        }


        /// <summary>
        ///     该实例的实例文件夹名称。
        /// </summary>
        public string Name
        {
            get
            {
                if (field is null && !string.IsNullOrEmpty(PathInstance))
                    field = ModBase.GetFolderNameFromPath(PathInstance);
                return field;
            }
        }


        /// <summary>
        ///     该实例是否可以安装 Mod。
        /// </summary>
        public bool Modable
        {
            get
            {
                if (!IsLoaded)
                    Load();
                return Info.HasFabric || Info.HasLegacyFabric || Info.HasQuilt || Info.HasForge || Info.HasLiteLoader ||
                       Info.HasNeoForge || Info.HasCleanroom || displayType == McInstanceCardType.API; // #223
            }
        }


        /// <summary>
        ///     实例信息。
        /// </summary>
        public McInstanceInfo Info
        {
            get
            {
                if (field is not null)
                    return field;
                field = new McInstanceInfo();


                try
                {
                    // 获取发布时间并判断是否为老版本
                    try
                    {
                        if (JsonObject["releaseTime"] is null)
                            releaseTime = new DateTime(1970, 1, 1, 15, 0, 0); // 未知版本也可能显示为 1970 年
                        else
                            releaseTime = JsonObject["releaseTime"].ToObject<DateTime>();
                        if (releaseTime.Year > 2000 && releaseTime.Year < 2013)
                        {
                            field.VanillaName = "Old";
                            goto VersionSearchFinish;
                        }
                    }
                    catch
                    {
                        releaseTime = new DateTime(1970, 1, 1, 15, 0, 0);
                    }

                    // 实验性快照
                    if ((string)(JsonObject["type"] ?? "") == "pending")
                    {
                        field.VanillaName = "pending";
                        goto VersionSearchFinish;
                    }

                    // 从 PCL 下载的版本信息中获取版本号
                    if (JsonObject["clientVersion"] is not null)
                    {
                        field.VanillaName = (string)JsonObject["clientVersion"];
                        goto VersionSearchFinish;
                    }

                    // 从 HMCL 下载的版本信息中获取版本号
                    if (JsonObject["patches"] is not null)
                        foreach (var patchNode in JsonObject["patches"].AsArray()) { var patch = patchNode.AsObject();
                            if ((patch["id"] ?? "").ToString() == "game" && patch["version"] is not null)
                            {
                                field.VanillaName = patch["version"].ToString();
                                goto VersionSearchFinish;
                            } }

                    // 从 Forge / NeoForge / LabyMod Arguments 中获取版本号
                    if (JsonObject["arguments"] is not null)
                    {
                        if (JsonObject["arguments"]["game"] is not null)
                        {
                            var mark = false;
                            foreach (var Argument in JsonObject["arguments"]["game"].AsArray())
                            {
                                if (mark)
                                {
                                    field.VanillaName = Argument.ToString();
                                    goto VersionSearchFinish;
                                }

                                if (Argument.ToString() == "--fml.mcVersion")
                                    mark = true;
                            }
                        }

                        if (JsonObject["arguments"]["jvm"] is not null)
                            foreach (var Argument in JsonObject["arguments"]["jvm"].AsArray())
                            {
                                var regexArgument = Argument.ToString().RegexSeek(RegexPatterns.LabyModVersion);
                                if (regexArgument is not null)
                                {
                                    field.VanillaName = regexArgument;
                                    goto VersionSearchFinish;
                                }
                            }
                    }

                    // 从继承实例中获取版本号
                    if (!string.IsNullOrEmpty(InheritInstanceName))
                    {
                        field.VanillaName = (JsonObject["jar"] ?? "").ToString(); // LiteLoader 优先使用 Jar
                        if (string.IsNullOrEmpty(field.VanillaName))
                            field.VanillaName = InheritInstanceName;
                        goto VersionSearchFinish;
                    }

                    // 从下载地址中获取版本号
                    var regex = (JsonObject["downloads"] ?? "").ToString()
                        .RegexSeek(RegexPatterns.MinecraftDownloadUrlVersion);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 从 Forge 版本中获取版本号
                    var librariesString = JsonObject["libraries"].ToString();
                    regex = librariesString.RegexSeek(RegexPatterns.ForgeLibVersion);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 从 OptiFine 版本中获取版本号
                    regex = librariesString.RegexSeek(RegexPatterns.OptiFineLibVersion);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 从 Fabric / Quilt / Legacy Fabric 版本中获取版本号
                    regex = librariesString.RegexSeek(RegexPatterns.FabricLikeLibVersion);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 从 jar 项中获取版本号
                    if (JsonObject["jar"] is not null)
                    {
                        field.VanillaName = JsonObject["jar"].ToString();
                        goto VersionSearchFinish;
                    }

                    // 从 jar 文件的 version.json 中获取版本号
                    if (JsonVersion?["name"] is not null)
                    {
                        var jsonVerName = JsonVersion["name"].ToString();
                        if (jsonVerName.Length < 32) // 因为 wiki 说这玩意儿可能是个 hash，虽然我没发现
                        {
                            field.VanillaName = jsonVerName;
                            ModBase.Log("[Minecraft] 从版本 jar 中的 version.json 获取到版本号：" + jsonVerName);
                            goto VersionSearchFinish;
                        }
                    }

                    // 从 JSON 的 ID 中获取
                    regex = ((string)JsonObject["id"]).RegexSeek(RegexPatterns.MinecraftJsonVersion,
                        RegexOptions.IgnoreCase);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 非准确的版本判断警告
                    ModBase.Log("[Minecraft] 无法完全确认 MC 版本号的版本：" + Name);
                    field.Reliable = false;
                    // 从文件夹名中获取
                    regex = Name.RegexSeek(RegexPatterns.MinecraftJsonVersion, RegexOptions.IgnoreCase);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 从 JSON 出现的版本号中获取
                    var jsonRaw = (JsonObject)JsonObject.DeepClone();
                    jsonRaw.Remove("libraries");
                    var jsonRawText = jsonRaw.ToString();
                    regex = jsonRawText.RegexSeek(RegexPatterns.MinecraftJsonVersion, RegexOptions.IgnoreCase);
                    if (regex is not null)
                    {
                        field.VanillaName = regex;
                        goto VersionSearchFinish;
                    }

                    // 无法获取
                    field.VanillaName = "Unknown";
                    Desc = Lang.Text("Select.Instance.Description.UnknownMcVersion");
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "识别 Minecraft 版本时出错");
                    field.VanillaName = "Unknown";
                    Desc = Lang.Text("Minecraft.Error.Unrecognizable", ex.Message);
                }


                VersionSearchFinish: ;

                if (field.VanillaName.StartsWithF("20.") || field.VanillaName.StartsWithF("21."))
                {
                    field.VanillaName = "1." + field.VanillaName;
                }
                
                field.VanillaName = field.VanillaName.Replace("_unobfuscated", "").Replace(" Unobfuscated", "");
                // 获取版本号
                if (field.VanillaName.StartsWithF("1."))
                {
                    var segments = field.VanillaName.Split(" _-.".ToCharArray());
                    field.vanilla = new Version((int)Math.Round(ModBase.Val(segments.Count() >= 2 ? segments[1] : "0")),
                        0, (int)Math.Round(ModBase.Val(segments.Count() >= 3 ? segments[2] : "0")));
                }
                else if (field.VanillaName.RegexCheck(@"^[2-9][0-9]\."))
                {
                    var segments = field.VanillaName.Split(" _-.".ToCharArray());
                    field.vanilla = new Version((int)Math.Round(ModBase.Val(segments[0])),
                        (int)Math.Round(ModBase.Val(segments.Count() >= 2 ? segments[1] : "0")),
                        (int)Math.Round(ModBase.Val(segments.Count() >= 3 ? segments[2] : "0")));
                }
                else
                {
                    field.vanilla = new Version(9999, 0, 0);
                }

                return field;
            }
            set { field = value; }
        }


        /// <summary>
        ///     该实例的 JSON 文本。
        /// </summary>
        public string JsonText
        {
            get
            {
                // 快速检查 JSON 是否以 { 开头、} 结尾；忽略空白字符
                bool FastJsonCheck(string json)
                {
                    var trimedJson = json.Trim();
                    return trimedJson.StartsWithF("{") && trimedJson.EndsWithF("}");
                }

                ;
                if (field is null)
                {
                    var jsonPath = PathInstance + Name + ".json";
                    if (!File.Exists(jsonPath))
                    {
                        // 如果文件夹下只有一个 JSON 文件，则将其作为实例 JSON
                        var jsonFiles = Directory.GetFiles(PathInstance, "*.json");
                        if (jsonFiles.Count() == 1)
                        {
                            jsonPath = jsonFiles[0];
                            ModBase.Log("[Minecraft] 未找到同名实例 JSON，自动换用 " + jsonPath, ModBase.LogLevel.Debug);
                        }
                        else
                        {
                            throw new Exception(Lang.Text("Minecraft.Error.InstanceJsonNotFound",
                                $"{PathInstance}{Name}.json"));
                        }
                    }

                    field = ModBase.ReadFile(jsonPath);
                    // 如果 ReadFile 失败会返回空字符串；这可能是由于文件被临时占用，故延时后重试
                    if (!FastJsonCheck(field))
                    {
                        if (ModBase.RunInUi())
                        {
                            ModBase.Log($"[Minecraft] 实例 JSON 文件为空或有误，将进行短暂重试（{jsonPath}）", ModBase.LogLevel.Debug);
                            Thread.Sleep(200);
                            field = ModBase.ReadFile(jsonPath);
                        }
                        else
                        {
                            ModBase.Log($"[Minecraft] 实例 JSON 文件为空或有误，将在 2s 后重试读取（{jsonPath}）", ModBase.LogLevel.Debug);
                            Thread.Sleep(2000);
                            field = ModBase.ReadFile(jsonPath);
                        }
                        if (!FastJsonCheck(field))
                            ModBase.GetJson(field);
                    }
                }

                return field;
            }
            set => field = value;
        }


        /// <summary>
        ///     该实例的 JSON 对象。
        ///     若 JSON 存在问题，在获取该属性时即会抛出异常。
        /// </summary>
        public JsonObject JsonObject
        {
            get
            {
                if (field is null)
                {
                    var text = JsonText; // 触发 JsonText 的 Get 事件
                    try
                    {
                        field = (JsonObject)ModBase.GetJson(text);
                        // 转换 HMCL 关键项
                        if (field.ContainsKey("patches") && !field.ContainsKey("time"))
                        {
                            IsHmclFormatJson = true;
                            // 合并 JSON
                            // Dim HasOptiFine As Boolean = False, HasForge As Boolean = False
                            JsonObject currentObject = null;
                            var subjsonList = new List<JsonObject>();
                            foreach (var SubjsonNode in field["patches"].AsArray()) { var subjson = SubjsonNode.AsObject();
                                subjsonList.Add(subjson); }
                            subjsonList.Sort((left, right) =>
                            {
                                var leftVal = ModBase.Val((left["priority"] ?? "0").ToString());
                                var rightVal = ModBase.Val((right["priority"] ?? "0").ToString());
                                return leftVal.CompareTo(rightVal);
                            });
                            foreach (var Subjson in subjsonList)
                            {
                                var id = (string)Subjson["id"];
                                if (id is not null)
                                {
                                    // 合并 JSON
                                    ModBase.Log("[Minecraft] 合并 HMCL 分支项：" + id);
                                    if (currentObject is not null)
                                        currentObject.Merge(Subjson);
                                    else
                                        currentObject = Subjson;
                                }
                                else
                                {
                                    ModBase.Log("[Minecraft] 存在为空的 HMCL 分支项");
                                }
                            }

                            field = currentObject;
                            // 修改附加项
                            field["id"] = Name;
                            if (field.ContainsKey("inheritsFrom"))
                                field.Remove("inheritsFrom");
                        }

                        // 与继承实例合并
                        object inheritInstanceName = null;
                        do
                        {
                            try
                            {
                                inheritInstanceName = field["inheritsFrom"] is null
                                    ? ""
                                    : field["inheritsFrom"].ToString();
                                if (Equals(inheritInstanceName, Name))
                                {
                                    ModBase.Log("[Minecraft] 自引用的继承实例：" + Name, ModBase.LogLevel.Debug);
                                    inheritInstanceName = "";
                                    break;
                                }

                                Recheck: ;

                                if (!Equals(inheritInstanceName, ""))
                                {
                                    var inheritInstance = new McInstance(inheritInstanceName?.ToString() ?? "");
                                    // 继续循环
                                    if (Equals(inheritInstance.InheritInstanceName,
                                            inheritInstanceName))
                                        throw new Exception(Lang.Text("Minecraft.Error.DependencyRecursion",
                                            inheritInstanceName));
                                    inheritInstanceName = inheritInstance.InheritInstanceName;
                                    // 合并
                                    inheritInstance.JsonObject.Merge(field);
                                    field = inheritInstance.JsonObject;
                                    goto Recheck;
                                }
                            }
                            catch (Exception ex)
                            {
                                ModBase.Log(ex, "合并实例依赖项 JSON 失败（" + (inheritInstanceName ?? "null") + "）");
                            }
                        } while (false);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Lang.Text("Minecraft.Error.InitInstanceJsonFailed", Name ?? "null"), ex);
                    }
                }

                return field;
            }
            set => field = value;
        }


        /// <summary>
        ///     是否为旧版 JSON 格式。
        /// </summary>
        public bool IsOldJson => JsonObject["minecraftArguments"] is not null &&
                                 (string)JsonObject["minecraftArguments"] != "";


        /// <summary>
        ///     JSON 是否为 HMCL 格式。
        /// </summary>
        public bool IsHmclFormatJson { get; set; }


        /// <summary>
        ///     实例 JAR 中的 version.json 文件对象。
        ///     若没有则返回 Nothing。
        /// </summary>
        public JsonObject JsonVersion
        {
            get
            {
                if (!_jsonVersionInited)
                {
                    _jsonVersionInited = true;
                    do
                    {
                        try
                        {
                            if (!File.Exists(PathInstance + Name + ".jar"))
                                break;
                            using (var jarArchive = new ZipArchive(new FileStream(PathInstance + Name + ".jar",
                                       FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            {
                                var versionJson = jarArchive.GetEntry("version.json");
                                if (versionJson is not null)
                                    using (var versionJsonStream = new StreamReader(versionJson.Open()))
                                    {
                                        field = (JsonObject)ModBase.GetJson(versionJsonStream.ReadToEnd());
                                    }
                            }
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, $"从实例 JAR 中读取 version.json 失败 ({PathInstance}{Name}.jar)");
                        }
                    } while (false);
                }

                return field;
            }
        }


        /// <summary>
        ///     该实例的依赖实例。若无依赖实例则为空字符串。
        /// </summary>
        public string InheritInstanceName
        {
            get
            {
                if (field is null)
                {
                    field = (JsonObject["inheritsFrom"] ?? "").ToString();
                    // 由于过老的 LiteLoader 中没有 Inherits（例如 1.5.2），需要手动判断以获取真实继承实例
                    // 此外，由于这里的加载早于实例种类判断，所以需要手动判断是否为 LiteLoader
                    // 如果实例提供了不同的 JAR，代表所需的 JAR 可能已被更改，则跳过 Inherit 替换
                    if (JsonText.Contains("liteloader") && (Info.VanillaName ?? "") != (Name ?? "") &&
                        !JsonText.Contains("logging"))
                        if (((JsonObject["jar"] ?? Info.VanillaName).ToString() ?? "") == (Info.VanillaName ?? ""))
                            field = Info.VanillaName;
                    // HMCL 实例无 JSON
                    if (IsHmclFormatJson)
                        field = "";
                }

                return field;
            }
        }


        /// <summary>
        ///     检查 Minecraft 版本，若检查通过 State 则为 Original 且返回 True。
        /// </summary>
        public bool Check()
        {
            // 检查文件夹
            if (!Directory.Exists(PathInstance))
            {
                state = McInstanceState.Error;
                Desc = Lang.Text("Select.Instance.Description.NotFound", Name);
                return false;
            }

            // 检查权限
            try
            {
                Directory.CreateDirectory(PathInstance + @"PCL\");
                ModBase.CheckPermissionWithException(PathInstance + @"PCL\");
            }
            catch (Exception ex)
            {
                state = McInstanceState.Error;
                Desc = Lang.Text("Select.Instance.Description.NoPermission");
                ModBase.Log(ex, "没有访问实例文件夹的权限");
                return false;
            }

            // 确认 JSON 可用性
            try
            {
                var jsonObjCheck = JsonObject;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "实例 JSON 可用性检查失败（" + PathInstance + "）");
                JsonText = "";
                JsonObject = null;
                Desc = ex.Message;
                state = McInstanceState.Error;
                return false;
            }

            // 检查版本号获取
            try
            {
                if (string.IsNullOrEmpty(Info.VanillaName))
                    throw new Exception(Lang.Text("Minecraft.Error.VersionNumberEmpty"));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "版本号获取失败（" + Name + "）");
                state = McInstanceState.Error;
                Desc = Lang.Text("Minecraft.Error.VersionNumberFetchFailed", ex);
                return false;
            }

            // 检查依赖实例
            try
            {
                if (!string.IsNullOrEmpty(InheritInstanceName))
                    if (!File.Exists(Path.Combine(ModBase.GetPathFromFullPath(PathInstance), InheritInstanceName, InheritInstanceName + ".json")))
                    {
                        state = McInstanceState.Error;
                        Desc = Lang.Text("Select.Instance.Description.NeedInherit", InheritInstanceName);
                        return false;
                    }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "依赖实例检查出错（" + Name + "）");
                state = McInstanceState.Error;
                Desc = Lang.Text("Select.Instance.Description.UnknownError") + ": " + ex;
                return false;
            }

            state = McInstanceState.Original;
            return true;
        }

}
