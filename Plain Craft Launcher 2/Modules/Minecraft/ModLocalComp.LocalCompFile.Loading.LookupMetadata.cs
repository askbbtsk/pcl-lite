using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using static PCL.ModComp;
using static PCL.ModLoader;

namespace PCL;

public static partial class ModLocalComp
{
public partial class LocalCompFile
{

        /// <summary>
        ///     从 Jar 文件中获取 Mod 信息。
        /// </summary>
        internal void LookupMetadata(ZipArchive jar, string? loaderType = null)
        {

            do
            {
                try
                {
                    // 获取信息文件
                    var hasModernMetadata = jar.GetEntry("fabric.mod.json") is not null ||
                                            jar.GetEntry("quilt.mod.json") is not null ||
                                            jar.GetEntry("META-INF/mods.toml") is not null ||
                                            jar.GetEntry("META-INF/neoforge.mods.toml") is not null;
                    var infoEntry = hasModernMetadata ? null : jar.GetEntry("mcmod.info");
                    string infoString = null;
                    if (infoEntry is not null)
                    {
                        infoString = ModBase.ReadFile(infoEntry.Open());
                        if (infoString.Length < 15)
                            infoString = null;
                    }

                    if (infoString is null)
                        break;
                    // 获取可用 Json 项
                    JsonObject infoObject;
                    var jsonObject = (JsonNode)ModBase.GetJson(infoString);
                    if (jsonObject.GetValueKind() == JsonValueKind.Array)
                        infoObject = (JsonObject)jsonObject[0];
                    else
                        infoObject = (JsonObject)jsonObject["modList"][0];
                    // 从文件中获取 Mod 信息项
                    Name = (string)infoObject["name"];
                    Description = (string)infoObject["description"];
                    Version = (string)infoObject["version"];
                    Url = (string)infoObject["url"];
                    ModId = (string)infoObject["modid"];
                    var authorJson = (JsonArray)infoObject["authorList"];
                    if (authorJson is not null)
                    {
                        var author = new List<string>();
                        foreach (var Token in authorJson)
                            author.Add(Token.ToString());
                        if (author.Any())
                            Authors = author.Join(", ");
                    }

                    var logoFile = (string)infoObject["logoFile"];
                    if (logoFile is not null)
                    {
                        var logoItem = jar.GetEntry(logoFile);
                        if (logoItem is not null)
                        {
                            var md5 = ModBase.GetStringMD5(logoItem.Length + logoItem.CompressedLength + path);
                            Logo = System.IO.Path.Combine(ModBase.pathTemp, "Cache", "Images", $"{md5}.png");
                            using (var entryStream = logoItem.Open())
                            {
                                ModBase.WriteFile(Logo, entryStream);
                            }
                        }
                    }

                    var reqs = (JsonArray)infoObject["requiredMods"];
                    if (reqs is not null)
                        foreach (string item in reqs) // 将迭代变量重命名为 item
                            if (!string.IsNullOrEmpty(item))
                            {
                                // 使用一个局部变量 token 来处理逻辑
                                var token = item;

                                token = token.Substring(token.IndexOfF(":") + 1);
                                if (token.Contains("@"))
                                {
                                    var parts = token.Split("@");
                                    AddDependency(parts[0], parts[1]);
                                }
                                else
                                {
                                    AddDependency(token);
                                }
                            }

                    reqs = (JsonArray)infoObject["dependencies"];
                    if (reqs is not null)
                        foreach (string rawToken in reqs)
                            if (!string.IsNullOrEmpty(rawToken))
                            {
                                var id = rawToken.Substring(rawToken.IndexOfF(":") + 1);

                                if (id.Contains("@"))
                                {
                                    var parts = id.Split("@");
                                    AddDependency(parts[0], parts[1]);
                                }
                                else
                                {
                                    AddDependency(id);
                                }
                            }
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "读取 mcmod.info 时出现未知错误（" + path + "）", ModBase.LogLevel.Developer);
                }
            } while (false);



