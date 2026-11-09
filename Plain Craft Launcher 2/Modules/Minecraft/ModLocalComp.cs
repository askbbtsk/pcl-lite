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
    private const int localModCacheVersion = 7;

    private static readonly Lazy<HashCache> _hashCache = new(() =>
        new HashCache(ModBase.pathTemp + @"Cache\HashCache.db"));

    public partial class LocalCompFile
    {



        /// <summary>
        ///     资源的文件的地址。
        /// </summary>
        public readonly string path;

        public LocalCompFile(string path)
        {
            this.path = path ?? "";
        }

        /// <summary>
        ///     NBT数据是否已加载（用于延迟加载优化）。
        /// </summary>
        private bool _nbtDataLoaded;

        private string _Name;

        private string _Description;

        private string _Version;

        private string _ModId;

        /// <summary>
        ///     其他可能的 ModID。
        /// </summary>
        public List<string> possibleModId = new();

        private string _Authors;

        private long? _litematicTimeCreated;

        private long? _litematicTimeModified;

        private string _schemOriginalName;

        private string _litematicOriginalName;

        private int? _litematicVersion;

        private string _litematicEnclosingSize;

        private int? _litematicRegionCount;

        private int? _litematicTotalBlocks;

        private int? _litematicTotalVolume;

        private string? _structureGameVersion = null;

        private int? _structureDataVersion;

        private string _structureAuthor;

        private int? _spongeVersion;

        private Dictionary<string, string> _Dependencies = new();

        private readonly Dictionary<string, string> _DependencyRaw = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<EmbeddedDependency> _DependencyDeclarations = new();

        private readonly HashSet<string> _ProvidedIds = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _ProvidedVersions = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<EmbeddedConflict> _ConflictDeclarations = new();



        // 1. 进行文件可用性检查
        // 成功：继续第二步。
        // 失败：标记 FileUnavailableReason， 并停止后续加载。
        /// <summary>
        ///     是否已进行 Mod 文件的基础加载。（这包括第一步和第二步）
        /// </summary>
        // volatile：加载器线程写、UI 线程双检锁快路径无锁读，保证元数据发布顺序（弱内存模型下的可见性）
        private volatile bool isLoaded;

        private Exception _FileUnavailableReason;

        private readonly object _loadLock = new();

        private List<LocalCompFile> _EmbeddedMods = new();

        private long? _jijExpectedLastModified;
        private long? _jijExpectedSize;
        private volatile bool _jijPending;



        /// <summary>
        ///     当任何网络信息更新时触发。
        /// </summary>
        public event OnCompUpdateEventHandler? OnCompUpdate;

        /// <summary>
        ///     本地文件对应的联网文件信息。
        /// </summary>
        public CompFile compFile;

        /// <summary>
        ///     该 Mod 的更新日志网址。
        /// </summary>
        public List<string> changelogUrls = new();

        /// <summary>
        ///     所有网络信息是否已成功加载。
        /// </summary>
        public bool compLoaded;

        private uint? _CurseForgeHash;

    }

    // 加载资源列表
    public static LoaderTask<CompLocalLoaderData, List<LocalCompFile>> compResourceListLoader =
        new("Comp Resource List Loader", CompResourceListLoad);

    // 列表加载 Phase 1 期间置真：LoadCore 遇缓存未命中的内嵌解析直接延后（不解压内嵌 jar，不拖慢首屏）。
    // [ThreadStatic]：仅列表加载线程延后；UI 惰性 getter 在别的线程触发 Load 时仍即时解析该单项。
    [ThreadStatic] internal static bool DeferJijOnMiss;

    // 当前线程所属的一次实例扫描预算；后台解析、崩溃导出与同步补解析分别注册自己的上下文。
    [ThreadStatic] internal static ModJarInJar.ScanContext JijScanContext;

    // 后台内嵌解析（Phase 2）是否已全部完成；false 时级联反查可能遗漏内嵌依赖，禁用/删除前应提示。
    internal static volatile bool CompJijResolved = true;

    // 每次启动 Mod 列表解析递增。后台任务只有仍属于最新一轮时才可发布完成状态，
    // 避免旧实例/旧重载的 finally 把新一轮尚未完成的状态覆盖成 true。
    private static int _jijGeneration;

    // 联网加载 Mod 详情
    public static LoaderTask<CompLocalLoaderData, int> compUpdateDetailLoader =
        new("Comp List Detail Loader", CompUpdateDetailLoad);

    private static readonly Regex regexIsJarFile = new(@"\.jar(\.disabled)?$");
}
