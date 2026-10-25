PCL Lite 1.2.2-beta。

**这是一个修复版本。** 1.2.1-beta 有一个会让设置页整个打不开的严重问题，请务必升级。

## 修复

### 设置页打不开（严重）

1.2.1-beta 打开「设置」会直接报 `XamlParseException: 无法从文本"BtnDiscordPortal_Click"创建"Click"`，
整个设置页加载失败，等于所有设置项都进不去。

原因是 Discord 那三个按钮的事件处理函数签名写成了 `(object, EventArgs)`，而
`MyButton.Click` 用的是自定义委托 `(object, MouseButtonEventArgs)`。这种不匹配**编译期查不出来**
（事件挂接走运行期 `Delegate.CreateDelegate`），只有真正打开页面才会炸。

### 切到 Beta 通道后查不到新版本

现象：启动器开着的时候发布了新版本，之后不管点多少次「重新检查」，都一直显示「已是最新版本」。

原因：版本列表在启动时拉一次就缓存到进程结束，没有任何有效期。发版前开着的启动器，
看到的永远是发版前那份列表，必须重启才认新版本。

现在：

- 「重新检查」和切换通道会**强制重新拉取**，不再吃缓存
- 自动检查的缓存有效期改为 5 分钟，到点自己刷新
- 切换通道时先写配置再检查。原来是反过来的，切完第一次检查用的还是旧通道，
  于是「切到 Beta 却提示已是最新」——那个结论是按正式通道算出来的

### Dev 通道被当成正式版

选「Dev」时 `IsCurrentVersionBeta` 只认 `== 1`，把 Dev（2）当成正式版，导致选了 Dev
反而收不到任何预发布版本。现在 Dev 也走 beta 版本源。

### Atom 降级通道把 beta 当正式版

GitHub API 被限流时会降级用 `releases.atom`，而 Atom 不提供 prerelease 标记，原来一律按正式版处理，
于是正式通道会把 beta 推给用户。现在按 tag 里的 `-` 后缀判断。

### Discord 状态「已连接」但看不到

Discord 同一时间只显示一个「正在玩」，别的 RPC 应用会把这个位置抢走；而原来内容没变就不再上报，
一旦被抢走就再也回不来。现在**每 30 秒重发一次**，被抢走后最多 30 秒能抢回来。

另外 `activity` 显式带上 `type: 0`（Playing）。

## 其它

- 版本号 `1.2.2-beta`
- **主页默认改为「预设 → PCL Lite 欢迎页」**；在设置里选「空白」现在是真的空白，
  不再强行显示欢迎页（原来是「空白」和「预设」共用同一个加载分支）
- 切换主页来源后立即刷新主页，不用手动点「刷新主页」
- 「特别鸣谢」精简为只保留 **bangbang93** 与 **z0z0r4** 两项
- `docs/DISCORD.md` 补充格式限制表（`details`/`state` 128 字符、`buttons` 最多 2 个等）与「多应用抢位」排查
- `README-EN.md` / `README-ZH_TW.md` 与中文版同步（前言、文档章节、下载文件名）

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.2.2-beta_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

## 已知问题

- Discord 状态需要 Discord **桌面端**在运行，网页版不行
- 若本机同时有别的程序在发 Rich Presence，两边会来回抢「正在玩」的位置
- 发布文件已做代码签名；签名证书为自签名，暂无商业信誉，个别安全软件首次运行时仍可能提示发布者信息
- 自动更新暂不做完整性哈希校验，以下载通道自身的安全性为准

## 许可证

本项目以 **MIT** 发布，并附带下列**传染性（copyleft）**条款。

完整条款见 [LICENSE](https://github.com/askbbtsk/pcl-lite/blob/main/LICENSE)。

### MIT License

```
Copyright (c) 2026 PCL Lite

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 附加条款（传染性）

1. **传染性**：任何修改版、衍生版或再分发版，必须同样以本协议（MIT + 本附加条款）分发，
   不得改用其他许可证。
2. **必须署名**：必须完整保留本项目的版权声明与作者署名，不得删除或替换。
3. **二次修改须标注 PCL Lite**：修改后分发时，必须在名称或说明中明确标注本项目为
   **PCL Lite**，不得抹去或替换原有标识。
4. **与 Mojang / Microsoft 无关**：本项目与 Mojang Studios、Microsoft 及其关联公司
   没有任何关系，未获其批准、授权或背书。
5. **地域限制**：中国大陆用户请勿使用。
6. **协议可变**：本协议可能随时更新，且不提前通知；更新只对之后获取的版本生效，
   **已经 fork 或已经分发的版本不受影响**。

## 校验

SHA-256：

```
502d549011b31b113cabff492e558000cbac304758ae5795ca95be8421eeb0df
```

---

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
