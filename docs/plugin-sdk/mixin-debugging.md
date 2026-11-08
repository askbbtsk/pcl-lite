# Mixin 目标定位与调试工作流

本专题帮助插件作者和 AI 对当前 PCL Lite 构建编写可验证的启动器补丁。先读 AI 总入口 [`README.md`](README.md)，再按需参照 [Mixin 声明与约束](mixins.md)。Mixin 是进程内编译代码，目标变更或匹配不确定时应停止安装，不要靠扩大匹配范围“试到能用”。

## 1. 固定要支持的启动器版本

记录当前启动器版本和 commit/发行包哈希。插件清单通过 `pclCoreVersion` 声明支持范围；不要把一个开发构建上的成功推断成其他版本兼容。升级启动器后重新跑目标测试。未识别或超出范围时按兼容性指南阻止或要求用户确认。

## 2. 从当前源码解析目标

先从触发行为开始搜索，而不是猜类型名：

```powershell
rg -n '可见文本|日志关键字|调用的 UI 文案' 'Plain Craft Launcher 2' PCL.Core
```

记录目标类型完整名称、程序集、方法声明、可见性、静态/实例状态、参数顺序和返回类型。重载方法必须按参数类型精确指定；本引擎对开放泛型和 ref-return 边界目标有限制。接着检查调用者和所有影响路径，判断改动是否会碰到启动流程、Bedrock 或 CIK；Bedrock/CIK 现有逻辑必须保持不变。

## 3. 检查实际 IL 和注入点

源码不等于 Release IL：优化、编译器版本、构建配置会改变常量、局部变量、分支和调用形式。对准备支持的实际发行程序集用 ILSpy 或同等 IL 查看器检查目标方法，记录目标指令（opcode）、被调用成员完整签名、字段/常量、局部变量索引和分支。源代码修改后重新查看 IL；不要把旧版本截图或调试构建当作当前证据。

把检查结果映射到一个明确的选择器：`MixinAt`、`Target`、`Opcode`、`Ordinal` 和/或 `Slice`。优先选最具体的成员签名和最窄的 Slice；声明 `Require`/`Expect` 以便目标漂移时显式失败。避免只凭“附近有个相似调用”使用无约束的 `Redirect` 或常量匹配。

## 4. 为每个操作构造可重复测试

在 `PCL.Core.Test/Plugins/MixinRuntimeTests.cs` 增加独立、`NoInlining` 的目标夹具，确保测试目标有可预测的 IL。每个操作至少断言：

- 应用前后的目标结果或副作用；
- 精确重载、操作码、序号和 Slice 只命中预期指令；
- 缺失/多重匹配时的 `Require`、`Expect`、`Allow` 结果；
- 与另一个补丁冲突时的顺序、诊断及失败回滚；
- `LocalCapture`、ref/out 和值类型 receiver 等相关栈/类型边界；
- `Dispose` 或配置回滚后原始目标行为恢复。

不要把多个待验证操作塞进一个复杂 handler；分开应用和断言可让失败定位到具体选择器。目标夹具需要复现线上方法的关键栈形状，不能仅测一个返回值相同但 IL 不同的替代方法。

```powershell
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~MixinRuntimeTest
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~PluginMixinPackageRuntimeTests
```

## 5. 验证签名包和实际发行版

测试单元运行时后，构建并签名 `.pclplugin`，在独立的启动器数据目录安装。先运行 safe mode 确认故障恢复路径，再正常启动检查插件页状态和日志。`PluginRuntimeManager` 会记录配置应用结果与警告；搜索该插件 ID 对应的 `插件 Mixin 已应用`、`Mixin 诊断`、`加载或应用 Mixin 失败` 信息，并对照匹配数和目标方法。

最后在每个声明支持的 Release 启动器版本中重复安装、启动、功能行为、禁用/卸载及安全模式检查。真实 UI dispatcher 和真实 Minecraft 进程行为不由 `MixinRuntimeTests` 的纯运行时夹具证明。如果改动可能触及启动路径，执行对应回归并确认 Bedrock/CIK 行为未变。若发行版验证失败，撤销/禁用该补丁或收窄版本范围；不要静默扩大注入范围。

## 失败诊断顺序

1. 包签名和清单是否有效，Mixin JSON 是否在已签名包内。
2. 当前运行的启动器版本是否在声明的 `pclCoreVersion` 范围中。
3. 类型解析结果是否是期望程序集中的唯一类型；是否使用正确的嵌套类型全名。
4. 目标方法是否选择了正确的重载，handler 的 receiver/参数/返回类型是否匹配。
5. 目标 IL 中选择器是否命中，命中数量是否符合 `Require`/`Expect`/`Allow`。
6. 是否存在优先级冲突、前置插件失败或配置级回滚；查看对应插件 ID 的启动器日志。
7. 用 safe mode 恢复启动器，先禁用问题插件，再对相同发行版重跑最小测试。
