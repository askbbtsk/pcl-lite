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

    private static string CompDBConnectionString => _dbInitializer.Value;


    private static string InitializeModDbAndGetConnectionString()
    {
        ModBase.Log("[DB] 解压 ModData (SQLite) 中");
        using (var compressedDbData = ModBase.GetResourceStream("Resources/mcmod.buf"))
        {
            using (var trueDbFile = new GZipStream(compressedDbData, CompressionMode.Decompress))
            {
                using (var ms = new MemoryStream())
                {
                    // 这里提取文件资源
                    trueDbFile.CopyTo(ms);
                    ms.Seek(0L, SeekOrigin.Begin);
                    var fileHash = ModBase.GetHexString(SHA1Provider.Instance.ComputeHash(ms));
                    var dbDir = Path.Combine(ModBase.pathTemp, "Cache");
                    var dbPath = Path.Combine(dbDir, $"ModData{fileHash}.sqlite");

                    if (File.Exists(dbPath) && !IsDatabaseValid(dbPath))
                    {
                        File.Delete(dbPath);
                    }

                    if (!File.Exists(dbPath))
                    {
                        ms.Seek(0L, SeekOrigin.Begin);
                        var entries = Serializer.Deserialize<List<CompDatabaseEntry>>(ms);

                        Directory.CreateDirectory(dbDir);

                        var tempPath = dbPath + ".tmp";
                        if (File.Exists(tempPath)) File.Delete(tempPath);

                        using (var buildDbConnection = new SqliteConnection($"Data Source=\"{tempPath}\";Pooling=False"))
                        {
                            buildDbConnection.Open();

                            // 不用事务的话构建会非常慢
                            using (var transaction = buildDbConnection.BeginTransaction())
                            {
                                buildDbConnection.Execute(@"
                                    CREATE TABLE ModTranslation (
                                        WikiId INTEGER,
                                        ChineseName TEXT,
                                        CurseForgeSlug TEXT,
                                        ModrinthSlug TEXT
                                    );
                                    CREATE INDEX idx_curseforge ON ModTranslation (CurseForgeSlug);
                                    CREATE INDEX idx_modrinth ON ModTranslation (ModrinthSlug);
                                    CREATE INDEX idx_chinesename ON ModTranslation (ChineseName);
                                ");

                                var insertSql =
                                    @"INSERT INTO ModTranslation (WikiId, ChineseName, CurseForgeSlug, ModrinthSlug) 
                                    VALUES (@WikiId, @ChineseName, @CurseForgeSlug, @ModrinthSlug)";

                                foreach (var entry in entries)
                                    buildDbConnection.Execute(insertSql, entry, transaction);

                                transaction.Commit();
                            }
                        }

                        // 构建完成的文件移入缓存位
                        File.Move(tempPath, dbPath, true);
                    }

                    return $"Data Source=\"{dbPath}\"";
                }
            }
        }
    }


    /// <summary>
    /// 验证 SQLite 数据库文件是否包含预期的表且非空
    /// </summary>
    private static bool IsDatabaseValid(string dbPath)
    {
        try
        {
            using (var conn = new SqliteConnection($"Data Source=\"{dbPath}\";Pooling=False;Mode=ReadOnly"))
            {
                conn.Open();
                // 检查表是否存在
                var tableCheck = conn.ExecuteScalar<int>(
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ModTranslation'");
                if (tableCheck == 0) return false;
                // 检查表中是否有数据
                var rowCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ModTranslation");
                return rowCount > 0;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "检查模组翻译数据库有效性失败");
            return false;
        }
    }


    private static SqliteConnection CompDB
    {
        get
        {
            var conn = new SqliteConnection(CompDBConnectionString);
            conn.Open();
            return conn;
        }
    }


    private static CompDatabaseEntry GetCompWikiEntryBySlug(string slug)
    {
        try
        {
            using (var conn = CompDB)
            {
                return conn.QueryFirstOrDefault<CompDatabaseEntry>(
                    "SELECT * FROM ModTranslation WHERE CurseForgeSlug = @s OR ModrinthSlug = @s LIMIT 1",
                    new { s = slug });
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "获取模组翻译信息失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Minecraft.Comp.Error.OperationFailed"));
            return null;
        }
    }


    [ProtoContract]
    private class CompDatabaseEntry
    {
        /// <summary>
        ///     McMod 的对应 ID。
        /// </summary>
        [ProtoMember(1)]
        public int WikiId { get; set; }

        /// <summary>
        ///     中文译名。空字符串代表没有翻译。
        /// </summary>
        [ProtoMember(2)]
        public string ChineseName { get; set; } = "";

        /// <summary>
        ///     CurseForge Slug（例如 advanced-solar-panels）。
        /// </summary>
        [ProtoMember(3)]
        public string CurseForgeSlug { get; set; }

        /// <summary>
        ///     Modrinth Slug（例如 advanced-solar-panels）。
        /// </summary>
        [ProtoMember(4)]
        public string ModrinthSlug { get; set; }

        public override string ToString()
        {
            return (CurseForgeSlug ?? "") + "&" + (ModrinthSlug ?? "") + "|" + WikiId + "|" + ChineseName;
        }
    }

}
