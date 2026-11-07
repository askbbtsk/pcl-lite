# 编译型 Mixin：修改启动器现有行为

Mixin 由插件程序集中的 C# 属性和处理器构成，不是脚本。启动器在主窗体创建前加载启用包并应用 Mixin 配置；普通 `IPclPlugin.StartAsync` 生命周期在 `WindowCreated` 后开始。Mixin 与宿主 API 插件共享同一个 `.pclplugin` 包和签名。`PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 会跳过插件和 Mixin 加载，可用于恢复启动器。

## 基本结构

1. 在插件项目中引用 `PCL.Plugin.SDK`，使用 `PCL.Mixin` 属性。
2. 在 `plugin.json` 声明 `mixinConfig`（单个）或 `mixinConfigs`（多个）。
3. 将 JSON 配置和程序集一并放入包并签名。
4. 通过实际打包后的插件，在测试版启动器上验证目标方法、冲突和恢复行为。

每份 Mixin JSON 限制为 256 KiB、1–512 个 Mixin 类型名；清单最多声明 64 份配置，配置路径不区分大小写且不能重复。配置中的 `package`、`plugin` 和每个类型名最长 256 个字符，`injectors.defaultRequire` 必须为 0–1000，类型名不能重复。配置文件必须在包内并被签名覆盖。

配置字段以 JSON 名称为准：

| 字段 | 类型 | 默认值 / 限制 |
|---|---|---|
| `required` | bool | `true`；仅表示补丁应用失败时是否使该插件失败 |
| `package` | string | 空字符串；类型名解析用的命名空间前缀，最多 256 字符 |
| `mixins` | string 数组 | 必填，1–512 个唯一类型名，每项最多 256 字符 |
| `priority` | int | 默认 `1000`；控制补丁顺序，当前没有额外数值范围校验 |
| `injectors.defaultRequire` | int | 默认 `1`，范围 0–1000 |
| `plugin` | string/null | 可选配置处理器类型名，最多 256 字符 |

清单属性名为 `mixinConfig`（单个路径）或 `mixinConfigs`（路径数组）。混合使用时路径总数也不能超过 64。无效/缺失 JSON 在安装或启动校验阶段拒绝；`required:false` 不会让损坏的配置文件变成可忽略错误。

示意清单：

```json
{
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.ExamplePlugin",
  "mixinConfigs": ["mixins/launcher.json"]
}
```

示意配置（目标与类名须按真实程序集填写）：

```json
{
  "required": true,
  "package": "Example.Plugin.Mixins",
  "mixins": ["ExampleMixin"],
  "priority": 1000,
  "injectors": { "defaultRequire": 1 }
}
```

示意处理器：

```csharp
using PCL.Mixin;

[Mixin("PCL.TargetType, PCL.Core")]
public static class ExampleMixin
{
    [Inject("TargetMethod", At = MixinAt.Head, Cancellable = true)]
    public static void BeforeTarget(CallbackInfo callback)
    {
        // 只在你确认目标版本与语义后修改 callback。
    }
}
```

以上目标名只是占位示例。必须对照当前启动器代码检查目标类型完整名称、程序集、方法重载、参数绑定和目标 IL。目标解析失败、匹配数量不符或处理器签名错误时，必需配置应让该插件补丁失败并回滚；可选配置可跳过并记录警告。

## 支持的操作类型

具体限制由 `PCL.Mixin` 运行时验证，能力包括：

- `Inject`：方法入口/返回点及指定 IL 匹配点注入处理器，可取消或修改结果。
- `Overwrite`：替换目标方法。
- `Redirect`：重定向调用、字段或构造指令。
- `ModifyArg` / `ModifyArgs`：修改某个调用参数或整组参数。
- `ModifyConstant`：修改目标方法中的常量。
- `ModifyVariable`：修改局部变量加载/存储值。
- `Accessor` / `Invoker`、`Shadow`、`Unique`：访问目标成员、调用目标方法和声明 Mixin 成员。
- `Slice`、优先级和匹配数量约束：收窄目标 IL 范围并检测意外匹配。

不要以“某属性可编译”推断某注入点有效。每个操作都应有针对目标方法的测试，包括匹配失败、重载歧义、冲突排序以及回滚。

## 安全、兼容和资源要求

- Mixin handler 在启动器进程内执行，和启动器拥有相同权限；它可以触达当前用户可访问的文件和进程。
- Mixin 会影响所有调用目标方法的路径。错误的 `Overwrite`、宽泛的 `Redirect` 或未约束的 IL 匹配可能导致启动崩溃。
- 包签名只证明当前包内容与签名时的字节一致；自签名不能证明作者身份，也不能限制代码行为。
- `required: true` 配置失败应回滚该插件注册的补丁；`required: false` 只用于确实可选的扩展，不要用它掩盖必需补丁故障。
- 通过版本检测明确支持的 PCL Core 版本；目标实现变化时应拒绝应用或要求用户确认，不要悄悄猜测 IL。
- 保持补丁少而窄。避免静态持有大对象/大图像，不要在 handler 中做阻塞 I/O、长计算或同步等待。
- 不改 Bedrock / CIK 现有逻辑。触碰 Minecraft 启动流程时，应增加明确的 CIK 不变回归断言。

必需配置的补丁应用失败会回滚该插件已应用的补丁；可选配置的补丁应用失败会记录警告并继续。安全模式会在应用补丁前跳过插件。`PluginMixinPackageRuntimeTests` 已通过真实签名包安装及加载路径验证 Mixin 应用、必需配置失败回滚和释放时恢复目标方法；它没有覆盖每种 Mixin 操作，也不证明任意已执行代码都能安全卸载。Mixin 没有沙箱。
