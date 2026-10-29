# 配置与数据目录

想知道某个设置存在哪、想手动改、想批量改，看这篇。

## 一、目录一览

`Paths.cs` 里定义，分四类：

| 用途 | 路径 | 说明 |
| --- | --- | --- |
| 实例数据 | `<启动器exe目录>\PCL\` | 每份启动器一份，配置、缓存、日志都在这 |
| 共享数据 | `%APPDATA%\PCLLite\` | 跨启动器实例共享，账号等 |
| 共享本地数据 | `%LOCALAPPDATA%\PCLLite\` | 大文件、可随时删的缓存 |
| 临时 | `%TEMP%\PCLLite\` | 运行中可删，运行时可删但要小心 |

> 调试版（`#if DEBUG`）目录名是 `PCLLite_Debug` / `.PCLCEDebug`，和正式版隔离，
> 可以同时开着两个版本调试。调试版还认四个环境变量覆盖路径：
> `PCL_PATH` / `PCL_PATH_SHARED` / `PCL_PATH_LOCAL` / `PCL_PATH_TEMP`。

> 1.5.5 起这两处目录叫 `PCLLite`（以前叫 `PCLCE`）。**不做迁移**：老的 `PCLCE`
> 目录原样留着，新版本只读写新目录。所以从 1.5.4 及更早升级上来时，
> 设置和登录状态是空的 —— 重新配一次即可，老目录可以手动删。

## 二、三份配置文件

| 文件 | 路径 | 格式 | 存什么 |
| --- | --- | --- | --- |
| 本地配置 | `<exe目录>\PCL\config.v1.yml` | YAML | 界面、主题、下载、Java、启动参数等绝大多数设置 |
| 共享配置 | `%APPDATA%\PCLLite\config.v1.json` | JSON | 跨实例共享的东西（账号相关等） |
| 实例配置 | `<游戏目录>\PCL\config.v1.yml` | YAML | 只对该游戏实例生效的覆盖项 |

三者的关系：**实例配置 > 本地配置 > 默认值**，共享配置是独立的一份，不参与这个优先级。

`ConfigSource` 枚举对应关系：

| 枚举 | 落到哪个文件 |
| --- | --- |
| `Shared` | `config.v1.json` |
| `SharedEncrypt` | 同上，但值经过 DPAPI 加密（账号令牌这类） |
| `Local` | `config.v1.yml` |
| `GameInstance` | `<游戏目录>\PCL\config.v1.yml` |

## 三、键名怎么来的

配置项都是源生成器生成的 partial 属性，键名就是 `ConfigItem` 的第一个参数：

```csharp
[ConfigItem<int>("UiLauncherHue", 210, ConfigSource.Local)]
public partial int WindowHue { get; set; }
```

- **文件里的键** → `UiLauncherHue`
- **代码里的名** → `Config.Preference.Theme.WindowHue`

两者刻意不一样：文件键沿用原版 PCL2 的 `setup.ini` 命名，方便老配置迁移；
代码名是后来整理过的。找键名时用文件键去搜 `ConfigItem`。

## 四、主题相关键速查

完整说明见 [THEME.md](THEME.md)，这里只列键名方便搜索：

| 文件键 | 类型 | 默认 | 作用 |
| --- | --- | --- | --- |
| `UiDarkMode` | `ColorMode` | `System` | 浅色 / 深色 / 跟随系统 |
| `UiDarkColor` | `ColorTheme` | `CatBlue` | 深色模式用哪个内置主题（路径 B） |
| `UiLightColor` | `ColorTheme` | `CatBlue` | 浅色模式同上 |
| `UiCustomTheme` | bool | `true` | 是否走 HSL 引擎（路径 A） |
| `UiLauncherHue` | int | 210 | 色相 0–360 |
| `UiLauncherSat` | int | 85 | 饱和度 0–100 |
| `UiLauncherLight` | int | 20 | 明度调整，**实际值 = 存储值 − 20** |
| `UiLauncherDelta` | int | 90 | 标题栏色相偏移，**实际值 = 存储值 − 90** |
| `UiLauncherDeltaTriple` | string | `""` | 三元组，如 `-30,10,50` |
| `UiLauncherTheme` | int | 0 | 预设编号，14 = 自定义 |
| `UiLauncherTransparent` | int | 600 | 窗口透明度 |
| `UiBackgroundColorful` | bool | `true` | 主窗口彩色背景渐变 |
| `UiToneProfiles` | 对象 | 见 THEME.md | 灰度档位，可整体调深浅 |

`ColorMode`：`Light=0` / `Dark=1` / `System=2`
`ColorTheme`：`SkyBlue=0` / `CatBlue=1` / `DeathBlue=2` / `HmclBlue=3`

### Discord 状态显示

| 文件键 | 类型 | 默认 | 作用 |
| --- | --- | --- | --- |
| `DiscordRpcEnabled` | bool | `false` | 总开关，关着就完全不连 Discord |
| `DiscordRpcAppId` | string | 内置 ID | 换成自己的应用才需要改 |
| `DiscordRpcFields` | string | `launcherVersion,launcherMemory,playTime` | 逗号分隔的显示项 |

详见 [DISCORD.md](DISCORD.md)。

## 五、手动改配置的正确姿势

**必须先关掉启动器。**

配置写入是异步批处理的（`FileConfigStorage` 内部有个 Channel，约每 10 秒落一次盘），
运行中手改文件会被内存里的值覆盖回去，甚至写出半截内容。

步骤：

1. 完全退出启动器（托盘也要退）
2. 备份：`copy config.v1.yml config.v1.yml.bak`
3. 编辑 YAML。注意**缩进两格**，布尔用 `true` / `false`
4. 启动，验证

改坏了直接把 `.bak` 改回来，启动器不会因为配置损坏而无法启动——解析失败会回退默认值。

## 六、迁移逻辑

老版本（PCL2 CE）的 `setup.ini` 会自动迁移：

| 从 | 到 |
| --- | --- |
| `<exe目录>\PCL\setup.ini` | `<exe目录>\PCL\config.v1.yml` |
| `%APPDATA%\.PCLCE\*.json` | `%APPDATA%\PCLLite\config.v1.json` |
| `<游戏目录>\PCL\setup.ini` | `<游戏目录>\PCL\config.v1.yml` |

迁移只在目标文件不存在时执行一次（`ConfigService._TryMigrate`），INI 解析器是 `CatIniMigration`。
想重新触发迁移，删掉目标文件再启动即可——但**先备份**，迁移不保证覆盖所有自定义项。

## 七、日志

日志在 `<exe目录>\PCL\Log\`（`LogService`）。排查配色问题时有用的行：

```
当前状态: Dark, CatBlue
```

启动时 `ThemeService._LogStatus()` 打的，能直接确认当前是深色还是浅色、用的哪个主题。
如果这行都没有，说明主题服务没起来，先查 IoC 生命周期。
