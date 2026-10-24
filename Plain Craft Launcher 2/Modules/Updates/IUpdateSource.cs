using PCL.Core.Utils;

namespace PCL;

public interface IUpdateSource
{
    string SourceName { get; set; }

    /// <summary>
    ///     是否可用，根据本地情况判断
    /// </summary>
    /// <returns></returns>
    bool IsAvailable();

    /// <summary>
    ///     确保最新版本
    /// </summary>
    /// <param name="force">
    ///     为 true 时无视缓存有效期，强制重新拉取。
    ///     用户手动点「重新检查」时必须传 true：否则一个在发版前就开着的启动器
    ///     会一直用启动那一刻的版本列表，永远看不到新发布的版本。
    /// </param>
    /// <returns>True 表示更新成功，False 表示没有数据更新</returns>
    bool RefreshCache(bool force = false);

    VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch);
    bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode);
    VersionAnnouncementDataModel GetAnnouncementList();
    List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output);
}