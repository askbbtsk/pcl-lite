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
        ///     Mod 的名称。若不可用则为 ModID 或无扩展的文件名。
        /// </summary>
        public string Name
        {
            get
            {
                if (_Name is null)
                    Load();
                if (_Name is null)
                    _Name = _ModId;
                if (_Name is null)
                {
                    if (IsFolder)
                        _Name = ModBase.GetFolderNameFromPath(ActualPath);
                    else
                        _Name = ModBase.GetFileNameWithoutExtentionFromPath(path);
                }

                return _Name;
            }
            set
            {
                if (_Name is null && value is not null && !value.Contains("modname") && value.ToLower() != "name" &&
                    value.Length > 1 && (ModBase.Val(value).ToString() ?? "") != (value ?? "")) _Name = value;
            }
        }


        /// <summary>
        ///     Mod 的描述信息。
        /// </summary>
        public string Description
        {
            get
            {
                if (_Description is null)
                    Load();
                if (_Description is null && FileUnavailableReason is not null)
                    _Description = FileUnavailableReason.Message;
                // If _Description Is Nothing Then _Description = Path
                return _Description;
            }
            set
            {
                if (_Description is null && value is not null && value.Length > 2)
                {
                    _Description = value.Trim('\n');
                    // 优化显示：若以 [a-zA-Z0-9] 结尾，加上小数点句号
                    if (_Description.ToLower().LastIndexOfAny("qwertyuiopasdfghjklzxcvbnm0123456789".ToCharArray()) ==
                        _Description.Length - 1)
                        _Description += ".";
                }
            }
        }


        /// <summary>
        ///     文件类型标签。
        /// </summary>
        public List<string> Tags
        {
            get
            {
                if (field is null)
                {
                    field = new List<string>();
                    if (IsFolder)
                    {
                        field.Add("文件夹");
                    }
                    else
                    {
                        var extension = System.IO.Path.GetExtension(RawPath).ToLower();
                        switch (extension ?? "")
                        {
                            case ".litematic":
                            {
                                field.Add("原理图");
                                break;
                            }
                            case ".schem":
                            case ".schematic":
                            {
                                field.Add("Schematic结构");
                                break;
                            }
                            case ".nbt":
                            {
                                field.Add("原版结构");
                                break;
                            }
                        }
                    }
                }

                return field;
            }
        }


        /// <summary>
        ///     Mod 的版本，不保证符合版本格式规范。
        /// </summary>
        public string Version
        {
            get
            {
                if (_Version is null)
                    Load();
                return _Version;
            }
            set
            {
                if (_Version is not null && _Version.RegexCheck(@"[0-9.\-]+"))
                    return;
                if (value?.ContainsF("version", true) == true)
                    value = "version"; // 需要修改的标识
                _Version = value;
            }
        }


        /// <summary>
        ///     用于依赖检查的 ModID。
        /// </summary>
        public string ModId
        {
            get
            {
                if (_ModId is null)
                    Load();
                return _ModId;
            }
            set
            {
                if (value is null)
                    return;
                value = value.RegexSeek(RegexPatterns.ModIdMatch);
                if (value is null || value.Length <= 1 || (ModBase.Val(value).ToString() ?? "") == (value ?? ""))
                    return;
                if (value.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("modid", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!possibleModId.Contains(value))
                    possibleModId.Add(value);
                if (_ModId is null)
                    _ModId = value;
            }
        }


        /// <summary>
        ///     Mod 的主页。
        /// </summary>
        public string Url
        {
            get
            {
                if (field is null)
                    Load();
                return field;
            }
            set
            {
                if (field is null && value is not null && value.StartsWithF("http")) field = value;
            }
        }


        /// <summary>
        ///     Mod 的作者列表。
        /// </summary>
        public string Authors
        {
            get
            {
                if (_Authors is null)
                    Load();
                return _Authors;
            }
            set
            {
                if (_Authors is null && !string.IsNullOrWhiteSpace(value)) _Authors = value;
            }
        }


        /// <summary>
        ///     Litematic 文件的创建时间戳。
        /// </summary>
        public long? LitematicTimeCreated
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicTimeCreated;
            }
        }


        /// <summary>
        ///     Litematic 文件的修改时间戳。
        /// </summary>
        public long? LitematicTimeModified
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicTimeModified;
            }
        }


        /// <summary>
        ///     Schem 读取到的原始名称。
        /// </summary>
        public string SchemOriginalName
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _schemOriginalName;
            }
        }


        /// <summary>
        ///     Litematic 读取到的原始名称。
        /// </summary>
        public string LitematicOriginalName
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicOriginalName;
            }
        }


        /// <summary>
        ///     Litematic 文件的版本。
        /// </summary>
        public int? LitematicVersion
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicVersion;
            }
        }


        /// <summary>
        ///     Litematic 文件的包围盒大小。
        /// </summary>
        public string LitematicEnclosingSize
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicEnclosingSize;
            }
        }


        /// <summary>
        ///     Litematic 文件的区域数量。
        /// </summary>
        public int? LitematicRegionCount
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicRegionCount;
            }
        }


        /// <summary>
        ///     Litematic 文件的总方块数。
        /// </summary>
        public int? LitematicTotalBlocks
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicTotalBlocks;
            }
        }


        /// <summary>
        ///     Litematic 文件的总体积。
        /// </summary>
        public int? LitematicTotalVolume
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _litematicTotalVolume;
            }
        }


        /// <summary>
        ///     原版结构文件的游戏版本。
        /// </summary>
        public string? StructureGameVersion
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _structureGameVersion;
            }
        }


        /// <summary>
        ///     原版结构文件的数据版本。
        /// </summary>
        public int? StructureDataVersion
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _structureDataVersion;
            }
        }


        /// <summary>
        ///     原版结构文件的作者。
        /// </summary>
        public string StructureAuthor
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _structureAuthor;
            }
        }


        /// <summary>
        ///     Sponge Schematic 文件的版本。
        /// </summary>
        public int? SpongeVersion
        {
            get
            {
                LoadNbtDataIfNeeded();
                return _spongeVersion;
            }
        }


        /// <summary>
        ///     Mod 图标路径。
        /// </summary>
        public string Logo { get; set; }


        /// <summary>
        ///     依赖项，其中包括了 Minecraft 的版本要求。格式为 ModID - VersionRequirement，若无版本要求则为 Nothing。
        /// </summary>
        public Dictionary<string, string> Dependencies
        {
            get
            {
                Load();
                return _Dependencies;
            }
        }


        /// <summary>
        ///     依赖的原始版本约束（未经 Maven 括号规整），保留 Fabric/Quilt semver 谓词原貌，供
        ///     <see cref="McConstraintMatcher" /> 按加载器方言求值；key=ModId，无版本要求则为 null。
        ///     <see cref="Dependencies" /> 仍存规整值以兼容既有消费方。
        /// </summary>
        public Dictionary<string, string> DependencyRaw
        {
            get
            {
                Load();
                return _DependencyRaw;
            }
        }


        /// <summary>
        ///     全部依赖声明。同一 ModId 的不同范围、声明方或可选性不会被字典合并，供 JIJ 关系分析使用。
        /// </summary>
        public IReadOnlyList<EmbeddedDependency> DependencyDeclarations
        {
            get
            {
                Load();
                return _DependencyDeclarations;
            }
        }


        /// <summary>
        ///     本文件额外提供的 Mod id 别名：multi-mod jar 的兄弟 <c>[[mods]]</c> id 与 Fabric <c>provides</c>。
        ///     依赖索引把它们当作与主 ModId 同源、同版本的 provider，避免依赖别名时误报缺失。
        /// </summary>
        public HashSet<string> ProvidedIds
        {
            get
            {
                Load();
                return _ProvidedIds;
            }
        }


        /// <summary>multi-mod JAR 中兄弟 ModId 各自声明的版本；Fabric provides 未指定版本时回退主版本。</summary>
        public IReadOnlyDictionary<string, string> ProvidedVersions
        {
            get
            {
                Load();
                return _ProvidedVersions;
            }
        }


        /// <summary>全部冲突声明；同一目标的不同范围与严重级别分别保留。</summary>
        public IReadOnlyList<EmbeddedConflict> ConflictDeclarations
        {
            get
            {
                Load();
                return _ConflictDeclarations;
            }
        }


        private void _AddConflict(string modID, string versionRange, bool hard, string declarerId = null)
        {
            if (modID is null || modID.Length < 2) return;
            modID = modID.ToLower();
            if (ModDependencyIds.IsPlatform(modID)) return; // 不报"与加载器/minecraft 冲突"
            if (!string.IsNullOrWhiteSpace(versionRange) && versionRange.Contains("$")) return;
            var raw = string.IsNullOrWhiteSpace(versionRange) ? null : versionRange;
            if (!_ConflictDeclarations.Any(c =>
                    string.Equals(c.Target, modID, StringComparison.OrdinalIgnoreCase) && c.Raw == raw &&
                    c.Hard == hard && string.Equals(c.DeclarerId, declarerId, StringComparison.OrdinalIgnoreCase)))
                _ConflictDeclarations.Add(new EmbeddedConflict
                    { DeclarerId = declarerId, Target = modID, Raw = raw, Hard = hard });
        }


        private void AddDependency(string modID, string versionRequirement = null, bool optional = false)
        {
            // 确保信息正确
            if (modID is null || modID.Length < 2)
                return;
            modID = modID.ToLower();
            if (ModDependencyIds.IsLoaderId(modID)) // 加载器/平台伪依赖不收录（minecraft 除外，其版本另有用途）
                return;
            if (modID == "name" || (ModBase.Val(modID).ToString() ?? "") == (modID ?? ""))
                return; // 跳过 name 与纯数字 id
            // 原始约束：仅剔除空值与占位符，语法有效性交给 McConstraintMatcher；
            // 不能因"无 . 无 -"就丢掉 >=2 / [2,) 这类合法整数约束（否则任何 provider 版本都会被判满足）
            var raw = string.IsNullOrWhiteSpace(versionRequirement) || versionRequirement.Contains("$")
                ? null
                : versionRequirement;
            var hasRequired = _DependencyDeclarations.Any(d =>
                string.Equals(d.Id, modID, StringComparison.OrdinalIgnoreCase) && !d.Optional);
            if (!optional)
                _DependencyDeclarations.RemoveAll(d =>
                    string.Equals(d.Id, modID, StringComparison.OrdinalIgnoreCase) && d.Optional);
            if ((!optional || !hasRequired) && !_DependencyDeclarations.Any(d =>
                    string.Equals(d.Id, modID, StringComparison.OrdinalIgnoreCase) && d.Raw == raw &&
                    d.Optional == optional))
                _DependencyDeclarations.Add(new EmbeddedDependency { Id = modID, Raw = raw, Optional = optional });
            if (!_DependencyRaw.TryGetValue(modID, out var oldRaw) || oldRaw is null || !optional && !hasRequired)
                _DependencyRaw[modID] = raw;

            if (versionRequirement is null ||
                (!versionRequirement.Contains(".") && !versionRequirement.Contains("-")) ||
                versionRequirement.Contains("$"))
                versionRequirement = null;
            else if (!versionRequirement.StartsWithF("[") && !versionRequirement.StartsWithF("(") &&
                     !versionRequirement.EndsWithF("]") && !versionRequirement.EndsWithF(")"))
                versionRequirement = "[" + versionRequirement + ",)";
            // 向依赖项中添加
            if (_Dependencies.ContainsKey(modID))
            {
                if (_Dependencies[modID] is null)
                    _Dependencies[modID] = versionRequirement;
            }
            else
            {
                _Dependencies.Add(modID, versionRequirement);
            }
        }


        // 提取 dependencies[.<id>] = [ ... ] 的内联数组体。用引号感知的方括号平衡扫描而非懒惰正则，
        // 避免在 versionRange="[1.21.1]" 之类字符串内的 ] 处提前截断
        private static List<(string DeclarerId, string Body)> _ExtractInlineDepArrays(string text)
        {
            var result = new List<(string, string)>();
            foreach (Match m in Regex.Matches(text, @"dependencies[.\w""-]*\s*=\s*\[", RegexOptions.IgnoreCase))
            {
                var header = m.Value.Substring(0, m.Value.IndexOf('=')).Trim();
                var declarerId = header.Substring("dependencies".Length).Trim().TrimStart('.').Trim('"');
                if (declarerId.Length == 0) declarerId = null;
                var start = m.Index + m.Length;
                var depth = 1;
                var inStr = false;
                for (var i = start; i < text.Length; i++)
                {
                    var ch = text[i];
                    if (inStr)
                    {
                        if (ch == '"') inStr = false;
                        continue;
                    }

                    if (ch == '"') inStr = true;
                    else if (ch == '[') depth++;
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            result.Add((declarerId, text.Substring(start, i - start)));
                            break;
                        }
                    }
                }
            }

            return result;
        }

}
}
