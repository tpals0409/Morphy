import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('publish_release', Path(__file__).with_name('publish-release.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
release = {'version': '0.1.5', 'path': '/releases/Morphy-0.1.5-win-x64-Setup.exe', 'sha256': 'a' * 64, 'bytes': 123}
files = [(Path('codec.zip'), 'application/zip'), (Path('setup.exe'), 'application/octet-stream')]


class Publishing(unittest.TestCase):
    def test_feed_is_changed_only_after_all_uploads(self):
        events = []
        with patch.object(module, 'd1', side_effect=lambda sql: events.append(sql) or []), \
             patch.object(module.subprocess, 'run', side_effect=lambda *a, **kw: events.append('upload')):
            module.publish('windows', release, files)
        self.assertTrue(events[0].startswith('SELECT'))
        self.assertEqual(events[1:3], ['upload', 'upload'])
        self.assertTrue(events[3].startswith('INSERT'))

    def test_failed_upload_does_not_change_feed(self):
        with patch.object(module, 'd1', return_value=[]) as db, \
             patch.object(module.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'upload')):
            with self.assertRaises(subprocess.CalledProcessError):
                module.publish('windows', release, files)
        self.assertEqual(db.call_count, 1)

    def test_identical_retry_does_not_upload_or_change_timestamp(self):
        with patch.object(module, 'd1', return_value=[release]) as db, \
             patch.object(module.subprocess, 'run') as upload:
            module.publish('windows', release, files)
        self.assertEqual(db.call_count, 1)
        upload.assert_not_called()

    def test_collision_and_downgrade_fail_before_upload(self):
        for existing in [{**release, 'sha256': 'b' * 64}, {**release, 'version': '0.1.6'}]:
            with self.subTest(existing=existing), patch.object(module, 'd1', return_value=[existing]), \
                 patch.object(module.subprocess, 'run') as upload:
                with self.assertRaises(SystemExit):
                    module.publish('windows', release, files)
                upload.assert_not_called()


if __name__ == '__main__':
    unittest.main()
