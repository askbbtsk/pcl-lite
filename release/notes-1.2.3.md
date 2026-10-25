<summary>更新页的应用图标改为 PCL Lite 自己的 LITE 图标，不再从上游 CDN 拉取 CE 的图标。</summary>

## 更新页图标

「软件更新」里新版本卡片左侧那张图，原来是从 `https://www.pclc.cc/img/pcl-ce/icon.webp` 在线拉的，也就是上游 PCL-CE 的图标——所以启动器明明叫 PCL Lite，更新卡片上却顶着别人的 logo。

现在改成读启动器自带的 `Images/icon.png`（蓝底白字 LITE）：

- 不再依赖外部 CDN，断网也能正常显示
- 上游以后换图标，我们不会跟着变成别人的样子
- 图标是打包在 exe 里的资源，加载即时，不用等网络

## 发布说明里加 `<summary>`

顺带补了更新摘要的标记。更新卡片上那句「开发者似乎忘记提供更新摘要了」是因为发布说明里没有 `<summary>...</summary>` 区块，启动器取不到摘要就退回了占位文案。从这一版起会正常显示一句话摘要。

## 校验

```text
SHA-256: 9b4e7eb168f561fb17895291f250d3c433a50b4fbc2ae7581f9dbcaf9bfe89f3
```
