# PCL-Lite Repository Agent Guidelines

## UI Copy & Localization Style
- For any UI text, tooltip, confirmation dialog, or localization edits (`PCL.Core/App/Localization/Languages/*.xaml` and hardcoded UI prompts), follow the project skill [.agents/skills/pcl-lite-ui-copy/SKILL.md](.agents/skills/pcl-lite-ui-copy/SKILL.md).
- Keep normal feature copy short, plain, and free of internal implementation jargon (e.g., WGC, Pixel Shader, DIP, sampling/drag internals), while keeping crash analysis, errors, and debug diagnostics detailed.
- Never alter code logic when asked only to adjust copy; verify UI copy matches actual code behavior.

## Collaboration & Communication Principles

### I Have ADHD
- Keep replies, plans, and explanations **concise, scannable, and low-friction**.
- Lead with the conclusion/action first, use short bullet points and **bold keywords**, and avoid walls of text.

### Grill Me
- When planning or facing ambiguous/underspecified requirements, **interview the user relentlessly one question at a time** before implementing.
- Walk down each branch of the decision tree sequentially and **always include a recommended option** for each question.
- Do not ask questions that can be answered directly by reading the codebase first.

### Stop That Shit
- 采用 [Stop That Shit](https://github.com/lennney/stop-that-shit) 的[工程判断规则](https://github.com/lennney/stop-that-shit/blob/main/skills/stop-that-shit/SKILL.md)：完成当前任务，让复杂度来自真实需求。
- 严守用户授权范围。审查、回答和监控默认只读；实施时补齐必要的调用方、数据迁移、测试和文档，不顺手整理无关模块。
- 优先采用仓库现有实现、标准库和已有依赖；只有具体输入、失败路径或现有兼容承诺存在缺口时才扩展方案，不为假想需求增加抽象、文件或依赖。
- 防护必须有实际作用：能发现问题，并改变拒绝、恢复或诊断结果。不要生成无人使用的摘要、清单或重复校验；不得借此删除必要的认证、完整性检查、恢复和兼容措施。
- 按改动影响和项目要求验证；证据仍适用于最终状态时直接复用。没有代码变化、新问题或验收变化，不重复构建、测试、审查，也不额外拉代理“再确认一遍”。
- 操作尚未结束时先等待结果，不重复提交、发布或启动依赖它的操作。常规选择从代码和上下文解决，不重复索取已获得的授权。
- 完成结果和必要验证后立即交付。回复只写结果、相关验证及影响使用的限制，不堆内部过程、无用辩护或防御性废话。
