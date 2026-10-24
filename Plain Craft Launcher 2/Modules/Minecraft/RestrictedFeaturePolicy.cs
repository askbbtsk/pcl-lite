namespace PCL;

/// <summary>
/// 受限功能策略。
/// <para>
/// PCL Lite 不做任何地区判定：下载、补全、整合包安装等功能对所有用户一视同仁地开放。
/// 这里保留同名类型只是为了不改动既有调用点，所有方法恒为「不受限」。
/// </para>
/// </summary>
internal static class RestrictedFeaturePolicy
{
    internal static bool IsRestricted => false;

    internal static bool Check(string feature, bool showHint = true) => true;

    internal static void ThrowIfRestricted(string feature)
    {
    }
}
