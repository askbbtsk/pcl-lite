using System;
using System.Text.RegularExpressions;

namespace PCL.Core.Utils.OS;

/// <summary>
///     把 Windows 用户文件夹的名字打码成 <c>*****</c>。
///     <para>
///         为什么不做「把用户名替换成星号」：用户名经常就是 <c>18857</c> 这种普通词，
///         整串替换会连版本号、文件大小、别的数字一起改掉；而且只能命中当前用户、
///         只认反斜杠，<c>C:\Users\Public</c> 或别人账号下的路径一律漏掉。
///     </para>
///     <para>
///         这里只认 <c>Users</c> 这个路径段：紧跟它后面的那一段才是用户名，替换的也只有
///         这一段，前后字符一个都不动。所以 <c>C:\MyUsers\18857</c> 这种长得像但结构不对的
///         不会被误伤。
///     </para>
///     <para>
///         统一打成五个星号，不按名字长度补 —— 星号个数等于名字长度，本身就是在泄露长度。
///     </para>
/// </summary>
public static partial class UserPathMasker
{
    /// <summary>替换用的固定文本。</summary>
    public const string Mask = "*****";

    private static volatile bool _enabled = true;

    /// <summary>
    ///     界面上要不要打码。默认开；由启动器在启动时和用户改设置时同步过来
    ///     （<c>Config.Preference.MaskUserFolder</c>）。
    ///     <para>
    ///         放在这里而不是直接读配置，是为了让这层只做字符串处理、不认识配置系统 ——
    ///         <c>Lang.Text</c> 在启动早期就会被调用，那时候读配置容易踩到初始化顺序。
    ///     </para>
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>按当前开关决定要不要打码；关掉时原样返回。</summary>
    public static string Apply(string? text) => Enabled ? MaskUserFolders(text) : text ?? string.Empty;

    /// <summary>
    ///     <c>Users</c> 路径段 + 紧随其后的一段（用户名）。前面必须是分隔符或字符串开头，
    ///     后面遇到分隔符、空白或文件名非法字符就停。
    /// </summary>
    [GeneratedRegex(@"(?<![^\\/])[Uu][Ss][Ee][Rr][Ss][\\/][^\\/\s""'<>|:*?]+")]
    private static partial Regex UserFolderPattern();

    /// <summary>
    ///     把文本里所有 <c>Users\&lt;名字&gt;</c> 的「名字」换成 <see cref="Mask" />。
    ///     没有命中时原样返回（同一个字符串实例），调用方可以放心地到处调。
    /// </summary>
    public static string MaskUserFolders(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        // 绝大多数文本里根本没有这个路径段，先做个大小写无关的粗筛，省掉正则的开销
        if (text.IndexOf("users", StringComparison.OrdinalIgnoreCase) < 0) return text;
        return UserFolderPattern().Replace(text, match => match.Value[..6] + Mask); // 前 6 个字符是 "Users" + 分隔符
    }
}
