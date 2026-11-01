using System;
using PCL.Core.Utils.OS;

namespace PCL;

public static class McLogFilter
{
    /// <summary>
    ///     打码字符串中的 AccessToken。
    /// </summary>
    public static string FilterAccessToken(string raw, char filterChar)
    {
        // 打码 "accessToken " 后的内容
        if (raw.Contains("accessToken "))
            foreach (var Token in raw.RegexSearch("(?<=accessToken ([^ ]{5}))[^ ]+(?=[^ ]{5})"))
                raw = raw.Replace(Token, new string(filterChar, Token.Count()));
        // 打码当前登录的结果
        var accessToken = ModLaunch.mcLoginLoader.output.AccessToken;
        if (accessToken is not null && accessToken.Length >= 10 && raw.ContainsF(accessToken, true) &&
            (ModLaunch.mcLoginLoader.output.Uuid ?? "") !=
            (ModLaunch.mcLoginLoader.output.AccessToken ?? "")) // UUID 和 AccessToken 一样则不打码
            raw = raw.Replace(accessToken, accessToken[..5] + new string(filterChar, accessToken.Length - 10) +
                                           accessToken[^5..]);
        return raw;
    }

    /// <summary>
    ///     打码字符串中的 Windows 用户文件夹名。
    ///     <para>
    ///         以前这里是 <c>raw.Replace(userProfile, userProfile.Replace(userName, 星号))</c>：
    ///         只能命中当前用户、只认反斜杠，而且星号个数等于名字长度（等于把长度也说出去）。
    ///         现在交给 <see cref="UserPathMasker" /> 按路径结构处理 —— 凡是 <c>Users</c>
    ///         路径段后面那一段，不管是谁的账号，统一打成五个星号。
    ///     </para>
    /// </summary>
    public static string FilterUserName(string raw) => UserPathMasker.MaskUserFolders(raw);
}
