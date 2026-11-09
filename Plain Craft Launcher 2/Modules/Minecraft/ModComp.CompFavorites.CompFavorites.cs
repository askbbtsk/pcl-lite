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


    public class CompFavorites
    {
        private static List<FavData> _FavoritesList;

        /// <summary>
        ///     收藏的工程列表
        /// </summary>
        public static List<FavData> FavoritesList
        {
            get
            {
                if (_FavoritesList is null)
                {
                    var rawData = States.Game.CompFavorites;
                    List<FavData> rawList = null;
                    // 尝试作为新格式解析
                    try
                    {
                        rawList = JsonSerializer.Deserialize<List<FavData>>(rawData, JsonCompat.SerializerOptions);
                    }
                    catch (Exception)
                    {
                        // 尝试作为旧格式（HashSet）迁移
                        try
                        {
                            var migrate = JsonSerializer.Deserialize<HashSet<string>>(rawData, JsonCompat.SerializerOptions);
                            if (migrate is not null) rawList = new List<FavData> { GetNewFav(Lang.Text("Download.Comp.Detail.Favorites.DefaultName"), migrate) };
                        }
                        catch (Exception)
                        {
                            // 两种都失败，使用默认
                        }
                    }

                    // 最终兜底：确保至少有一个收藏夹
                    if (rawList is null || rawList.Count == 0) rawList = new List<FavData> { GetNewFav(Lang.Text("Download.Comp.Detail.Favorites.DefaultName"), null) };
                    _FavoritesList = rawList;
                    Save();
                }

                return _FavoritesList;
            }
            set
            {
                _FavoritesList = value;
                foreach (var item in _FavoritesList)
                    item.Notes = item.Notes.Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary();
                var rawList = JsonSerializer.Serialize(_FavoritesList, JsonCompat.SerializerOptions);
                States.Game.CompFavorites = JsonSerializer.Serialize(_FavoritesList, JsonCompat.SerializerOptions);
            }
        }

        public static string GetShareCode(HashSet<string> data)
        {
            try
            {
                return JsonSerializer.Serialize(data, JsonCompat.SerializerOptions);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[CompFavorites] 生成分享出错");
            }

            return "";
        }

        public static HashSet<string> GetIdsByShareCode(string code)
        {
            try
            {
                return JsonSerializer.Deserialize<HashSet<string>>(code, JsonCompat.SerializerOptions);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[CompFavorites] 通过分享获取 ID 出错");
            }

            return new HashSet<string>();
        }

        /// <summary>
        ///     显示收藏菜单。
        /// </summary>
        /// <param name="project"></param>
        /// <param name="pos"></param>
        public static void ShowMenu(CompProject project, UIElement pos, Action closedCallBack = null)
        {
            var body = new ContextMenu();
            foreach (var i in FavoritesList)
            {
                var item = new MyMenuItem();
                item.MaxWidth = 240d;
                var hasFavs = i.Favs.Contains(project.Id);
                if (hasFavs)
                {
                    item.Header = Lang.Text("Download.Comp.Detail.Favorites.UnfavoriteContextMenu", i.Name);
                    item.SvgIcon = "lucide/heart-filled";
                }
                else
                {
                    item.Header = Lang.Text("Download.Comp.Detail.Favorites.FavoriteContextMenu", i.Name);
                    item.SvgIcon = "lucide/heart";
                }

                item.Click += (_, _) =>
                {
                    try
                    {
                        if (hasFavs)
                        {
                            i.Favs.Remove(project.Id);
                            HintService.Hint(Lang.Text("Download.Comp.Detail.Favorites.Remove", project.TranslatedName, i.Name), HintType.Success);
                        }
                        else
                        {
                            i.Favs.Add(project.Id);
                            HintService.Hint(Lang.Text("Download.Comp.Detail.Favorites.Add", project.TranslatedName, i.Name), HintType.Success);
                        }

                        Save();
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, "[CompFavorites] 改变收藏项出错");
                    }
                };
                body.Items.Add(item);
            }

            body.Closed += (_, _) => closedCallBack?.Invoke();
            body.Placement = PlacementMode.Bottom;
            body.PlacementTarget = pos;
            body.IsOpen = true;
        }

        /// <summary>
        ///     显示收藏菜单。
        /// </summary>
        public static void ShowMenu(List<CompProject> project, UIElement pos, Action closedCallBack = null)
        {
            var body = new ContextMenu();
            foreach (var i in FavoritesList)
            {
                var item = new MyMenuItem
                {
                    MaxWidth = 240d,
                    Header = Lang.Text("Download.Comp.Detail.Favorites.FavoriteContextMenu", i.Name),
                    SvgIcon = "lucide/heart"
                };
                item.Click += (_, _) =>
                {
                    try
                    {
                        var count = i.Favs.Count;
                        project.Select(p => p.Id).ToList().ForEach(x => i.Favs.Add(x));
                        Save();
                        var successCount = i.Favs.Count - count;
                        var failedCount = project.Count - successCount;
                        HintService.Hint(
                            Lang.Text(failedCount > 0
                                ? "Download.Comp.Detail.Favorites.BulkAddWithFailures"
                                : "Download.Comp.Detail.Favorites.BulkAdd", successCount, i.Name, failedCount),
                            HintType.Success);
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, "[CompFavorites] 改变收藏项出错");
                    }
                };
                body.Items.Add(item);
            }

            body.Closed += (_, _) => closedCallBack?.Invoke();
            body.Placement = PlacementMode.Bottom;
            body.PlacementTarget = pos;
            body.IsOpen = true;
        }

        /// <summary>
        ///     保存收藏夹数据
        /// </summary>
        public static void Save()
        {
            FavoritesList = _FavoritesList;
        }

        /// <summary>
        ///     获取一个新的收藏夹
        /// </summary>
        /// <param name="name"></param>
        /// <param name="favList">没有传 Nothing</param>
        /// <returns></returns>
        public static FavData GetNewFav(string name, HashSet<string> favList)
        {
            var res = new FavData { Name = name, Id = Guid.NewGuid().ToString() };
            if (favList is null)
                res.Favs = new HashSet<string>();
            else
                res.Favs = favList;
            return res;
        }

        public static bool IsFavourite(string id)
        {
            if (FavoritesList is null)
                return false;
            foreach (var i in FavoritesList)
                if (i.Favs.Contains(id))
                    return true;
            return false;
        }

        public class FavData
        {
            /// <summary>
            ///     收藏夹名称
            /// </summary>
            /// <returns></returns>
            [JsonPropertyName("Name")]
            public string Name { get; set; }

            /// <summary>
            ///     Guid
            /// </summary>
            /// <returns></returns>
            [JsonPropertyName("Id")]
            public string Id { get; set; }

            /// <summary>
            ///     收藏的工程 ID 列表
            /// </summary>
            /// <returns></returns>
            [JsonPropertyName("Favs")]
            public HashSet<string> Favs { get; set; } = new();

            /// <summary>
            ///     备注
            /// </summary>
            /// <returns></returns>
            [JsonPropertyName("Notes")]
            public Dictionary<string, string> Notes { get; set; } = new();
        }
    }

}
