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
        ///     标记为已加载。用于内嵌（Jar-in-Jar）子项——其元数据已由 <see cref="ModJarInJar" /> 通过
        ///     LookupMetadata 从已打开的嵌套流读入，虚拟路径不是真实文件，须避免属性 getter 再触发 Load() 清空元数据。
        /// </summary>
        internal void MarkLoaded() => isLoaded = true;


        internal void SetJijMetadata(string name, string modId, string version)
        {
            _Name = name;
            _ModId = modId;
            _Version = version;
            isLoaded = true;
        }


        internal void SetJijDependencies(List<EmbeddedDependency> declarations)
        {
            _Dependencies = new Dictionary<string, string>();
            _DependencyRaw.Clear();
            _DependencyDeclarations.Clear();
            if (declarations is null) return;
            foreach (var declaration in declarations)
            {
                if (declaration?.Id is null) continue;
                AddDependency(declaration.Id, declaration.Raw, declaration.Optional);
            }
        }


        internal void SetJijConflicts(List<EmbeddedConflict> conflicts, List<string> provided,
            Dictionary<string, string> providedVersions)
        {
            _ConflictDeclarations.Clear();
            if (conflicts is not null)
                foreach (var c in conflicts)
                    if (!string.IsNullOrEmpty(c?.Target))
                    {
                        _ConflictDeclarations.Add(new EmbeddedConflict
                            { DeclarerId = c.DeclarerId, Target = c.Target, Raw = c.Raw, Hard = c.Hard });
                    }
            _ProvidedIds.Clear();
            if (provided is not null) _ProvidedIds.UnionWith(provided);
            _ProvidedVersions.Clear();
            if (providedVersions is not null)
                foreach (var kv in providedVersions) _ProvidedVersions[kv.Key] = kv.Value;
        }


        /// <summary>
        ///     从同一物理文件的已解析实体复制解析结果。启/禁用仅重命名、文件内容未变，
        ///     替换实体后无需在 UI 线程重新解压解析。来源未加载时不做任何事。
        /// </summary>
        internal void CopyLoadedStateFrom(LocalCompFile other)
        {
            if (other is null || !other.isLoaded) return;
            lock (_loadLock)
            {
                _Name = other._Name;
                _Description = other._Description;
                _Version = other._Version;
                _ModId = other._ModId;
                possibleModId = new List<string>(other.possibleModId);
                _Dependencies = new Dictionary<string, string>(other._Dependencies);
                _DependencyRaw.Clear();
                foreach (var kv in other._DependencyRaw) _DependencyRaw[kv.Key] = kv.Value;
                _DependencyDeclarations.Clear();
                foreach (var d in other._DependencyDeclarations)
                    _DependencyDeclarations.Add(new EmbeddedDependency
                        { Id = d.Id, Raw = d.Raw, Optional = d.Optional });
                _ProvidedIds.Clear();
                _ProvidedIds.UnionWith(other._ProvidedIds);
                _ProvidedVersions.Clear();
                foreach (var kv in other._ProvidedVersions) _ProvidedVersions[kv.Key] = kv.Value;
                _ConflictDeclarations.Clear();
                foreach (var c in other._ConflictDeclarations)
                    _ConflictDeclarations.Add(new EmbeddedConflict
                        { DeclarerId = c.DeclarerId, Target = c.Target, Raw = c.Raw, Hard = c.Hard });
                _EmbeddedMods = other._EmbeddedMods;
                PreferredLoader = other.PreferredLoader;
                JijLoader = other.JijLoader;
                JijTargetMcVersion = other.JijTargetMcVersion;
                JijIdentifier = other.JijIdentifier;
                JijVersionRange = other.JijVersionRange;
                JijArtifactVersion = other.JijArtifactVersion;
                DetectedLoader = other.DetectedLoader;
                DeclaredLoaders = other.DeclaredLoaders;
                Logo = other.Logo;
                Url = other.Url;
                Authors = other.Authors;
                _FileUnavailableReason = other._FileUnavailableReason;
                isLoaded = true;
            }
        }


        /// <summary>
        ///     Mod 文件是否可被正常读取。
        /// </summary>
        public bool IsFileAvailable
        {
            get
            {
                Load();
                return FileUnavailableReason is null;
            }
        }


        /// <summary>
        ///     Mod 文件出错的原因。若无错误，则为 Nothing。
        /// </summary>
        public Exception FileUnavailableReason
        {
            get
            {
                Load();
                return _FileUnavailableReason;
            }
        }

}
}
