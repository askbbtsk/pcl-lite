PCL Lite 1.1.0。

## 本次内容

- **修复版本号显示错误**：1.0.0 的界面仍显示 `0.1.0-beta`，原因是 `metadata.json` 里的版本没跟着改。
  现在界面、文件属性、更新检查三处统一为 `1.1.0`
- **微软正版登录恢复可用**：换用已验证可用的 OAuth 应用凭据，修复 1.0.0 中授权流程走到最后一步
  （`login_with_xbox`）稳定返回 403、无法进入游戏档案的问题
- **遥测彻底移除**：
  - 删除 Sentry 崩溃上报与异常采集，以及全部设备环境探测（启动器 ID、系统版本、内存、
    IPv6 状态、NAT 映射行为），其中包含读取注册表判断是否使用过其它启动器、扫描 `.hmcl` / `BakaXL` 目录的行为
  - 删除每次启动都会真实执行的 STUN NAT 探测（`stun.miwifi.com`）
  - 设置页的「匿名数据统计」开关、首次运行的遥测同意弹窗、4 种语言共 16 条相关文案一并清掉
  - 联机大厅的房间登记只保留房间发现必需的字段，不再上传联机账号邮箱、最近登录 IP 与机器识别码；
    登记失败不再阻断联机
- 欢迎页文案改为「没有任何遥测与崩溃上报」，状态行显示「遥测：已移除」
- 更新检查继续保留 Atom 订阅源降级与多个下载反代，接口被限流时不再直接报错

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.1_x64.exe` |

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
9c9dde7e1f371fccffbbbb2a82db04e2a6e72b676ed0902f752ab819cfccc31a
```

---

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