            do
            {
                try
                {
                    var fabricEntry = string.Equals(loaderType, "Fabric", StringComparison.OrdinalIgnoreCase)
                        ? jar.GetEntry("fabric.mod.json")
                        : null;
                    string fabricText = null;
                    if (fabricEntry is not null)
                    {
                        fabricText = ModBase.ReadFile(fabricEntry.Open(), Encoding.UTF8);
                        if (!fabricText.Contains("schemaVersion")) fabricText = null;
                    }

                    if (fabricText is null) break;

                    var fabricObject = (JsonObject)ModBase.GetJson(fabricText);

                    if (fabricObject.ContainsKey("name")) Name = fabricObject["name"].ToString();
                    if (fabricObject.ContainsKey("version")) Version = fabricObject["version"].ToString();
                    if (fabricObject.ContainsKey("description")) Description = fabricObject["description"].ToString();
                    if (fabricObject.ContainsKey("id")) ModId = fabricObject["id"].ToString();
                    if (fabricObject.ContainsKey("contact") && fabricObject["contact"]["homepage"] is not null)
                        Url = fabricObject["contact"]["homepage"].ToString();

                    var authorJson = (JsonArray)fabricObject["authors"];
                    if (authorJson is not null)
                    {
                        var authorList = authorJson.Select(t => t.ToString()).ToList();
                        if (authorList.Any()) Authors = string.Join(", ", authorList);
                    }

                    if (fabricObject.ContainsKey("icon"))
                    {
                        var logoFile = fabricObject["icon"].ToString();
                        var logoItem = jar.GetEntry(logoFile);
                        if (logoItem is not null)
                        {
                            var md5 = ModBase.GetStringMD5(logoItem.Length + logoItem.CompressedLength + path);
                            Logo = System.IO.Path.Combine(ModBase.pathTemp, "Cache", "Images", $"{md5}.png");
                            using (var entryStream = logoItem.Open())
                            {
                                ModBase.WriteFile(Logo, entryStream);
                            }
                        }
                    }

                    // 依赖处理：版本要求允许为字符串或字符串数组。Fabric 的数组语义是任一命中即满足（OR），
                    // 故用 || 合并；元素自身含 || 时 OR 结合律无损
                    if (fabricObject.ContainsKey("depends"))
                        foreach (var dep in (JsonObject)fabricObject["depends"])
                        {
                            var ver = dep.Value is JsonArray arr
                                ? string.Join(" || ",
                                    arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)))
                                : dep.Value?.ToString();
                            AddDependency(dep.Key, string.IsNullOrEmpty(ver) ? null : ver);
                        }

