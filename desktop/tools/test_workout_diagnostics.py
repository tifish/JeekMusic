import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

TOOL = Path(__file__).with_name("evaluate-workout-diagnostics.py")
spec = importlib.util.spec_from_file_location("diagnostic_eval", TOOL)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiagnosticEvaluationTests(unittest.TestCase):
    def test_missing_diagnostic_features_never_become_cache_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            subprocess.run([sys.executable, str(TOOL), str(root), "--output", str(root/"report.json")], check=True, capture_output=True)
            report = json.loads((root/"report.json").read_text(encoding="utf-8"))
            self.assertEqual(6, len(report["rows"]))
            self.assertTrue(all(r["missingDiagnosticEvidence"] and r["used_for_tuning"] and not r["independent"] for r in report["rows"]))
            self.assertFalse(report["independentEvaluation"])

    def test_recording_feedback_keeps_corrected_positive_and_explicit_sources(self):
        labels = json.loads((TOOL.parents[2]/"shared/testcases/workout-feedback.json").read_text(encoding="utf-8"))["records"]
        self.assertEqual(5, sum(r["label"] == "SPORT" for r in labels))
        self.assertEqual("SPORT", next(r for r in labels if r["trackId"] == 1155)["label"])
        self.assertEqual("NON_SPORT", next(r for r in labels if r["trackId"] == 1212)["label"])
        self.assertTrue(all(r["label_source"] == "explicit_user_feedback" and not r["independent"] and r["used_for_tuning"] for r in labels))

    def test_cache_impact_ignores_stale_versions_and_keeps_first_pass_on_extra_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/"library.db"
            frame = lambda e: dict(calm=.003, energy=e)
            good = dict(additional=False, mean={}, frames=[frame(.3)]*4+[frame(.001)]*6)
            # mean must be a present object; a failed additional round cannot rescue median.
            extra = dict(additional=True, mean={}, frames=[frame(.3)]*10)
            fail = dict(additional=True, mean=None, frames=[], error="model:IOException")
            evidence = dict(contract=5, feature_complete=True, segments=[good,extra,fail])
            data = json.dumps(dict(analysis_evidence=json.dumps(evidence)))
            with sqlite3.connect(path) as db:
                db.executescript("create table track(track_key text,mtime integer,size integer); create table track_features(track_key text,analyzer_version integer,data text,signature text,failed integer);")
                for key, version, signature in [("good",5,"1:2"),("old",4,"1:2"),("stale",5,"2:2")]:
                    db.execute("insert into track values(?,1,2)",(key,))
                    db.execute("insert into track_features values(?,?,?,?,0)",(key,version,data,signature))
            db.close()
            report = module.cache_impact(path)
            self.assertEqual(1, report["usable"])
            self.assertEqual(0, report["baselineSport"])
            self.assertEqual(1, report["anyRun3Adds"])
            self.assertEqual(0, report["twoSegmentsRun3Adds"])
            self.assertEqual(1, report["missingNewEvidence"])


if __name__ == "__main__":
    unittest.main()
