import importlib.util
from contextlib import closing
import os
import sqlite3
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("probe_legacy", Path(__file__).parents[1] / "scripts" / "probe-legacy-records.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class LegacyRecordEvidenceTests(unittest.TestCase):
    def test_exact_json_preservation_allows_new_rows_but_detects_changed_or_missing_history(self):
        temp_root = Path(os.environ['LOCALAPPDATA']) / 'Temp' / 'opencode' if 'LOCALAPPDATA' in os.environ else None
        with tempfile.TemporaryDirectory(dir=temp_root) as folder:
            db = Path(folder) / "jobs.sqlite"
            with closing(sqlite3.connect(db)) as c, c:
                c.executescript("CREATE TABLE packets(id TEXT, json TEXT); CREATE TABLE revisions(packet_id TEXT, revision INTEGER, json TEXT); CREATE TABLE artifacts(packet_id TEXT, name TEXT, json TEXT); INSERT INTO packets VALUES('p','{\"status\":\"approved\"}'); INSERT INTO revisions VALUES('p',1,'{\"status\":\"review\"}'); INSERT INTO artifacts VALUES('p','prediction','{\"type\":\"bol\"}');")
            baseline = probe.snapshot(db)
            with closing(sqlite3.connect(db)) as c, c:
                c.execute("INSERT INTO packets VALUES('new','{}')")
            probe.verify(baseline, probe.snapshot(db))
            with closing(sqlite3.connect(db)) as c, c:
                c.execute("UPDATE revisions SET json='{}'")
            with self.assertRaises(ValueError):
                probe.verify(baseline, probe.snapshot(db))
            with closing(sqlite3.connect(db)) as c, c:
                c.execute("UPDATE revisions SET json=?", ('{"status":"review"}',))
                c.execute("DELETE FROM artifacts")
            with self.assertRaises(ValueError):
                probe.verify(baseline, probe.snapshot(db))

    def test_empty_proof_is_rejected(self):
        with self.assertRaises(ValueError):
            probe.verify({"packets": {}, "revisions": {}, "artifacts": {}}, {})


if __name__ == "__main__":
    unittest.main()
