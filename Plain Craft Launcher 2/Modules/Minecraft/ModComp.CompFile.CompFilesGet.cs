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

    /// <summary>
    ///     获取某个工程下的全部文件列表。
    ///     必须在工作线程执行，失败会抛出异常。
    /// </summary>
    public static List<CompFile> CompFilesGet(string projectId, bool fromCurseForge)
    {
        // 1. 获取工程对象（使用 TryGetValue 提高效率并防止并发异常）
        CompProject targetProject = null;
        if (!compProjectCache.TryGetValue(projectId, out targetProject))
        {
            var url = fromCurseForge
                ? $"https://api.curseforge.com/v1/mods/{projectId}"
                : $"https://api.modrinth.com/v2/project/{projectId}";
            if (fromCurseForge)
            {
                var json = ModDownload.DlModRequest<JsonObject>(url);
                targetProject = new CompProject((JsonObject)json["data"]);
            }
            else
            {
                targetProject = new CompProject(ModDownload.DlModRequest<JsonObject>(url));
            }
            // 假设 CompProject 构造函数内已处理缓存，否则此处应添加缓存逻辑
        }

        // 2. 获取并缓存文件列表
        if (!compFilesCache.ContainsKey(projectId))
        {
            ModBase.Log("[Comp] 开始获取文件列表：" + projectId);
            JsonArray resultJsonArray;
            if (fromCurseForge)
            {
                // 注意：若 pageSize=10000 失效，需考虑分页逻辑
                var response = ModDownload.DlModRequest<JsonObject>(
                    $"https://api.curseforge.com/v1/mods/{projectId}/files?pageSize=10000"
                );

                resultJsonArray = (JsonArray)response["data"];
            }
            else
            {
                resultJsonArray =
                    ModDownload.DlModRequest<JsonArray>($"https://api.modrinth.com/v2/project/{projectId}/version?include_changelog=false");
            }

            compFilesCache[projectId] = resultJsonArray.Select(a => new CompFile((JsonObject)a, targetProject.Type))
                .Where(a => a.Available).GroupBy(a => a.Id).Select(g => g.First())
                .ToList(); // 使用 GroupBy 实现更高效的 Distinct
        }

        var currentFiles = compFilesCache[projectId];

        // 3. 提取所有需要获取信息的前置 ID（合并必要和可选）
        var allRawDeps = currentFiles.SelectMany(f => f.RawDependencies.Concat(f.RawOptionalDependencies)).Distinct()
            .ToList();
        var undoneDeps = allRawDeps.Where(id => !compProjectCache.ContainsKey(id)).ToList();

        // 4. 批量请求缺失的前置工程信息
        if (undoneDeps.Any())
        {
            ModBase.Log($"[Comp] {projectId} 需要补全信息的依赖项共 {undoneDeps.Count} 个");
            JsonArray projects;
            if (fromCurseForge)
            {
                // 1. 获取响应并转为 JsonObject
                var response = ModDownload.DlModRequest<JsonObject>(
                    "https://api.curseforge.com/v1/mods",
                    "POST",
                    "{\"modIds\": [" + string.Join(",", undoneDeps) + "]}",
                    "application/json"
                );

                // 2. 提取 data 数组
                projects = (JsonArray)response["data"];
            }
            else
            {
                projects = ModDownload.DlModRequest<JsonArray>(
                    $"https://api.modrinth.com/v2/projects?ids=[\"{undoneDeps.Join("\",\"")}\"]");
            }

            foreach (var project in projects)
                new CompProject((JsonObject)project);
        }

        // 5. 建立文件与依赖工程的关联映射
        // 优化：预先筛选出存在于缓存中的依赖工程，避免在多层循环中重复查询字典
        var availableDeps = allRawDeps.Where(id => compProjectCache.ContainsKey(id) && (id ?? "") != (projectId ?? ""))
            .Select(id => compProjectCache[id]).ToList();

        foreach (var file in currentFiles)
        foreach (var dep in availableDeps)
        {
            // 处理必要依赖
            if (file.RawDependencies.Contains(dep.Id))
                if (!file.Dependencies.Contains(dep.Id))
                    file.Dependencies.Add(dep.Id);

            // 处理可选依赖
            if (file.RawOptionalDependencies.Contains(dep.Id))
                if (!file.OptionalDependencies.Contains(dep.Id))
                    file.OptionalDependencies.Add(dep.Id);
        }

        return compFilesCache[projectId];
    }


    public static string CompFileNameGet(CompProject proj, CompFile file)
    {
        string fileName;
        if ((proj.TranslatedName ?? "") == (proj.RawName ?? ""))
        {
            fileName = file.FileName;
        }
        else
        {
            var chineseName = proj.TranslatedName.BeforeFirst(" (").BeforeFirst(" - ").Replace(@"\", "＼")
                .Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜").Replace(">", "＞")
                .Replace("*", "＊").Replace("?", "？").Replace("\"", "").Replace("： ", "：");
            fileName = Config.Download.Comp.NameFormatV2 switch
            {
                0 => $"【{chineseName}】{file.FileName}",
                1 => $"[{chineseName}] {file.FileName}",
                2 => $"{chineseName}-{file.FileName}",
                3 => $"{file.FileName}-{chineseName}",
                _ => file.FileName
            };
        }

        if (file.Type == CompType.Mod)
            fileName = fileName.Replace("~", "-"); // ~ 会导致 Mixin 加载失败
        return CompFileNameSanitize(fileName);
    }


    public static string CompFileNameSanitize(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "download";

        var sanitized = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            sanitized.Append(c switch
            {
                '\\' => '＼',
                '/' => '／',
                ':' => '：',
                '*' => '＊',
                '?' => '？',
                '"' => '＂',
                '<' => '＜',
                '>' => '＞',
                '|' => '｜',
                _ when char.IsControl(c) => '_',
                _ => c
            });
        }

        var result = sanitized.ToString().Trim();
        return result is "" or "." or ".." ? "download" : result;
    }

}
