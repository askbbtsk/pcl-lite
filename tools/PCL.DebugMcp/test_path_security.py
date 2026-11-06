import tempfile
import unittest
from pathlib import Path

from path_security import resolve_within


class PathSecurityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "repo"
        self.root.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def test_allows_root_and_descendants(self):
        nested = self.root / "src" / "file.cs"
        nested.parent.mkdir()
        nested.touch()

        self.assertEqual(self.root.resolve(), resolve_within(self.root, "."))
        self.assertEqual(nested.resolve(), resolve_within(self.root, "src/file.cs"))

    def test_rejects_parent_traversal(self):
        with self.assertRaises(ValueError):
            resolve_within(self.root, "../outside.txt")

    def test_rejects_sibling_with_matching_string_prefix(self):
        sibling = self.root.with_name(self.root.name + "-evil")
        sibling.mkdir()

        with self.assertRaises(ValueError):
            resolve_within(self.root, sibling / "secret.txt")

    def test_rejects_symlink_escape(self):
        outside = Path(self.temp.name) / "outside"
        outside.mkdir()
        link = self.root / "linked"
        try:
            link.symlink_to(outside, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("symlink creation is unavailable")

        with self.assertRaises(ValueError):
            resolve_within(self.root, "linked/secret.txt")


if __name__ == "__main__":
    unittest.main()
