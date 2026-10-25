<summary>新增基岩版启动（商店版 / GDK 便携版），并大幅收紧内置 MCP 的安全边界：口令不可读、权限不可自我提权、启动参数白名单、真取消与超时、回收站可还原、审计日志，以及改 options.txt / 备份存档 / 按链接安装 mod 三个新能力。</summary>

## 基岩版启动

百宝箱里多了一页「基岩版」，能找出本机已装的基岩版并直接启动。两种形态都支持：

| 形态 | 怎么认 | 怎么启动 |
| --- | --- | --- |
| 商店版 | 系统包 `Microsoft.MinecraftUWP`（正式）/ `Microsoft.MinecraftWindowsBeta`（预览） | `shell:appsFolder\<包族名>!<AppId>` |
| GDK 便携版 | 解包目录里的 `Minecraft.Windows.exe` | 直接运行，不注册系统包，天然多版本共存 |

页面提供：重新检测、结束游戏进程、添加便携版目录、打开存档目录。

**只做检测与启动，不下载也不解包游戏本体。** GDK 的 `.msixvc` 需要微软内容密钥才能解密，
不适合随源码分发，也与「启动器负责启动」的定位不符。

启动逻辑移植自 [PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
按本项目结构重写为不依赖 WPF 的静态服务，可单测。**本项目许可证不变**，
来源与许可证见新增的 [docs/CREDITS.md](docs/CREDITS.md)，启动器「关于 → 开源许可」里也加了这一条。

## MCP：从「能用」到「敢开」

这一版把 MCP 的安全与协议层按审计报告逐条重做。以前最大的问题是：
**只要用户打开「允许修改启动器设置」，前面所有权限开关都会失效**——AI 可以自己把权限改回去。

### 权限与凭据

- **口令不可读**：`pcl_config_get` 读到 `McpToken` 直接拒绝，不再把明文送进对话记录
- **权限不可自我提权**：`Mcp*` 全部配置项与 `SystemDebug*` 一律不可写，只能由用户在界面上改
- **默认全关**：「允许修改游戏文件」「允许启动/停止游戏」默认值从 `true` 改为 `false`
- **比较不泄露时序**：口令比对改成 SHA-256 归一 + `FixedTimeEquals`
- **`?token=` 已移除**：查询串会整条进日志，等于把口令写进明文

### 启动参数白名单

`extraArgs` 原来原样拼进 Java 命令行，等于给了一条代码注入通道。现在：

- 拒绝 `-javaagent:` / `-agentlib:` / `-agentpath:` / `-Xbootclasspath` / `-Djava.system.class.loader` 等
- 拒绝 `-jar` / `-cp` / `--module-path` 这类改变启动目标的参数
- 拒绝含空白与引号的参数（最终用空格拼接，带空格会被拆错）
- 上限 32 个、单个 512 字符

### 协议层

- **取消是真的**：`notifications/cancelled` 会中断在飞请求，另有 3 分钟请求超时，超时返回 `-32800`
- **错误语义不再混用**：未知工具 / 资源 / 提示词返回 `-32602`，不再返回「空结果」
- **500 不再回堆栈**：只回通用文案，详情进日志
- **结构化输出**：`tools/call` 同时返回 `content` 与 `structuredContent`
- **并发与体积上限**：4 并发（超出 `503`）、请求体 4 MB（超出 `413`）
- **同时绑 v4 与 v6 回环**：客户端把 `localhost` 解析成 `::1` 时也能连
- **UI 线程不再挂死**：需要界面的工具有 20 秒上限，超时返回明确错误

### 路径沙箱

`ResolveUnder` 现在会把路径逐级展开 junction / 符号链接再比前缀。
以前只做字符串前缀比较，游戏目录里放一个指向 `C:\` 的目录联接就能越界。

### 新能力

| 工具 | 作用 |
| --- | --- |
| `pcl_list_trash` / `pcl_restore` / `pcl_clean_trash` | 软删除不再单向：能列、能还原、能按天龄清理 |
| `pcl_game_option_get` / `pcl_game_option_set` | 读写 `options.txt`，改前自动备份 |
| `pcl_backup_saves` / `pcl_list_backups` | 存档打包成 zip 放进 `PCL/McpBackup/` |
| `pcl_install_mod` | 从 https 直链下载 jar 装进 mods（校验 zip 魔数、可选 SHA-256、不覆盖同名） |

### 审计日志

变更类工具（写 / 启动控制 / 配置）会往 `PCL/Log/mcp-audit.log` 记一行 JSON：
谁、什么时候、调了什么、参数摘要、成功与否。读取类不记。口令类键名不进日志。

### 分析器的两个真 bug

- `RecommendedJavaMajor("1.21")` 原来返回 17（应为 21）——判断写成了 `minor >= 20 && patch >= 5`
- `ParseJavaMajorFromPath("…jre1.8.0_51…")` 原来返回 0，导致「Java 版本过低」检查整段被跳过

## 其它

- 基岩版页面的文案补齐 zh-CN / zh-TW / en-US / en-GB 四种语言
- 新增 [docs/CREDITS.md](docs/CREDITS.md)（开源项目清单），并从 README 与 docs 索引链接过去
- 新增 19 个 MCP 单元测试（路径沙箱、配置闸门、参数白名单、分析器、审计摘要），`dotnet test` 全绿
- 修正 `docs/MCP.md`：删掉 `?token=`、补上默认关闭的权限、新工具、审计位置、协议错误码

## 校验

```text
SHA-256: 08d10fcbca17900039852e93ea3bac147987c56b8e6ec215ee9d922bd5b9215b
```
