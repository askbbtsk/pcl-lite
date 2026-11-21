# 发布到 PCL Lite 插件市场

PCL Lite 的插件市场读取固定仓库里的一个静态索引：

```
https://raw.githubusercontent.com/bedkillerspacex-boop/Pcl-Lite-service/main/plugins/index.json
```

索引里每个插件指向一个 **GitHub Release 资源**，并带 SHA-256 校验。所以完整流程是：

1. 插件代码放到一个公开 GitHub 仓库
2. 打 tag，发布 Release，上传 `.pclplugin` 包
3. 计算包的 SHA-256
4. 向 `bedkillerspacex-boop/Pcl-Lite-service` 提交 PR，在 `plugins/index.json` 里加一条记录
5. 合并后市场里就能看到

## 1. 打包

本地执行（需要 pcl-lite 源码里的 SDK）：

```powershell
# 设置 SDK 所在目录（包含 PCL.Plugin.SDK 的 pcl-lite 源码根目录）
$env:PCL_LITE_DIR = "D:\桌面\pcl-lite-src"

.\pack.ps1 -Tag v1.0.0 -Owner <你的GitHub用户名> -Repo imcp-pcl-plugin
```

脚本会：

- 编译插件
- 生成 `dist/imcproxy.control-v1.0.0.pclplugin`（zip 内含 `plugin.json` 与 DLL）
- 计算 SHA-256
- 直接打印可粘贴的 `plugins/index.json` 条目

`.pclplugin` 本质是 zip，根目录必须同时有：

```
plugin.json
PCL.Plugin.ImcProxy.dll
```

## 2. 发布 Release

在 GitHub 仓库里创建 Release：

- tag：`v1.0.0`（与 `pack.ps1 -Tag` 一致）
- 资源：上传 `imcproxy.control-v1.0.0.pclplugin`

资源地址必须是这种形式，市场才会接受：

```
https://github.com/<owner>/<repo>/releases/download/v1.0.0/imcproxy.control-v1.0.0.pclplugin
```

仓库里已带 `.github/workflows/release.yml`，推送 `v*` tag 会自动构建并上传资源，并在 Actions 摘要里打印 SHA-256。

## 3. 索引条目

`plugins/index.json` 的结构：

```json
{
  "schemaVersion": 1,
  "plugins": [
    {
      "id": "imcproxy.control",
      "name": "IMCProxy",
      "version": "1.0.0",
      "author": "IMCProxy",
      "description": "在 PCL Lite 中下载、启动并配置 IMCProxy-Free，通过 HTTP API 控制，不显示控制台。",
      "packageUrl": "https://github.com/<owner>/<repo>/releases/download/v1.0.0/imcproxy.control-v1.0.0.pclplugin",
      "website": "https://github.com/<owner>/<repo>",
      "sha256": "<pack.ps1 输出的 64 位十六进制>"
    }
  ]
}
```

## 4. 市场会校验这些

| 项 | 规则 |
| --- | --- |
| `schemaVersion` | 必须是 `1` |
| `id` | ≤64 字符，只允许小写字母、数字、`.`、`-`；必须与 `plugin.json` 的 `id` 完全一致 |
| `version` | 合法 SemVer，必须与 `plugin.json` 的 `version` 一致 |
| `name` / `author` | 非空，≤128 字符 |
| `description` | ≤4096 字符 |
| `packageUrl` | 必须是 HTTPS 的 GitHub Release 资源地址，文件名以 `.pclplugin` 结尾 |
| `sha256` | 必填，64 位十六进制，与实际包一致 |
| `icon` / `website` | 可选，必须是 HTTPS URL |

## 5. 提交 PR

```bash
git clone https://github.com/bedkillerspacex-boop/Pcl-Lite-service
cd Pcl-Lite-service
# 编辑 plugins/index.json，把上面的条目加进 plugins 数组
git checkout -b add-imcproxy
git commit -am "Add IMCProxy plugin"
git push origin add-imcproxy
```

然后在 GitHub 上创建 Pull Request，等维护者合并。合并后重启 PCL Lite 即可在插件市场看到 IMCProxy。

## 常见失败原因

- 包名不是 `.pclplugin` 结尾
- `packageUrl` 不是 `https://github.com/<owner>/<repo>/releases/download/<tag>/<asset>` 形式
- `sha256` 与上传的包不一致（重新打包后忘记更新索引）
- 索引里的 `id` / `version` 与包内 `plugin.json` 不一致
- 版本号不是 SemVer（例如写成 `1.0` 而不是 `1.0.0`）