                    // recommends/suggests 是 Fabric 的可选依赖，与 depends 同结构，标 optional
                    foreach (var relKey in new[] { "recommends", "suggests" })
                        if (fabricObject[relKey] is JsonObject rel)
                            foreach (var dep in rel)
                            {
                                var ver = dep.Value is JsonArray arr
                                    ? string.Join(" || ",
                                        arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)))
                                    : dep.Value?.ToString();
                                AddDependency(dep.Key, string.IsNullOrEmpty(ver) ? null : ver, true);
                            }

                    // breaks(硬)/conflicts(软) 是冲突关系而非依赖
                    foreach (var (relKey, hard) in new[] { ("breaks", true), ("conflicts", false) })
                        if (fabricObject[relKey] is JsonObject rel)
                            foreach (var dep in rel)
                            {
                                var ver = dep.Value is JsonArray arr
                                    ? string.Join(" || ",
                                        arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)))
                                    : dep.Value?.ToString();
                                _AddConflict(dep.Key, string.IsNullOrEmpty(ver) ? null : ver, hard);
                            }

                    if (fabricObject["provides"] is JsonArray provides)
                        foreach (var pv in provides)
                        {
                            var id = pv?.ToString();
                            if (!string.IsNullOrEmpty(id)) _ProvidedIds.Add(id.ToLower());
                        }
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "读取 fabric.mod.json 时出错（" + path + "）", ModBase.LogLevel.Developer);
                }
            } while (false);



            do
            {
                try
                {
                    // 获取 quilt.mod.json 文件
                    var quiltEntry = string.Equals(loaderType, "Quilt", StringComparison.OrdinalIgnoreCase)
                        ? jar.GetEntry("quilt.mod.json")
                        : null;
                    string quiltText = null;
                    if (quiltEntry is not null)
                    {
                        quiltText = ModBase.ReadFile(quiltEntry.Open(), Encoding.UTF8);
                        if (!quiltText.Contains("schema_version"))
                            quiltText = null;
                    }

                    if (quiltText is null)
                        break;
                    var quiltObject = (JsonObject)((JsonObject)ModBase.GetJson(quiltText))["quilt_loader"];
                    // 从文件中获取 Mod 信息项
                    if (quiltObject.ContainsKey("id"))
                        ModId = (string)quiltObject["id"];
                    if (quiltObject.ContainsKey("version"))
                        Version = (string)quiltObject["version"];
                    if (quiltObject.ContainsKey("metadata"))
                    {
                        var quiltMetadata = (JsonObject)quiltObject["metadata"];
                        if (quiltMetadata.ContainsKey("name"))
                            Name = (string)quiltMetadata["name"];
                        if (quiltMetadata.ContainsKey("description"))
                            Description = (string)quiltMetadata["description"];
                        if (quiltMetadata.ContainsKey("contact"))
                            Url = (string)(quiltMetadata["contact"]["homepage"] ?? "");
                    }

                    if (quiltObject.ContainsKey("icon"))
                    {
                        var logoFile = (string)quiltObject["icon"];
                        if (logoFile is not null)
                        {
                            var logoItem = jar.GetEntry(logoFile);
                            if (logoItem is not null)
                            {
                                var md5 = ModBase.GetStringMD5(logoItem.Length + logoItem.CompressedLength + path);
                                Logo = System.IO.Path.Combine(ModBase.pathTemp, "Cache", "Images", $"{md5}.png");
                                using (var entryStream = logoItem.Open())
                                {
                                    ModBase.WriteFile(Logo, entryStream);
                                }
                            }
                        }
                    }

                    goto Finished;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "读取 quilt.mod.json 时出现未知错误（" + path + "）", ModBase.LogLevel.Developer);
                }
            } while (false);



            try
            {
                // 获取 mods.toml 文件
                ZipArchiveEntry tomlEntry = null;
                if (string.Equals(loaderType, "NeoForge", StringComparison.OrdinalIgnoreCase))
                    tomlEntry = jar.GetEntry("META-INF/neoforge.mods.toml")
                                ?? jar.GetEntry("META-INF/mods.toml");
                else if (string.Equals(loaderType, "Forge", StringComparison.OrdinalIgnoreCase))
                    tomlEntry = jar.GetEntry("META-INF/mods.toml")
                                ?? jar.GetEntry("META-INF/neoforge.mods.toml");
                string tomlText = null;
                if (tomlEntry is not null)
                {
                    using (var reader = new StreamReader(tomlEntry.Open()))
                    {
                        tomlText = reader.ReadToEnd();
                    }

                    if (tomlText.Length < 15) tomlText = null;
                }

                if (tomlText is not null)
                {
                    // 文件标准化：统一换行符为 \n，去除注释、头尾的空格、空行
                    var lines = new List<string>();
                    var rawLines = tomlText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                    foreach (var rawLine in rawLines)
                    {
                        var line = rawLine;
                        if (line.StartsWithF("#")) continue; // 去除注释
                        if (line.Contains("#")) line = line.Substring(0, line.IndexOfF("#"));
                        // 去除头尾的空格（包含全角空格）
                        line = line.Trim(' ', '\t', '　');
                        if (!string.IsNullOrEmpty(line)) lines.Add(line);
                    }

                    // 读取文件数据
                    // TomlData 存储段落名及其对应的键值对
                    var tomlData = new List<KeyValuePair<string, Dictionary<string, object>>>
                    {
                        new("", new Dictionary<string, object>())
                    };

                    for (var i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i];
                        if (line.StartsWithF("[") && line.EndsWithF("]"))
                        {
                            // 段落标记
                            var header = line.Trim('[', ']');
                            tomlData.Add(
                                new KeyValuePair<string, Dictionary<string, object>>(header,
                                    new Dictionary<string, object>()));
                        }
                        else if (line.Contains("="))
                        {
                            // 字段标记
                            var key = line.Substring(0, line.IndexOfF("=")).TrimEnd(' ', '\t', '　');
                            var rawValue = line.Substring(line.IndexOfF("=") + 1).TrimStart(' ', '\t', '　');
                            object value;

                            if (rawValue.StartsWithF("\"") && rawValue.EndsWithF("\""))
                            {
                                // 单行字符串
                                value = rawValue.Trim('\"');
                            }
                            else if (rawValue.StartsWithF("'''"))
                            {
                                // 多行字符串
                                var valueLines = new List<string> { rawValue.Replace("'''", "") };
                                if (!rawValue.EndsWithF("'''") || rawValue.Length == 3)
                                    while (i < lines.Count - 1)
                                    {
                                        i++;
                                        var valueLine = lines[i];
                                        if (valueLine.EndsWithF("'''"))
                                        {
                                            valueLines.Add(valueLine.Replace("'''", ""));
                                            break;
                                        }

                                        valueLines.Add(valueLine);
                                    }

                                value = string.Join("\n", valueLines).Trim('\n').Replace("\n", "\r\n");
                            }
                            else if (rawValue.ToLower() == "true" || rawValue.ToLower() == "false")
                            {
                                // 布尔型
                                value = rawValue.ToLower() == "true";
                            }
                            else if (double.TryParse(rawValue, out var num))
                            {
                                // 数字型 (模拟 VB 的 Val)
                                value = num;
                            }
                            else
                            {
                                // 默认当做字符串存储
                                value = rawValue;
                            }

                            // 将值存入当前最后的段落中
                            var lastPair = tomlData[tomlData.Count - 1];
                            lastPair.Value[key] = value;
                        }
                    }

                    // 从解析出的数据中提取 Mod 信息
                    Dictionary<string, object> modEntry = null;
                    foreach (var subData in tomlData)
                        if (subData.Key == "mods")
                        {
                            modEntry = subData.Value;
                            break;
                        }

                    if (modEntry is not null && modEntry.ContainsKey("modId"))
                    {
                        ModId = modEntry["modId"].ToString();
                        // 假设 _ModId 是内部属性，如果为 null 说明设置失败
                        if (_ModId is not null)
                        {
                            if (modEntry.ContainsKey("displayName")) Name = modEntry["displayName"].ToString();
                            if (modEntry.ContainsKey("description")) Description = modEntry["description"].ToString();
                            if (modEntry.ContainsKey("version")) Version = modEntry["version"].ToString();

                            // [0] 是全局段落（无 Header）
                            if (tomlData[0].Value.ContainsKey("displayURL"))
                                Url = tomlData[0].Value["displayURL"].ToString();
                            if (tomlData[0].Value.ContainsKey("authors"))
                                Authors = tomlData[0].Value["authors"].ToString();

                            var ownModIdL = ModId.ToLower();
                            var jarMods = tomlData
                                .Where(s => s.Key == "mods" && s.Value.ContainsKey("modId"))
                                .Select(s => (Id: s.Value["modId"].ToString().ToLower(),
                                    Version: s.Value.ContainsKey("version") ? s.Value["version"].ToString() : null))
                                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
                            var jarModIds = jarMods.Keys
                                .ToHashSet();
                            foreach (var sib in jarModIds)
                                if (sib != ownModIdL)
                                {
                                    _ProvidedIds.Add(sib);
                                    if (!string.IsNullOrWhiteSpace(jarMods[sib]))
                                        _ProvidedVersions[sib] = jarMods[sib];
                                }
                            foreach (var subData in tomlData)
                            {
                                var headerL = subData.Key.ToLower();
                                if (headerL != "dependencies" && !headerL.StartsWithF("dependencies."))
                                    continue;
                                // 兄弟 mod 的依赖段不再整段跳过：否则兄弟对外部 mod 的真实依赖也被一起丢掉。
                                // 改为逐条过滤同 jar 内的 mod id（见下方 depModId 判断），只滤掉 jar 内互相依赖。

                                {
                                    var depEntry = subData.Value;
                                    if (depEntry.ContainsKey("modId"))
                                    {
                                        var depModId = depEntry["modId"].ToString();
                                        var declarerId = headerL.StartsWithF("dependencies.")
                                            ? headerL.Substring("dependencies.".Length).Trim('"')
                                            : ownModIdL;
                                        if (!jarModIds.Contains(declarerId)) continue;
                                        var type = depEntry.ContainsKey("type")
                                            ? depEntry["type"].ToString().ToLower()
                                            : null;
                                        // 仅服务端依赖/冲突与客户端无关，跳过（side 缺省为 BOTH）
                                        var serverOnly = depEntry.ContainsKey("side") &&
                                                         depEntry["side"].ToString().ToLower() == "server";
                                        var range = depEntry.ContainsKey("versionRange")
                                            ? depEntry["versionRange"].ToString()
                                            : null;
                                        var sameJarTarget = jarModIds.Contains(depModId.ToLower());
                                        // 同 JAR 兄弟依赖天然满足，可跳过；兄弟冲突则表示物理 JAR 自身不可加载，必须保留。
                                        if (sameJarTarget && type is not ("incompatible" or "discouraged")) continue;
                                        // incompatible/discouraged 不是依赖而是冲突关系，单独记录（incompatible=硬）
                                        if (type is "incompatible" or "discouraged")
                                        {
                                            if (!serverOnly)
                                                _AddConflict(depModId, range, type == "incompatible", declarerId);
                                        }
                                        else if (!serverOnly)
                                        {
                                            // optional：type=optional 或 mandatory=false（布尔或带引号字符串）；未显式声明即必需
                                            var optional = type == "optional" ||
                                                           (depEntry.ContainsKey("mandatory") &&
                                                            depEntry["mandatory"].ToString().ToLower() == "false");
                                            AddDependency(depModId, range, optional);
                                        }
                                    }
                                }
                            }

                            // 内联表写法：dependencies[.<id>] = [ { modId=".." , ... }, ... ]。
                            foreach (var inline in _ExtractInlineDepArrays(string.Join("\n", lines)))
                                foreach (Match obj in Regex.Matches(inline.Body, @"\{([^}]*)\}"))
                                {
                                        var declarerId = inline.DeclarerId ?? ownModIdL;
                                        if (!jarModIds.Contains(declarerId)) continue;
                                        var body = obj.Groups[1].Value;
                                        var idMatch = Regex.Match(body, "modId\\s*=\\s*\"([^\"]+)\"",
                                            RegexOptions.IgnoreCase);
                                        if (!idMatch.Success) continue;
                                        if (Regex.IsMatch(body, "side\\s*=\\s*\"server\"", RegexOptions.IgnoreCase))
                                            continue;
                                        var typeMatch = Regex.Match(body,
                                            "type\\s*=\\s*\"(incompatible|discouraged|optional|required)\"",
                                            RegexOptions.IgnoreCase);
                                        var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLower() : null;
                                        var optional =
                                            Regex.IsMatch(body, @"mandatory\s*=\s*""?false", RegexOptions.IgnoreCase) ||
                                            type == "optional";
                                        var vr = Regex.Match(body, "versionRange\\s*=\\s*\"([^\"]+)\"",
                                            RegexOptions.IgnoreCase);
                                        var range = vr.Success ? vr.Groups[1].Value : null;
                                        var depId = idMatch.Groups[1].Value;
                                        var sameJarTarget = jarModIds.Contains(depId.ToLower());
                                        if (sameJarTarget && type is not ("incompatible" or "discouraged")) continue;
                                        if (type is "incompatible" or "discouraged")
                                            _AddConflict(depId, range, type == "incompatible", declarerId);
                                        else
                                            AddDependency(depId, range, optional);
                                }

                            // 加载成功，跳转到完成标签
                            goto Finished;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取 mods.toml 时出现未知错误（" + path + "）", ModBase.LogLevel.Developer);
            }



            do
            {
                try
                {
                    // 获取 fml_cache_annotation.json 文件
                    var fmlEntry = jar.GetEntry("META-INF/fml_cache_annotation.json");
                    string fmlText = null;
                    if (fmlEntry is not null)
                    {
                        fmlText = ModBase.ReadFile(fmlEntry.Open(), Encoding.UTF8);
                        if (!fmlText.Contains("Lnet/minecraftforge/fml/common/Mod;"))
                            fmlText = null;
                    }

                    if (fmlText is null)
                        break;
                    var fmlJson = (JsonObject)ModBase.GetJson(fmlText);
                    // 获取可用 Json 项
                    JsonObject fmlObject = null;
                    foreach (var ModFilePair in fmlJson)
                    {
                        var modFileAnnos = (JsonArray)ModFilePair.Value["annotations"];
                        if (modFileAnnos is not null)
                            // 先获取 Mod
                            foreach (var ModFileAnno in modFileAnnos)
                            {
                                var name = (string)(ModFileAnno["name"] ?? "");
                                if (name == "Lnet/minecraftforge/fml/common/Mod;")
                                {
                                    fmlObject = (JsonObject)ModFileAnno["values"];
                                    goto Got;
                                }
                            }
                    }

                    break;
                    Got: ;

                    // 从文件中获取 Mod 信息项
                    if (fmlObject.ContainsKey("useMetadata") &&
                        (fmlObject["useMetadata"]["value"] ?? "").ToString().ToLower() == "true")
                    {
                        // 要求使用 mcmod.info 中的信息
                        var value = (string)fmlObject["modid"]["value"];
                        if (value is null)
                            break;
                        value = value.ToLower().RegexSeek(RegexPatterns.ModIdMatch);
                        if (value is not null && value.ToLower() != "name" && value.Length > 1 &&
                            (ModBase.Val(value).ToString() ?? "") != (value ?? ""))
                            if (!possibleModId.Contains(value))
                                possibleModId.Add(value);
                        break;
                    }

                    if (fmlObject.ContainsKey("name"))
                        Name = (string)fmlObject["name"]["value"];
                    if (fmlObject.ContainsKey("version"))
                        Version = (string)fmlObject["version"]["value"];
                    if (fmlObject.ContainsKey("modid"))
                        ModId = (string)fmlObject["modid"]["value"];
                    if (!fmlObject.ContainsKey("serverSideOnly") ||
                        !fmlObject["serverSideOnly"]["value"].ToObject<bool>())
                    {
                        // 添加 Minecraft 依赖
                        var depMinecraft = (string)((fmlObject["acceptedMinecraftVersions"] is not null
                            ? fmlObject["acceptedMinecraftVersions"]["value"]
                            : "") ?? "");
                        if (!string.IsNullOrEmpty(depMinecraft))
                            AddDependency("minecraft", depMinecraft);
                        // 添加其他依赖
                        var deps = (string)((fmlObject["dependencies"] is not null
                            ? fmlObject["dependencies"]["value"]
                            : "") ?? "");
                        if (!string.IsNullOrEmpty(deps))
                            foreach (var item in deps.Split(";"))
                            {
                                if (string.IsNullOrEmpty(item) || !item.StartsWithF("required-"))
                                    continue;

                                // 使用局部变量处理逻辑，不要直接修改迭代变量 item
                                var dep = item.Substring(item.IndexOfF(":") + 1);

                                if (dep.Contains("@"))
                                {
                                    var parts = dep.Split("@");
                                    AddDependency(parts[0], parts[1]);
                                }
                                else
                                {
                                    AddDependency(dep);
                                }
                            }
                    }
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "读取 fml_cache_annotation.json 时出现未知错误（" + path + "）");
                }
            } while (false);



            try
            {
                // 检查并提取资源包的 pack.png 图标
                var packPngEntry = jar.GetEntry("pack.png");
                if (packPngEntry is not null)
                    try
                    {
                        var md5 = ModBase.GetStringMD5(packPngEntry.Length + packPngEntry.CompressedLength + path);
                        Logo = System.IO.Path.Combine(ModBase.pathTemp, "Cache", "Images", $"{md5}.png");
                        using (var entryStream = packPngEntry.Open())
                        {
                            ModBase.WriteFile(Logo, entryStream);
                        }

                        ModBase.Log("成功提取资源包图标：" + path, ModBase.LogLevel.Developer);
                    }
                    catch (Exception logoEx)
                    {
                        ModBase.Log(logoEx, "提取 pack.png 图标失败（" + path + "）", ModBase.LogLevel.Developer);
                    }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "识别资源包图标时出现未知错误（" + path + "）", ModBase.LogLevel.Developer);
            }


            Finished: ;


            if (_Version == "version")
                try
                {
                    var metaEntry = jar.GetEntry("META-INF/MANIFEST.MF");
                    if (metaEntry is not null)
                    {
                        var metaString = ModBase.ReadFile(metaEntry.Open()).Replace(" :", ":").Replace(": ", ":");
                        if (metaString.Contains("Implementation-Version:"))
                        {
                            metaString = metaString.Substring(metaString.IndexOfF("Implementation-Version:") +
                                                              "Implementation-Version:".Count());
                            metaString = metaString.Substring(0, metaString.IndexOfAny("\r\n".ToCharArray()))
                                .Trim();
                            Version = metaString;
                        }
                    }
                }
                catch (Exception)
                {
                    ModBase.Log("获取 META-INF 中的版本信息失败（" + path + "）", ModBase.LogLevel.Developer);
                    Version = null;
                }

            if (_Version is not null && !(_Version.Contains(".") || _Version.Contains("-")))
                Version = null;

        }

}
}
