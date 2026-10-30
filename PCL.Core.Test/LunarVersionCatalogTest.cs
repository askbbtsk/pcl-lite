using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 版本目录的缓存读写测试。
///     <para>
///         重点是 <c>isDefault</c> 这个标记能不能原样存取：它是「没有配置时用哪个版本」的
///         唯一依据，落盘时丢了就只能退化成「版本号最大的那个」（通常是最新的 drop）。
///     </para>
///     <para>
///         只测磁盘缓存这一段，不联网 —— 服务端那套 <c>versions[].subversions[]</c>
///         结构在 <c>LunarVersionCatalog</c> 里由另一个解析器负责，走网络才到得了。
///     </para>
/// </summary>
[TestClass]
public class LunarVersionCatalogTest
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl_lunar_catalog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "launcher-cache"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private void WriteCache(string json) =>
        File.WriteAllText(LunarVersionCatalog.CachePath(_root), json);

    [TestMethod]
    public void CacheKeepsOfficialDefaultFlag()
    {
        WriteCache("""
                   {
                     "fetchedAt": "2026-10-30T00:00:00.0000000+08:00",
                     "versions": [
                       {
                         "id": "26.3", "parentId": "26", "parentName": "The 2026 Game Drops",
                         "assetIndexId": "24", "assetIndexUrl": "", "assetIndexSha1": "",
                         "isDefault": true,
                         "modules": [
                           { "id": "fabric", "name": "Fabric", "description": "", "default": true, "sort": 1 }
                         ]
                       },
                       {
                         "id": "1.8.9", "parentId": "1.8", "parentName": "The Bountiful Update",
                         "assetIndexId": "1.8", "assetIndexUrl": "", "assetIndexSha1": "",
                         "isDefault": false,
                         "modules": [
                           { "id": "lunar", "name": "Lunar + OptiFine", "description": "", "default": true, "sort": 0 }
                         ]
                       }
                     ]
                   }
                   """);

        Assert.IsTrue(LunarVersionCatalog.TryLoadCache(_root));
        Assert.AreEqual(2, LunarVersionCatalog.Versions.Count);
        // 列表按版本号降序：26.3 在前
        Assert.AreEqual("26.3", LunarVersionCatalog.Versions[0].Id);
        Assert.AreEqual("26.3", LunarVersionCatalog.DefaultVersion?.Id);
        Assert.IsFalse(LunarVersionCatalog.Find("1.8.9")!.IsDefault);
        Assert.AreEqual("fabric", LunarVersionCatalog.Find("26.3")!.DefaultModule?.Id);
    }

    [TestMethod]
    public void OldCacheWithoutFlagHasNoDefault()
    {
        // 1.6.1 及以前写的缓存没有 isDefault 字段。读得回来就行，
        // DefaultVersion 为 null 时上层会退到 1.8.9，不会去选最新版
        WriteCache("""
                   {
                     "fetchedAt": "2026-10-30T00:00:00.0000000+08:00",
                     "versions": [
                       {
                         "id": "1.8.9", "parentId": "1.8", "parentName": "The Bountiful Update",
                         "assetIndexId": "1.8", "assetIndexUrl": "", "assetIndexSha1": "",
                         "modules": [
                           { "id": "lunar", "name": "Lunar + OptiFine", "description": "", "default": true, "sort": 0 }
                         ]
                       }
                     ]
                   }
                   """);

        Assert.IsTrue(LunarVersionCatalog.TryLoadCache(_root));
        Assert.AreEqual(1, LunarVersionCatalog.Versions.Count);
        Assert.IsNull(LunarVersionCatalog.DefaultVersion);
    }

    [TestMethod]
    public void CacheWithoutModulesIsRejected()
    {
        // 1.5.4 及以前的缓存没有 modules，界面上一个 loader 都选不了，
        // 必须当成无效让上层重新拉一次
        WriteCache("""
                   {
                     "fetchedAt": "2026-10-30T00:00:00.0000000+08:00",
                     "versions": [
                       {
                         "id": "1.8.9", "parentId": "1.8", "parentName": "The Bountiful Update",
                         "assetIndexId": "1.8", "assetIndexUrl": "", "assetIndexSha1": "",
                         "modules": []
                       }
                     ]
                   }
                   """);

        Assert.IsFalse(LunarVersionCatalog.TryLoadCache(_root));
    }
}
