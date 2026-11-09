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
        ///     初始化所有数据。
        /// </summary>
        private void Init()
        {
            _Name = null;
            _Description = null;
            _Version = null;
            _ModId = null;
            possibleModId = new List<string>();
            _Dependencies = new Dictionary<string, string>();
            _DependencyRaw.Clear();
            _DependencyDeclarations.Clear();
            _ProvidedIds.Clear();
            _ProvidedVersions.Clear();
            _ConflictDeclarations.Clear();
            _EmbeddedMods = new List<LocalCompFile>();
            _jijExpectedLastModified = null;
            _jijExpectedSize = null;
            JijLoader = null;
            JijTargetMcVersion = null;
            JijIdentifier = null;
            JijVersionRange = null;
            JijArtifactVersion = null;
            DetectedLoader = null;
            isLoaded = false;
            _FileUnavailableReason = null;
        }


        /// <summary>
        ///     加载基本信息（不解析NBT数据）。
        /// </summary>
        public void LoadBasicInfo()
        {
            try
            {
                // 可用性检查
                if (IsFolder)
                {
                    // 文件夹项不需要进一步处理
                    isLoaded = true;
                    return;
                }

                if (!File.Exists(path))
                {
                    _FileUnavailableReason = new FileNotFoundException("未找到资源文件（" + path + ")");
                    isLoaded = true;
                    return;
                }

                // 对于原理图文件，只设置基本状态，不解析NBT数据
                if (path.EndsWithF(".litematic", true) || path.EndsWithF(".nbt", true) ||
                    path.EndsWithF(".schem", true) || path.EndsWithF(".schematic", true))
                {
                    _Name = ModBase.GetFileNameWithoutExtentionFromPath(path);
                    isLoaded = true;
                    return;
                }

                // 对于其他文件类型，正常加载
                Load();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"加载基本信息失败：{path}");
            }
        }


        /// <summary>
        ///     延迟加载NBT数据。
        /// </summary>
        public void LoadNbtDataIfNeeded()
        {
            try
            {
                // 如果已经加载过NBT数据，则跳过
                if (_nbtDataLoaded)
                    return;

                // 根据文件类型加载NBT数据
                if (path.EndsWithF(".litematic", true))
                    LoadLitematicNbtData();
                else if (path.EndsWithF(".nbt", true))
                    LoadStructureNbtData();
                else if (path.EndsWithF(".schem", true))
                    LoadSchemNbtData();
                else if (path.EndsWithF(".schematic", true)) LoadSchematicNbtData();

                _nbtDataLoaded = true;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"延迟加载NBT数据失败：{path}");
            }
        }


        /// <summary>
        ///     进行文件可用性检查与 .class 以外的信息获取。
        ///     加载器线程与 UI 线程的属性 getter 可能并发触发，须加锁防止同一文件被并行解析。
        /// </summary>
        public void Load(bool forceReload = false)
        {
            if (isLoaded && !forceReload)
                return;
            lock (_loadLock)
            {
                if (isLoaded && !forceReload)
                    return;
                LoadCore();
            }
        }


        private void LoadCore()
        {
            // 初始化
            Init();

            // 基础可用性检查
            if (path.Length < 2)
            {
                _FileUnavailableReason = new FileNotFoundException("错误的资源文件路径（" + (path ?? "null") + "）");
                isLoaded = true;
                return;
            }

            // 对于文件夹项，检查实际文件夹路径是否存在
            if (IsFolder)
            {
                if (!Directory.Exists(ActualPath))
                {
                    _FileUnavailableReason = new DirectoryNotFoundException("未找到文件夹（" + ActualPath + "）");
                    isLoaded = true;
                    return;
                }

                // 文件夹项不需要进一步处理
                isLoaded = true;
                return;
            }

            if (!File.Exists(path))
            {
                _FileUnavailableReason = new FileNotFoundException("未找到资源文件（" + path + "）");
                isLoaded = true;
                return;
            }

            // 对于投影文件，跳过 zip 解析
            if (path.EndsWithF(".litematic", true) || path.EndsWithF(".nbt", true) || path.EndsWithF(".schem", true) ||
                path.EndsWithF(".schematic", true))
            {
                try
                {
                    _Name = ModBase.GetFileNameWithoutExtentionFromPath(path);
                    // 根据文件类型加载数据
                    if (path.EndsWithF(".litematic", true))
                    {
                        LoadLitematicNbtData();
                    }
                    else if (path.EndsWithF(".schem", true) || path.EndsWithF(".schematic", true))
                    {
                        if (path.EndsWithF(".schem", true))
                            LoadSchemNbtData();
                        else
                            LoadSchematicNbtData();
                    }
                    else if (path.EndsWithF(".nbt", true))
                    {
                        LoadStructureNbtData();
                    }

                    _nbtDataLoaded = true;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "投影文件信息获取失败（" + path + "）", ModBase.LogLevel.Developer);
                    _FileUnavailableReason = ex;
                }

                isLoaded = true;
                return;
            }

            // 对于其他文件，尝试作为 Jar 文件打开
            ZipArchive jar = null;
            try
            {
                var fileInfo = new FileInfo(path);
                var jijLastModified = fileInfo.LastWriteTimeUtc.Ticks;
                var jijSize = fileInfo.Length;
                _jijExpectedLastModified = jijLastModified;
                _jijExpectedSize = jijSize;
                // 只读打开 + 宽共享：默认的 ReadWrite/FileShare.Read 会让并发解析同一文件的第二个打开者
                // （如崩溃导出线程与列表加载并发）抛 IOException，把好 Mod 误判为 Unavailable
                jar = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Write | FileShare.Delete));
                DetectedLoader = ModJarInJar.DetectLoader(jar, PreferredLoader);
                // 信息获取
                LookupMetadata(jar, DetectedLoader);
                // 列表批量加载（Phase 1）期间：缓存未命中的内嵌解析延后，返回 null 则标记待后台补解析
                var jijTree = ModJarInJar.ResolveCached(path, jar, out var jijComplete, DeferJijOnMiss,
                    jijLastModified, jijSize, JijScanContext, PreferredLoader);
                if (jijTree is not null) _EmbeddedMods = jijTree;
                _jijPending = jijTree is null || !jijComplete;
            }
            catch (UnauthorizedAccessException ex)
            {
                ModBase.Log(ex, "资源文件由于无权限无法打开（" + path + "）", ModBase.LogLevel.Developer);
                _FileUnavailableReason = new UnauthorizedAccessException("没有读取此文件的权限，请尝试右键以管理员身份运行 PCL", ex);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "资源文件无法打开（" + path + "）", ModBase.LogLevel.Developer);
                _FileUnavailableReason = ex;
            }
            finally
            {
                if (jar is not null)
                    jar.Dispose();
            }

            // 完成标记
            isLoaded = true;
        }


        /// <summary>
        ///     内嵌模组列表，由 <see cref="ModJarInJar" /> 解析填充。
        ///     惰性触发 Load：启/禁用后实体会被替换为未加载的新实例，直读字段会拿到空列表。
        /// </summary>
        public List<LocalCompFile> EmbeddedMods
        {
            get
            {
                Load();
                return _EmbeddedMods;
            }
            internal set => _EmbeddedMods = value;
        }


        /// <summary>本项的内嵌（Jar-in-Jar）树是否仍待后台补解析（列表首屏为不解压而延后的冷缓存项）。</summary>
        internal bool JijPending => _jijPending;


        /// <summary>后台（Phase 2）补解析被延后的内嵌树：重开 jar 递归解析并写回缓存。仅对 <see cref="JijPending" /> 项有效。</summary>
        internal bool ResolveJijNow()
        {
            lock (_loadLock)
            {
                if (!_jijPending) return true;
                ZipArchive jar = null;
                try
                {
                    var fileInfo = new FileInfo(path);
                    var jijLastModified = _jijExpectedLastModified ?? fileInfo.LastWriteTimeUtc.Ticks;
                    var jijSize = _jijExpectedSize ?? fileInfo.Length;
                    jar = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Write | FileShare.Delete));
                    var tree = ModJarInJar.ResolveCached(path, jar, out var complete, false,
                        jijLastModified, jijSize, JijScanContext, PreferredLoader);
                    if (tree is not null) _EmbeddedMods = tree;
                    if (tree is null || !complete) return false;
                    _jijPending = false;
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "后台补解析内嵌模组失败（" + path + "）", ModBase.LogLevel.Developer);
                    return false;
                }
                finally
                {
                    jar?.Dispose();
                }
            }
        }


        /// <summary>内嵌（Jar-in-Jar）子项声明的加载器（Fabric/Quilt/Forge/NeoForge）；仅内嵌项有值。</summary>
        public string JijLoader { get; internal set; }


        /// <summary>内嵌（Jar-in-Jar）子项声明的目标 Minecraft 版本范围；仅内嵌项有值。</summary>
        public string JijTargetMcVersion { get; internal set; }


        public string JijIdentifier { get; internal set; }


        public string JijVersionRange { get; internal set; }


        public string JijArtifactVersion { get; internal set; }


        internal string? PreferredLoader { get; set; }


        /// <summary>本 Mod 检出的加载器（Fabric/Quilt/Forge/NeoForge），供依赖版本约束按方言求值；null=未知。</summary>
        public string DetectedLoader { get; internal set; }

}
}
