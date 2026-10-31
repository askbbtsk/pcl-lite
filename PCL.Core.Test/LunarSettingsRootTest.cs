using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 设置目录位置的回归测试。
///     <para>
///         游戏只从 <c>%USERPROFILE%\.lunarclient\settings\game\accounts.json</c> 读账号，
///         物料根目录（设置里那个「Lunar 根目录」）只影响 <c>ui/</c>、<c>offline/multiver/</c>
///         这些资源。两者混用会出一个很难查的循环：PCL 改的是物料目录下那份，
///         游戏读的是用户目录那份，于是续期永远「成功」但游戏永远认为令牌过期，
///         每秒重试一次，界面卡在 <c>Connecting...</c>。
///     </para>
/// </summary>
[TestClass]
public class LunarSettingsRootTest
{
    [TestMethod]
    public void SettingsRootShouldStayInUserProfile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lunarclient");

        Assert.AreEqual(expected, LunarClientService.SettingsRoot);
        Assert.AreEqual(LunarClientService.DefaultLunarRoot, LunarClientService.SettingsRoot);
    }

    [TestMethod]
    public void AccountStoreShouldLiveUnderSettingsRoot()
    {
        var path = LunarAccountStore.FilePath(LunarClientService.SettingsRoot);

        Assert.AreEqual(
            Path.Combine(LunarClientService.SettingsRoot, "settings", "game", "accounts.json"),
            path);
    }

    [TestMethod]
    public void LauncherConfigShouldLiveUnderSettingsRoot()
    {
        var path = LunarLauncherConfig.FilePath(LunarClientService.SettingsRoot);

        Assert.AreEqual(Path.Combine(LunarClientService.SettingsRoot, "settings", "launcher.json"), path);
    }
}
