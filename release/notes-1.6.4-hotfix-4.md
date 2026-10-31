<summary>1.6.4-hotfix-4：修 Lunar 卡在 Connecting... —— 账户库一直跟着「物料根目录」走，续期全写进了 D 盘那份游戏根本不看的 accounts.json，游戏读的 ~/.lunarclient 那份永远是过期令牌，每秒重试一次。</summary>

PCL Lite 1.6.4-hotfix-4。

> 热修复，稳定版通道会收到。

## 现象

Lunar 启动后一直停在 **Connecting...**，进不去主界面。游戏日志里同一组行每秒重复一次：

```
[LC Accounts] Current account's access token has expired, refreshing...
[LC Accounts] Refreshing account: bsk10ww
[Lunar IPC] 游戏请求续期账号 694cd52b-8197-45f0-b28d-ad73eb299699
[续期] 请求的 UUID 694cd52b… 不在账户库里，按本次启动用的账号 Bed_Spacekiller_ 处理
[Lunar IPC] 账号 694cd52b… 续期成功：取自 PCL 登录（Bed_Spacekiller_）
[LC Accounts] Successfully refreshed access token for account bsk10ww
[LC Accounts] Current account's access token has expired, refreshing...   ← 又来了
```

「续期成功」和「令牌还是过期」同时成立，说明**写进去的和读出来的不是同一个文件**。

## 根因：账户库被挂在了物料根目录下

物料根目录（设置里的「Lunar 根目录」）设成了 `D:\pcllitelunar`，而账户库的路径一直是
用这个根拼出来的：

```
D:\pcllitelunar\settings\game\accounts.json          ← PCL 一直在改这份
C:\Users\<用户>\.lunarclient\settings\game\accounts.json   ← 游戏只读这份
```

两份文件当时的内容：

```
D 盘那份    active=98e05be5…  只有 1 个账号（我们写的 Bed_Spacekiller_）
用户目录那份 active=78180d4b…  4 个账号，当前那个 bsk10ww 的令牌 10-29 就过期了
```

游戏读的是用户目录那份，所以：

- 续期把新令牌写进了 D 盘那份，游戏那份纹丝不动 → 它认为还过期 → 再问一次 → 死循环；
- 「添加账号」也写进了 D 盘那份，游戏看不到 → 白写。

证据在游戏自己的日志里就有：

```
[Bridge] Found JIT dir: C:\Users\18857\.lunarclient\jit      ← 设置走用户目录
[Bridge] Found ui dir:  D:\pcllitelunar\ui\…                 ← 资源走物料目录
```

Lunar 的 `settings/`、`launcher-cache/` 恒在 `%USERPROFILE%\.lunarclient`，
物料根只决定 `ui/`、`offline/multiver/`、`textures/` 从哪来（走 `--workingDirectory`）。

## 改了什么

| 位置 | 改动 |
| --- | --- |
| `LunarClientService.SettingsRoot` | 新增。设置目录恒为 `%USERPROFILE%\.lunarclient`，与物料根解耦，注释里写清了为什么 |
| `ModLunar` | 7 处账户库读写（`Read` / `SetActive` / `Upsert` / `CreateIfMissing` / `UpdateToken`）全部改用 `SettingsRoot` |
| `LunarLauncherConfig` 调用点 | 官方启动器的 `settings/launcher.json` 同样在用户目录，一并改过去（以前自定义物料根时「跟随 Lunar 内存设置」是失效的） |
| `ModLunar.FindRefreshTarget` | 删掉「按本次启动用的账号顶替」那条兜底。顶替的代价是回一个**假成功**：令牌写进了别的账号的条目，游戏重读自己要的那条还是过期的 → 死循环。现在只按 UUID / `localId` 认，认不出就明确回失败 |
| `PCL.Core.Test/LunarSettingsRootTest` | 新增 3 个用例，锁住「设置目录在用户目录、账户库与启动器配置都在它下面」 |

## 更新后要做什么

**重启一次游戏**。这次启动时 PCL 会把 `activeAccountLocalId` 切到你在 PCL 里登录的账号，
令牌也就写进游戏真正读的那份文件了。

（D 盘那份 `settings/game/accounts.json` 留着没删，里面只有 PCL 自己写过的一条，
不影响任何东西；确认没问题后可以手动删掉。）

## 校验值

```
SHA-256  53EC5F476BE6D1F70FFFCDA86BC613C9BB86BF373C5CDC3172B2928D4FC87ECC
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
