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
