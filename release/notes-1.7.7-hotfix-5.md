# PCL Lite 1.7.7-hotfix-5

## Lunar native-library updates

- Compare extracted native files by SHA-256 and skip files whose content already matches. This avoids needlessly replacing DLLs that a running Lunar/Minecraft game is using.
- Extract archives into a temporary staging directory, then replace only changed files using a sibling temporary file and atomic move.
- If Windows prevents replacement because a native file is still in use, preserve the existing file and show a localized message naming the affected files and asking the user to close the related game and retry. The raw `being used by another process` exception is no longer presented as the user-facing error.
- Keep the original one-game/Lunar launch policy; this hotfix does not add multi-instance support.
- Changed the Lunar status text from “backend connected” to “friend system connected.”

## Validation

- Added targeted tests for skipping identical in-use files, preserving changed files when replacement is blocked, cleanup of temporary files, and successful retry after releasing the file.
- Targeted tests and the launcher CI build passed locally. GitHub CI verifies x64/ARM64 builds, full unit tests, and UI smoke; the release workflow signs both architectures.
- No UI or screen interaction was used during this hotfix.

