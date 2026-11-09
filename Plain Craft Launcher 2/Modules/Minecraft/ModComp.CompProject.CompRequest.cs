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



    public class CompRequest
    {
        /// <summary>
        ///     通过项目 Id 判断是否来自 CurseForge
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static bool IsFromCurseForge(string id)
=>ResourceProjectRequestService.IsFromCurseForge(id);

        /// <summary>
        ///     通过一堆 ID 从 Modrinth 那获取项目信息
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public static Task<List<CompProject>> GetListByIdsFromModrinthAsync(List<string> ids)
=>ResourceProjectRequestService.GetListByIdsFromModrinthAsync(ids);

        /// <summary>
        ///     通过一堆 ID 从 CurseForge 那获取项目信息
        /// </summary>
        /// <param name="Ids"></param>
        /// <returns></returns>
        public static Task<List<CompProject>> GetListByIdsFromCurseforgeAsync(List<string> ids)
=>ResourceProjectRequestService.GetListByIdsFromCurseforgeAsync(ids);

        public static List<CompProject> GetCompProjectsByIds(List<string> input)
=>ResourceProjectRequestService.GetCompProjectsByIds(input);

        public static Task<List<CompProject>> GetCompProjectsByIdsAsync(List<string> input)
=>ResourceProjectRequestService.GetCompProjectsByIdsAsync(input);
    }

}
