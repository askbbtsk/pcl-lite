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
public partial class CompProject
{

        // 辅助函数

        /// <summary>
        ///     检查是否与某个 Project 是相同的工程，只是在不同的网站。
        /// </summary>
        public bool IsLike(CompProject project)
        {
            if ((Id ?? "") == (project.Id ?? ""))
                return true; // 相同实例

            // 提取字符串中的字母和数字
            string GetRaw(string data)
            {
                var result = new StringBuilder();
                foreach (var r in data.Where(c => char.IsLetterOrDigit(c)))
                    result.Append(r);
                return result.ToString().ToLower();
            }

            ;
            // 来自不同的网站
            if (FromCurseForge == project.FromCurseForge)
                return false;
            // Mod 加载器一致
            if (ModLoaders.Count != project.ModLoaders.Count || ModLoaders.Except(project.ModLoaders).Any())
                return false;
            // 若不为光影，则要求 MC 版本一致
            if (Type != CompType.Shader && (Drops.Count != project.Drops.Count || Drops.Except(project.Drops).Any()))
                return false;
            // 最近更新时间差距在一周以内
            if (LastUpdate is not null && project.LastUpdate is not null &&
                Math.Abs((LastUpdate - project.LastUpdate).Value.TotalDays) > 7d)
                return false;
            // MCMOD 翻译名 / 原名 / 描述文本 / Slug 的英文部分相同
            if ((TranslatedName ?? "") == (project.TranslatedName ?? "") ||
                (RawName ?? "") == (project.RawName ?? "") || (Description ?? "") == (project.Description ?? "") ||
                (GetRaw(Slug) ?? "") == (GetRaw(project.Slug) ?? ""))
            {
                ModBase.Log($"[Comp] 将 {RawName} ({Slug}) 与 {project.RawName} ({project.Slug}) 认定为相似工程");
                // 如果只有一个有 DatabaseEntry，设置给另外一个
                if (DatabaseEntry is null && project.DatabaseEntry is not null)
                    DatabaseEntry = project.DatabaseEntry;
                if (DatabaseEntry is not null && project.DatabaseEntry is null)
                    project.DatabaseEntry = DatabaseEntry;
                return true;
            }

            return false;
        }


        public override string ToString()
        {
            return $"{Id} ({Slug}): {RawName}";
        }


        public override bool Equals(object obj)
        {
            var project = obj as CompProject;
            return project is not null && (Id ?? "") == (project.Id ?? "");
        }


        public override int GetHashCode() => (Id ?? "").GetHashCode(StringComparison.Ordinal);


        public static bool operator ==(CompProject left, CompProject right)
        {
            return EqualityComparer<CompProject>.Default.Equals(left, right);
        }


        public static bool operator !=(CompProject left, CompProject right)
        {
            return !(left == right);
        }

}
}
