# PCL Lite 内置 MCP 服务

让 AI 客户端（Claude、Cursor、Cherry Studio、Cline……）直接操作启动器：启动游戏、整理 mod、分析崩溃。

MCP（Model Context Protocol）是 AI 客户端调工具的标准协议。启动器在这里扮演「工具提供方」，
AI 扮演「调用方」——你在对话框里说「帮我把重复的 sodium 删掉」，AI 就会真的去改文件。

---

## 一、打开服务

**设置 → 启动器 → 杂项 → MCP 服务（AI 控制接口）**

| 项 | 说明 |
|---|---|
| 启用 MCP 服务 | 总开关，**默认关闭** |
| 监听端口 | 默认 `38765`，固定只绑 `127.0.0.1` |
| 访问口令 | 可为空。空 = 本机任意程序都能连；设了之后请求必须带 `Authorization: Bearer <口令>` |
| 允许修改游戏文件 | 关掉后只能查看 mod / 存档，不能启用、移动、删除 |
| 允许启动/停止游戏 | 关掉后不能触发启动流程，也不能强杀进程 |
| 允许修改启动器设置 | **默认关闭**。关掉时 `pcl_config_set` 会被拒绝 |

口令存在加密的共享配置里（`%APPDATA%\PCLCE\config.v1.json` 里是 `ENC!...` 密文），不会明文落进 `config.v1.yml`。
改完口令**立刻生效**，不用重启服务。

---

## 二、接到 AI 客户端

点「复制连接配置」，会得到：

```json
{
  "mcpServers": {
    "pcl-lite": {
      "type": "http",
      "url": "http://127.0.0.1:38765/mcp"
    }
  }
}
```

设了口令时会多一段：

```json
{
  "mcpServers": {
    "pcl-lite": {
      "type": "http",
      "url": "http://127.0.0.1:38765/mcp",
      "headers": { "Authorization": "Bearer 你的口令" }
    }
  }
}
```

各客户端放的位置：

- **Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`
- **Cursor** — `~/.cursor/mcp.json`
- **Cherry Studio / Cline / Continue** — 在「MCP 服务器」设置里选 HTTP 类型，粘 URL

改完重启客户端。连上之后可以让 AI 调 `pcl_server_status` 自检。

> 有些客户端只支持 stdio。那种情况下用 `npx -y mcp-remote http://127.0.0.1:38765/mcp` 做一层转发。

---

## 三、工具清单

### 实例

| 工具 | 作用 |
|---|---|
| `pcl_list_instances` | 列出所有实例（MC 版本、加载器、mod 数量） |
| `pcl_instance_info` | 单个实例详情：目录、是否版本隔离、内存、Java、各子目录状态 |
| `pcl_list_directory` | 浏览实例下任意子目录（只读，不解析 jar） |

### mod / jar

| 工具 | 作用 |
|---|---|
| `pcl_list_mods` | 列 mod，带 modId / 版本 / 加载器 / 依赖 / 启用状态 |
| `pcl_mod_info` | 读单个 jar 的完整元数据 |
| `pcl_disable_mod` | 禁用（`x.jar` → `x.jar.disabled`，只改名，零风险） |
| `pcl_enable_mod` | 启用（改回来；目标已存在时拒绝，不会覆盖） |
| `pcl_move_mod` | 移到另一个实例，或移到当前实例下的子目录 |
| `pcl_delete_mod` | 删除。**默认软删除**，文件进 `PCL/McpTrash/<时间戳>/`；`hard=true` 才真删 |
| `pcl_list_resourcepacks` | 列资源包与光影 |
| `pcl_list_saves` | 列存档（带大小、修改时间） |

### 游戏

| 工具 | 作用 |
|---|---|
| `pcl_launch_game` | 启动。前置检查失败（没选档案、Java 无效）会在 3 秒内返回明确错误 |
| `pcl_stop_game` | 强杀进程（不保存世界，慎用） |
| `pcl_game_status` | 进程是否存活、加载到哪一步 |
| `pcl_server_status` | 服务自身状态 + 可复制的客户端配置 |

### 分析

| 工具 | 作用 |
|---|---|
| `pcl_analyze_instance` | 完整体检：重复 mod、加载器混装、缺前置、MC 版本不符、Java 过低、内存不足 |
| `pcl_analyze_mods` | 只看 mod 层面的问题，返回更短，适合改完复查 |
| `pcl_analyze_crash` | 解析 `crash-reports` / `latest.log`，按已知模式给结论和可疑 mod |
| `pcl_find_mod` | 按关键词搜 mod |

### 启动器

| 工具 | 作用 |
|---|---|
| `pcl_launcher_info` | 版本、路径、日志目录、当前实例 |
| `pcl_log_tail` | 读启动器日志（可按级别、关键词过滤） |
| `pcl_config_list` / `pcl_config_get` / `pcl_config_set` | 配置读写（写需要开权限） |

---

## 四、提示词模板

服务端内置 5 条 prompt，客户端里会直接出现在提示词列表：

| 名称 | 用途 |
|---|---|
| `analyze-instance` | 体检一个实例并用人话解释 |
| `fix-crash` | 从崩溃报告出发逐层排除 |
| `organize-mods` | 清理重复、补齐依赖（先列清单再动手） |
| `launch-and-verify` | 启动并确认真的跑起来了 |
| `explain-mod` | 查清一个 mod 是干什么的、缺什么前置 |

这些 prompt 的作用是把排查顺序固化下来——与其指望模型自己想到「先看日志再看配置」，
不如给一条写死的路径，它照着走就不会漏步骤。

---

## 五、设计取舍

**为什么只监听 127.0.0.1。**
这是给本机 AI 用的接口，没有任何理由暴露到局域网。绑定回环地址之后，外部网络物理上碰不到，
剩下的攻击面只有「同一台机器上的其他程序」——这正是口令存在的意义。

**为什么默认关闭。**
它开放了「读写游戏目录 + 启动进程」的能力。默认开启意味着任何装了这个启动器的人，
在不知情的情况下多了一个可被本机任意程序调用的文件操作接口。必须由用户主动打开。

**为什么删除是软删除。**
模型会犯错。删错了 mod 的代价是重新下载几百 MB，而软删除的代价只是多占一点磁盘。
`PCL/McpTrash/` 下按时间戳分目录，想恢复直接拖回去。

**为什么路径要过 `McpPath.ResolveUnder`。**
入参来自模型，必须假设它可能写出 `../../Windows/System32`。所有文件操作都锁死在游戏目录内，
越界直接拒绝并告诉模型原因。

**为什么 jar 用 `FileShare.ReadWrite` 打开。**
游戏运行中 mod 文件是被占用的。用默认共享模式读会直接抛 `IOException`，
而「一边玩游戏一边让 AI 整理 mod」恰恰是最常见的场景。

---

## 六、排查

**连不上**

1. 设置页看状态是不是「运行中」
2. 终端跑 `curl http://127.0.0.1:38765/health`（**加 `--noproxy '*'`**，否则会被系统代理劫持）
3. 端口被占用时状态栏会显示「启动失败 —— ...」，换个端口

**返回 401**

口令不对。用 `?token=<口令>` 或 `Authorization: Bearer <口令>` 两种方式都行。

**工具返回「需要写入权限」**

对应权限开关被关掉了。返回信息里会指出该去开哪个。

**`pcl_launch_game` 返回「准备阶段就失败」**

通常是没在启动器里选游戏档案（离线账号），或者 Java 路径无效。
调 `pcl_log_tail` 看具体原因。
