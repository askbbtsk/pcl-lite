PCL Lite 1.2.1-beta。

## 本次内容

### 新增：Discord 状态显示（Rich Presence）

在 Discord 个人资料里展示你正在做什么，默认关闭，开关在 **设置 → 启动器 → Discord 状态显示**。

- **未开游戏**：显示启动器版本、内存占用、已运行时长
- **游戏运行中**：自动切换为实例名、游戏内存、游玩时长
- **时长由 Discord 自己跳动**，不会为了刷新计时器而反复上报
- **显示内容可多选**：启动器版本 / 启动器内存 / 游戏版本 / 游戏内存 / 游玩时长 / 服务器地址
- **服务器地址默认不显示**。它会从游戏日志里读当前连接的服务器，打开后任何能看到你资料的人
  都会知道你在哪个服，因此单独一项、默认关闭
- 应用 ID 已内置，打开开关即可用；也可以换成自己的 Discord 应用
- 状态只通过本机命名管道交给 Discord 客户端，**不经过任何第三方服务器**
- 支持「在 Discord 中清除」，可临时撤下状态

### 其它

- 版本号 `1.2.1-beta`
- 新增 `docs/DISCORD.md` 与文档索引条目，说明显示内容、隐私边界、实现结构与测试方法
- 新增应用图标源文件 `docs/assets/logo-{1024,512,256}.png`

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.2.1-beta_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

## 已知问题

- Discord 状态需要 Discord **桌面端**在运行，网页版不行
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
40e4921551e4b43aa7803361f0a552ac4c28522208e304c7d371c5001bf371e4
```

---

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
