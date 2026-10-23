import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('windows_version', Path(__file__).with_name('windows-version.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Versions(unittest.TestCase):
    def test_initial_release_is_newer_than_local_installs(self):
        self.assertEqual(module.choose('0.1.4', '0.1.0'), '0.1.5')
        self.assertEqual(module.choose('0.1.4'), '0.1.5')

    def test_published_version_wins_and_compares_numerically(self):
        self.assertEqual(module.choose('0.1.4', '0.1.9'), '0.1.10')
        self.assertEqual(module.choose('0.1.4', '1.0.0'), '1.0.1')

    def test_explicit_minor_release(self):
        self.assertEqual(module.choose('0.1.4', '0.1.9', '0.2.0'), '0.2.0')

    def test_invalid_duplicate_and_downgrade_versions_fail(self):
        for requested in ['0.1.4', '0.1.0', 'v0.2.0', '0.2', '0.2.0-beta', '0.1.65535']:
            with self.subTest(requested=requested), self.assertRaises(ValueError):
                module.choose('0.1.4', '0.1.4', requested)
        with self.assertRaises(ValueError):
            module.choose('0.1.4', 'invalid')


if __name__ == '__main__':
    unittest.main()
