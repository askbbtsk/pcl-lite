"""Path containment helpers shared by the debug MCP and its unit tests."""

from pathlib import Path


def resolve_within(root: str | Path, candidate: str | Path) -> Path:
    """Resolve a candidate and reject paths outside the resolved root.

    This checks path components after resolving ``..`` and symlinks.  A string
    prefix check would incorrectly accept sibling paths such as ``repo-evil``.
    """
    resolved_root = Path(root).resolve()
    supplied = Path(candidate)
    resolved = (resolved_root / supplied).resolve()
    try:
        resolved.relative_to(resolved_root)
    except ValueError as ex:
        raise ValueError("path is outside the configured root") from ex
    return resolved
