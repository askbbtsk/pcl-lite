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

using static PCL.ModComp;
using static PCL.ModComp.CompRequest;

namespace PCL;

internal static class ResourceProjectRequestService
{
        /// <summary>
        ///     通过项目 Id 判断是否来自 CurseForge
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static bool IsFromCurseForge(string id)
        {
            var res = 0;
            return int.TryParse(id, out res); // CurseForge 数字 ID Modrinth 乱序 ID
        }


        /// <summary>
        ///     通过一堆 ID 从 Modrinth 那获取项目信息
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public static async Task<List<CompProject>> GetListByIdsFromModrinthAsync(List<string> ids)
        {
            var res = new List<CompProject>();
            try
            {
                await Task.Run(() =>
                {
                    var rawProjectsData =
                        ModDownload.DlModRequest<JsonArray>($"https://api.modrinth.com/v2/projects?ids=[\"{ids.Join("\",\"")}\"]");
                    foreach (var rawData in (IEnumerable)rawProjectsData)
                        res.Add(new CompProject((JsonObject)rawData));
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "从 Modrinth 获取数据失败");
            }

            return res;
        }


        /// <summary>
        ///     通过一堆 ID 从 CurseForge 那获取项目信息
        /// </summary>
        /// <param name="Ids"></param>
        /// <returns></returns>
        public static async Task<List<CompProject>> GetListByIdsFromCurseforgeAsync(List<string> ids)
        {
            var res = new List<CompProject>();
            try
            {
                // 使用 Task.Run 将同步的 DlModRequest 包装为异步
                await Task.Run(() =>
                {
                    // 构建请求 Body，建议使用 string.Join
                    var jsonBody = "{\"modIds\": [" + string.Join(",", ids) + "]}";

                    // DlModRequest 返回 object，先强转 JsonObject，再获取 "data" 并强转为 JsonArray
                    var response = ModDownload.DlModRequest<JsonObject>(
                        "https://api.curseforge.com/v1/mods",
                        "POST",
                        jsonBody,
                        "application/json"
                    );

                    var rawProjectsData = (JsonArray)response["data"];

                    // 2. 使用 LINQ 快速转换并填充列表
                    if (rawProjectsData is not null)
                    {
                        var projectList = rawProjectsData
                            .Cast<JsonObject>()
                            .Select(data => new CompProject(data))
                            .ToList();

                        res.AddRange(projectList);
                    }
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "Failed to get project data from CurseForge");
            }

            return res;
        }


        public static List<CompProject> GetCompProjectsByIds(List<string> input)
        {
            return GetCompProjectsByIdsAsync(input).GetAwaiter().GetResult();
        }


        public static async Task<List<CompProject>> GetCompProjectsByIdsAsync(List<string> input)
        {
            if (input?.Any() == false)
                return new List<CompProject>();

            var modrinthIds = new List<string>();
            var curseForgeIds = new List<string>();
            foreach (var id in input)
                if (IsFromCurseForge(id))
                    curseForgeIds.Add(id);
                else
                    modrinthIds.Add(id);

            var tasks = new List<Task<List<CompProject>>>();
            if (curseForgeIds.Any()) tasks.Add(GetListByIdsFromCurseforgeAsync(curseForgeIds));
            if (modrinthIds.Any()) tasks.Add(GetListByIdsFromModrinthAsync(modrinthIds));

            await Task.WhenAll(tasks.ToArray());
            var result = new List<CompProject>();
            foreach (var task in tasks)
                result.AddRange(task.Result);

            return result;
        }

}
