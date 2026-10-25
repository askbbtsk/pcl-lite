PCL Lite 的第一个正式版。

## 本次内容

- 版本号升级为 `1.0.0`，转为正式版
- **支持微软正版登录**：OAuth2 设备码流程，在浏览器中完成微软账户授权后自动衔接 Xbox Live 与 Minecraft 身份校验；登录状态本地保存，后续启动自动登录，无需重复授权
- 自动更新新增多个国内下载反代，更新下载不再完全依赖直连 GitHub；接口受限时自动降级到 Atom 订阅源，更新检查不再直接报错
- 删除启动页的社区版提示卡片
- 关于页移除原作者与 PCL Community 署名、著作权登记号与举报邮箱，只保留本项目版本信息
- 删除未接入的 Mirror 酱 CDK：更新源、配置项、网络授权、CI 工作流与多语言文案一并清掉
- 署名统一为 bsk10ww，仓库外链改回本仓库
- 修复启动闪屏只有 13×13 像素的问题（原来直接拿 `icon.ico` 当闪屏，WPF 按 ico 第一帧的 16px 开窗）
- 更新检查触发 GitHub 限流（403）时提示「稍后再试」，不再误报「仓库地址填错了」

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.0_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

## 已知问题

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
a907689062ff7435068c16883c7f2c019e3e6cf10fe57864e304f942c54961f5
```

---

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
