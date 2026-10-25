namespace PCL;

public static class UpdateEnums
{
    public enum VersionStatus
    {
        Latest,
        NotLatest,
        Unknown
    }
    
    public enum UpdateType
    {
        /// <summary>
        /// 已废弃：静默更新（下完直接替换，不告诉用户）。
        /// <para>
        /// 保留这个成员只是为了兼容旧的配置值与序列化，<b>不要再使用</b>——
        /// 启动器现在一律要求「更新必须让用户看见」，见 <c>UpdateManager.ScheduleBasedOnConfig</c>。
        /// </para>
        /// </summary>
        [Obsolete("静默更新已移除，请改用 DownloadAndPrompt 或 PromptOnly")]
        Silent = 0,

        PromptOnly = 1,
        DownloadAndPrompt = 2,
        UpdateNow = 3
    }
}