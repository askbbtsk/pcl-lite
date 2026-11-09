using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Hash;
using PCL.Network;
using PCL.Network.Loaders;
using ProtoBuf;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public static partial class ModComp
{



    private static readonly Lazy<string> _dbInitializer = new(InitializeModDbAndGetConnectionString);



    // 类定义

    public partial class CompProject
    {
        /// <summary>
        ///     CurseForge 文件列表的数字 ID。Modrinth 工程的此项无效。
        /// </summary>
        public readonly List<int> CurseForgeFileIds;

        /// <summary>
        ///     英文描述。
        /// </summary>
        public readonly string Description;

        /// <summary>
        ///     下载量计数。注意，该计数仅为一个来源，无法反应两边加起来的下载量！
        /// </summary>
        public readonly int DownloadCount;

        /// <summary>
        ///     支持的 Drop 编号，从高到低排序，不为 Nothing。
        ///     例如：261（26.1.x）、180（1.18.x）。
        /// </summary>
        public readonly List<int> Drops;

        // 源信息

        /// <summary>
        ///     该工程信息来自 CurseForge 还是 Modrinth。
        /// </summary>
        public readonly bool FromCurseForge;

        /// <summary>
        ///     CurseForge 工程的数字 ID。Modrinth 工程的乱码 ID。
        /// </summary>
        public readonly string Id;

        /// <summary>
        ///     最后一次更新的时间。可能为 Nothing。
        /// </summary>
        public readonly DateTime? LastUpdate;

        /// <summary>
        ///     支持的 Mod 加载器列表。可能为空。
        /// </summary>
        public readonly List<CompLoaderType> ModLoaders;

        // 描述性信息

        /// <summary>
        ///     原始的英文名称。
        /// </summary>
        public readonly string RawName;

        /// <summary>
        ///     工程的短名。例如 technical-enchant。
        /// </summary>
        public readonly string Slug;

        /// <summary>
        ///     描述性标签的内容。已转换为中文。
        /// </summary>
        public readonly List<string> Tags;

        /// <summary>
        ///     工程的种类。
        ///     由于 Modrinth 混合使用 Mod 和数据包，结果不一定准确。
        /// </summary>
        public readonly CompType Type;

        /// <summary>
        ///     来源网站的工程页面网址。确保格式一定标准。
        ///     CurseForge：https://www.curseforge.com/minecraft/mc-mods/jei
        ///     Modrinth：https://modrinth.com/mod/technical-enchant
        /// </summary>
        public readonly string Website;

        private CompDatabaseEntry _DatabaseEntry;

        // 数据库信息

        private bool loadedDatabase;

        /// <summary>
        ///     Logo 图片的下载地址。
        ///     若为 Nothing 则没有，保证不为空字符串。
        /// </summary>
        public string LogoUrl;

        // 实例化

        /// <summary>
        ///     从工程 Json 中初始化实例。若出错会抛出异常。
        /// </summary>
        public CompProject(JsonObject data)
        {
            var result = data.ContainsKey("Tags")
                ? _BuildFromCompJson(data)
                : data.ContainsKey("summary")
                    ? _BuildFromCurseForge(data)
                    : _BuildFromModrinth(data);

            if (!data.ContainsKey("Tags"))
            {
                if (result.Tags.Count == 0)
                    result.Tags.Add(Lang.Text("Download.Comp.Category.Other"));

                result.Tags = result.Tags.Distinct().ToList();
                result.Tags.Sort();

                result.ModLoaders = result.ModLoaders
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();
            }

            FromCurseForge = result.FromCurseForge;
            Type = result.Type;
            Slug = result.Slug;
            Id = result.Id;
            CurseForgeFileIds = result.CurseForgeFileIds;
            RawName = result.RawName;
            Description = result.Description;
            Website = result.Website;
            LastUpdate = result.LastUpdate;
            DownloadCount = result.DownloadCount;
            ModLoaders = result.ModLoaders;
            Tags = result.Tags;
            LogoUrl = result.LogoUrl;
            Drops = result.Drops;

            // 保存缓存
            compProjectCache[Id] = this;
        }

        private static readonly Dictionary<string, CompLoaderType> modrinthLoaderTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["forge"] = CompLoaderType.Forge,
                ["fabric"] = CompLoaderType.Fabric,
                ["quilt"] = CompLoaderType.Quilt,
                ["neoforge"] = CompLoaderType.NeoForge
            };

        private static readonly Dictionary<int, string> curseForgeCategoryLangKeys =
            new()
            {
                // Mod
                [406] = "Download.Comp.Category.WorldGen",
                [407] = "Download.Comp.Category.Biomes",
                [410] = "Download.Comp.Category.Dimensions",
                [408] = "Download.Comp.Category.OresResources",
                [409] = "Download.Comp.Category.Structures",
                [412] = "Download.Comp.Category.Technology",
                [415] = "Download.Comp.Category.PipesLogistics",
                [4843] = "Download.Comp.Category.Automation",
                [417] = "Download.Comp.Category.Energy",
                [4558] = "Download.Comp.Category.Redstone",
                [436] = "Download.Comp.Category.FoodCooking",
                [416] = "Download.Comp.Category.Farming",
                [414] = "Download.Comp.Category.Transportation",
                [420] = "Download.Comp.Category.Storage",
                [419] = "Download.Comp.Category.Magic",
                [422] = "Download.Comp.Category.Adventure",
                [424] = "Download.Comp.Category.Decoration",
                [411] = "Download.Comp.Category.Mobs",
                [434] = "Download.Comp.Category.Equipment",
                [6814] = "Download.Comp.Category.Optimization",
                [9026] = "Download.Comp.Category.Creative",
                [423] = "Download.Comp.Category.Display",
                [435] = "Download.Comp.Category.Server",
                [5191] = "Download.Comp.Category.Tweaks",
                [421] = "Download.Comp.Category.Library",

                // 整合包
                [4484] = "Download.Comp.Category.Multiplayer",
                [4479] = "Download.Comp.Category.Modpack.Hardcore",
                [4483] = "Download.Comp.Category.Combat",
                [4478] = "Download.Comp.Category.Modpack.Quests",
                [4472] = "Download.Comp.Category.Technology",
                [4473] = "Download.Comp.Category.Magic",
                [4475] = "Download.Comp.Category.Adventure",
                [4476] = "Download.Comp.Category.Modpack.Exploration",
                [4477] = "Download.Comp.Category.Modpack.MiniGame",
                [4471] = "Download.Comp.Category.Modpack.SciFi",
                [4736] = "Download.Comp.Category.Modpack.Skyblock",
                [5128] = "Download.Comp.Category.Modpack.VanillaPlus",
                [4487] = "Download.Comp.Category.Modpack.Ftb",
                [4480] = "Download.Comp.Category.Modpack.MapBased",
                [4481] = "Download.Comp.Category.Modpack.SmallLight",
                [4482] = "Download.Comp.Category.Modpack.ExtraLarge",

                // 资源包
                [403] = "Download.Comp.Category.VanillaLike",
                [400] = "Download.Comp.Category.Realistic",
                [401] = "Download.Comp.Category.Modern",
                [402] = "Download.Comp.Category.Medieval",
                [399] = "Download.Comp.Category.Steampunk",
                [5244] = "Download.Comp.Category.Fonts",
                [404] = "Download.Comp.Category.Animated",
                [4465] = "Download.Comp.Category.ModSupport",
                [393] = "Download.Comp.Category.ResourcePack.Resolution16x",
                [394] = "Download.Comp.Category.ResourcePack.Resolution32x",
                [395] = "Download.Comp.Category.ResourcePack.Resolution64x",
                [396] = "Download.Comp.Category.ResourcePack.Resolution128x",
                [397] = "Download.Comp.Category.ResourcePack.Resolution256x",
                [398] = "Download.Comp.Category.ResourcePack.Resolution512xOrHigher",
                [5193] = "Download.Comp.Type.DataPack", // 有这个 Tag 的项会从资源包请求中被移除

                // 光影包
                [6553] = "Download.Comp.Category.Realistic",
                [6554] = "Download.Comp.Category.Fantasy",
                [6555] = "Download.Comp.Category.VanillaLike",

                // 数据包
                [6948] = "Download.Comp.Category.Adventure",
                [6949] = "Download.Comp.Category.DataPack.Fantasy",
                [6950] = "Download.Comp.Category.Library",
                [6952] = "Download.Comp.Category.Magic",
                [6946] = "Download.Comp.Category.Mod.ModRelated",
                [6951] = "Download.Comp.Category.Technology",
                [6953] = "Download.Comp.Category.Utility",

                // 世界
                [248] = "Download.Comp.Category.Adventure",
                [249] = "Download.Comp.Category.World.Creative",
                [250] = "Download.Comp.Category.Modpack.MiniGame",
                [251] = "Download.Comp.Category.World.Parkour",
                [252] = "Download.Comp.Category.World.Puzzle",
                [253] = "Download.Comp.Category.World.Survival",
                [4464] = "Download.Comp.Category.World.ModWorld"
            };

        private static readonly Dictionary<string, string> resourcePackHiddenCategoryLangKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["decoration"] = "Download.Comp.Category.Decoration",
                ["mobs"] = "Download.Comp.Category.Mobs",
                ["equipment"] = "Download.Comp.Category.Equipment"
            };

        private static readonly Dictionary<string, string> modrinthCategoryLangKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // 共用
                ["technology"] = "Download.Comp.Category.Technology",
                ["magic"] = "Download.Comp.Category.Magic",
                ["adventure"] = "Download.Comp.Category.Adventure",
                ["utility"] = "Download.Comp.Category.Utility",
                ["optimization"] = "Download.Comp.Category.Optimization",
                ["vanilla-like"] = "Download.Comp.Category.VanillaLike",
                ["realistic"] = "Download.Comp.Category.Realistic",

                // Mod / 数据包
                ["worldgen"] = "Download.Comp.Category.WorldGen",
                ["food"] = "Download.Comp.Category.FoodCooking",
                ["game-mechanics"] = "Download.Comp.Category.GameMechanics",
                ["transportation"] = "Download.Comp.Category.Transportation",
                ["storage"] = "Download.Comp.Category.Storage",
                ["social"] = "Download.Comp.Category.Server",
                ["library"] = "Download.Comp.Category.Library",

                // 整合包
                ["multiplayer"] = "Download.Comp.Category.Multiplayer",
                ["challenging"] = "Download.Comp.Category.Modpack.Hardcore",
                ["combat"] = "Download.Comp.Category.Combat",
                ["quests"] = "Download.Comp.Category.Modpack.Quests",
                ["kitchen-sink"] = "Download.Comp.Category.Modpack.KitchenSink",
                ["lightweight"] = "Download.Comp.Category.Modpack.SmallLight",

                // 资源包
                ["simplistic"] = "Download.Comp.Category.Simplistic",
                ["tweaks"] = "Download.Comp.Category.Tweaks",
                ["8x-"] = "Download.Comp.Category.ResourcePack.Resolution8xOrLower",
                ["16x"] = "Download.Comp.Category.ResourcePack.Resolution16x",
                ["32x"] = "Download.Comp.Category.ResourcePack.Resolution32x",
                ["48x"] = "Download.Comp.Category.ResourcePack.Resolution48x",
                ["64x"] = "Download.Comp.Category.ResourcePack.Resolution64x",
                ["128x"] = "Download.Comp.Category.ResourcePack.Resolution128x",
                ["256x"] = "Download.Comp.Category.ResourcePack.Resolution256x",
                ["512x+"] = "Download.Comp.Category.ResourcePack.Resolution512xOrHigher",
                ["audio"] = "Download.Comp.Category.Audio",
                ["fonts"] = "Download.Comp.Category.Fonts",
                ["models"] = "Download.Comp.Category.Models",
                ["gui"] = "Download.Comp.Category.Gui",
                ["locale"] = "Download.Comp.Category.Locale",
                ["core-shaders"] = "Download.Comp.Category.CoreShaders",
                ["modded"] = "Download.Comp.Category.ModSupport",

                // 光影包
                ["fantasy"] = "Download.Comp.Category.Fantasy",
                ["semi-realistic"] = "Download.Comp.Category.SemiRealistic",
                ["cartoon"] = "Download.Comp.Category.Cartoon",
                // 暂时不添加性能负荷 Tag：
                // potato / low / medium / high
                ["colored-lighting"] = "Download.Comp.Category.ColoredLighting",
                ["path-tracing"] = "Download.Comp.Category.PathTracing",
                ["pbr"] = "Download.Comp.Category.Pbr",
                ["reflections"] = "Download.Comp.Category.Reflections",
                ["iris"] = "Download.Comp.Category.Iris",
                ["optifine"] = "Download.Comp.Category.Optifine",
                ["vanilla"] = "Download.Comp.Filter.Loader.VanillaAvailable"
            };
    }

    // 实际的获取

    private const int compPageSize = 40;

    /// <summary>
    ///     已知工程信息的缓存。
    /// </summary>
    public static ConcurrentDictionary<string, CompProject> compProjectCache = new();

    /// <summary>
    /// CurseForge 分类 URL 段 → classId 映射。提为 static 避免每次解析重新分配。
    /// </summary>
    private static readonly Dictionary<string, string> curseForgeCategoryClassIds = new()
    {
        { "mc-mods", "6" },
        { "modpacks", "4471" },
        { "texture-packs", "12" },
        { "shaders", "6552" }
    };

    // 获取

    /// <summary>
    ///     已知文件信息的缓存。
    /// </summary>
    public static ConcurrentDictionary<string, List<CompFile>> compFilesCache = new();

}
