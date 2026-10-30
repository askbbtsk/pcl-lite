<summary>好友列表换成 Minecraft 皮肤头像，不再是灰色的默认用户图标。头像源用的是 Lunar 官方启动器自己的接口，直连可通、无皮肤回默认皮肤、带本地缓存。</summary>

PCL Lite 1.6.3 快照版 7（snapshot-7）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「最新」也会收到，「稳定版」「预览体验」不会。
>
> 已经是 `1.6.3-snapshot-6` 的话，这次只有一个改动：好友列表显示皮肤头像。

## 换成真头像

好友列表以前左边是个灰色的默认用户图标（在线 `user-round`、离线 `user`），
现在换成各自的 Minecraft 皮肤头像。

头像源用的是 **Lunar 官方启动器自己那个接口**：

```
https://skins.mcstats.com/face/<uuid>?size=64
```

官方在「Hosted World」里就用它取主持人头像（`size=128`）。选它而不是
mc-heads / crafatar 这类第三方，原因是实测：

| 源 | 结果 |
| --- | --- |
| `skins.mcstats.com` | 200，约 1 秒，无皮肤时回默认 Steve，**不会 404** |
| `mc-heads.net` | 403（Cloudflare 拦非浏览器请求） |
| `crafatar.com` | 12 秒超时，国内基本不通 |
| `littleskin.cn` | 500 |
| `skin.prinzeugen.net` | 404 |

因为「没有皮肤也返回一张默认皮肤」是它的特性，不需要额外准备兜底图。

## 缓存与加载

走 PCL 已有的 `MyImage` 缓存（`Cache/Images/<md5(url)>.png`，14 天过期）：

1. 进页面先查本地文件在不在，在就直接用 —— 第二次进页面完全不发网络请求
2. 没有才排队下载，下载期间先显示原来的图标
3. 下载完只替换对应那一行，不整表重建 —— 重建会把正在点的选中态一起刷掉
4. 下载失败不记住失败状态，下次刷新还能再试一次

## 校验值

```
SHA-256  DD8B08B5F6792CDD6B4144DEA48A84AD5789D53481AC3B2813CFC9A538D9F7A3
```

## 已知问题

- 好友/私聊依赖 Lunar 后端的私有协议，官方随时可能改字段；改了要重新逆向
- 头像来自 `skins.mcstats.com`，如果哪天这个域名挂了，会退回原来的默认图标
- 服务端下限会随官方启动器更新继续往上抬；PCL 被拒时会自动升版本重连，能自愈
- 结束游戏是直接杀进程，不保证存档落盘（与官方行为一致）
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
