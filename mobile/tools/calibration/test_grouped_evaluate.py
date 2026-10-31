import unittest
from grouped_evaluate import evaluate


def row(song, album, artists, independent=True):
    return dict(song_id=song, album_id=album, artist_ids=artists,
        labels={"VOICE": "VOCAL", "MOOD": "?", "GENRE": "NON_CLASSICAL"},
        automatic={"VOICE": "VOCAL", "MOOD": "SPORT", "GENRE": "CLASSICAL"},
        provenance=dict(labels="human_reviewed", scores="desktop_production_v5_onnx", independent=independent))

class GroupedEvaluationTest(unittest.TestCase):
    def test_reused_calmness_label_never_counts_as_independent_test(self):
        reused = row("user-recording", "album", ["artist"])
        reused["labels"]["CALMNESS"] = "CALM"
        reused["automatic"]["CALMNESS"] = "CALM"
        reused["provenance"]["used_for_tuning"] = True
        result = evaluate([reused, row("related-recording", "album", ["artist"])])
        self.assertEqual(2, result["results"]["regression"]["songs"])
        self.assertEqual(0, result["results"]["test"]["songs"])
        self.assertEqual(0, result["results"]["calibration"]["songs"])

    def test_calmness_is_independent_and_old_inputs_remain_unknown(self):
        old = row("old", "x", ["one"], False)
        new = row("new", "y", ["two"], False)
        new["labels"]["CALMNESS"] = "CALM"
        new["automatic"]["CALMNESS"] = "NON_CALM"
        metrics = evaluate([old, new])["results"]["regression"]["dimensions"]["CALMNESS"]
        self.assertEqual(1, metrics["unknown"])
        self.assertEqual(1, metrics["fn"])
        new["labels"]["CALMNESS"] = "QUIET"
        with self.assertRaises(ValueError): evaluate([new])
    def test_transitive_artist_album_and_duplicates_never_leak(self):
        rows = [row("a", "x", ["one"]), row("b", "x", ["two"]), row("c", "y", ["two"])]
        result = evaluate(rows + [rows[0]])
        self.assertEqual(1, result["components"])
        self.assertEqual(1, result["duplicates"])
        self.assertEqual(1, len({r["split"] for r in result["manifest"]}))
        split = result["manifest"][0]["split"]
        self.assertEqual(3, result["results"][split]["dimensions"]["MOOD"]["unknown"])
        self.assertEqual(3, result["results"][split]["dimensions"]["GENRE"]["fp"])
        self.assertIsNone(result["results"][split]["dimensions"]["MOOD"]["recall"])
    def test_weak_labels_contaminate_entire_component(self):
        result = evaluate([row("a", "x", ["one"], False), row("b", "x", ["two"])])
        self.assertEqual(2, result["results"]["regression"]["songs"])
        self.assertEqual(0, result["results"]["test"]["songs"])
    def test_deterministic_split_and_conflicting_duplicates_rejected(self):
        rows = [row(str(i), "album"+str(i), ["artist"+str(i)]) for i in range(100)]
        first = evaluate(rows)
        second = evaluate(list(reversed(rows)))
        self.assertEqual({r["song_id"]: r["split"] for r in first["manifest"]}, {r["song_id"]: r["split"] for r in second["manifest"]})
        self.assertTrue(0 < first["results"]["test"]["songs"] < 100)
        other = row("0", "album0", ["artist0"])
        other["labels"]["VOICE"] = "INSTRUMENTAL"
        with self.assertRaises(ValueError): evaluate(rows + [other])

if __name__ == "__main__": unittest.main()
