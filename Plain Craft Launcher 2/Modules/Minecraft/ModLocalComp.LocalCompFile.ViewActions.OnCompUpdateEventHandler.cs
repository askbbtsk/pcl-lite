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

        public delegate void OnCompUpdateEventHandler(LocalCompFile sender);


        /// <summary>
        ///     该 Mod 关联的网络项目。
        /// </summary>
        public CompProject Comp
        {
            get => field;
            set
            {
                field = value;
                OnCompUpdate?.Invoke(this);
            }
        }


        /// <summary>
        ///     该 Mod 对应的联网最新版本。
        /// </summary>
        public CompFile UpdateFile
        {
            get => field;
            set
            {
                field = value;
                OnCompUpdate?.Invoke(this);
            }
        }


        /// <summary>
        ///     将网络信息保存为 Json。
        /// </summary>
        public JsonObject ToJson()
        {
            var json = new JsonObject();
            if (Comp is not null)
                json.Add("Comp", Comp.ToJson());
            json.Add("ChangelogUrls", new JsonArray(changelogUrls.Select(s => (JsonNode)s).ToArray()));
            json.Add("CompLoaded", compLoaded);
            if (compFile is not null)
                json.Add("CompFile", compFile.ToJson());
            if (UpdateFile is not null)
                json.Add("UpdateFile", UpdateFile.ToJson());
            return json;
        }


        /// <summary>
        ///     从 Json 中读取网络信息。
        /// </summary>
        public void FromJson(JsonObject json)
        {
            compLoaded = (bool)json["CompLoaded"];
            if (json.ContainsKey("Comp"))
                Comp = new CompProject((JsonObject)json["Comp"]);
            if (json.ContainsKey("ChangelogUrls"))
                changelogUrls = json["ChangelogUrls"].ToObject<List<string>>();
            if (json.ContainsKey("CompFile"))
                compFile = new CompFile((JsonObject)json["CompFile"], CompType.Mod);
            if (json.ContainsKey("UpdateFile"))
                UpdateFile = new CompFile((JsonObject)json["UpdateFile"], CompType.Mod);
        }


        /// <summary>
        ///     该文件是否可以更新。
        /// </summary>
        public bool CanUpdate => !Config.Preference.Hide.FunctionModUpdate && changelogUrls.Any();


        /// <summary>
        ///     获取用于 CurseForge 信息获取的 Hash 值（MurmurHash2）。
        /// </summary>
        public uint CurseForgeHash
        {
            get
            {
                if (_CurseForgeHash is null)
                {
                    var buf = _hashCache.Value
                        .GetMurmurHash2Async(path)
                        .GetAwaiter().GetResult()
                        .HexToBytes();
                    _CurseForgeHash = BitConverter.ToUInt32(buf);
                }

                return (uint)_CurseForgeHash;
            }
        }


        /// <summary>
        ///     获取用于 Modrinth 信息获取的 Hash 值（SHA1）。
        /// </summary>
        public string ModrinthHash
        {
            get
            {
                if (field is null)
                    field = _hashCache.Value.GetSHA1Async(path).GetAwaiter().GetResult();

                return field;
            }
        }

}
}
