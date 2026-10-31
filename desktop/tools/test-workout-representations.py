"""Representation data/geometry controls, not human workout recognition tests."""
import copy
import importlib.util
from pathlib import Path
import unittest

import numpy as np

spec = importlib.util.spec_from_file_location("representations", Path(__file__).with_name("research-workout-representations.py"))
research = importlib.util.module_from_spec(spec)
spec.loader.exec_module(research)


def fixture(directions=((1, 0), (.99, .1), (0, 1))):
    records = []
    feedback = []
    for tid, direction in enumerate(directions, 1):
        vectors = {}
        for name, dim in research.DIMENSIONS.items():
            v = np.zeros(dim)
            v[:2] = direction
            vectors[name] = research.unit(v).tolist()
        segments = [dict(context=i, condition=name, vectors=copy.deepcopy(vectors))
                    for i in range(3) for name, _, _ in research.CONDITIONS]
        records.append(dict(trackId=tid, segments=segments))
        feedback.append(dict(trackId=tid, label="SPORT" if tid < 3 else "NON_SPORT",
            artist="A" if tid < 3 else "B", label_source="explicit_user_feedback",
            independent=False, used_for_tuning=True))
    return dict(contract="workout_representation_research_v1", protocolSha256="test",
        state="completed", cacheCompatible=False, model="MTT_musicnn",
        checkpointSha256=research.CHECKPOINT_SHA256,
        requestedRecordCount=len(records), records=records), dict(records=feedback)


class RepresentationControls(unittest.TestCase):
    def test_float_gain_is_removed_by_fixed_preprocessing(self):
        pcm = np.sin(np.arange(480000)*.071)*.02
        base, _ = research.normalized_pcm(pcm, 0)
        for gain in (-6, 6):
            value, _ = research.normalized_pcm(pcm, gain)
            np.testing.assert_array_equal(base, value)
        self.assertAlmostEqual(.1, float(np.sqrt(np.mean(base.astype(float)**2))), places=8)

    def test_silence_truncated_and_nonfinite_audio_are_not_features(self):
        for value in (np.zeros(480000), np.ones(479999), np.full(480000, np.nan)):
            with self.assertRaises(ValueError):
                research.normalized_pcm(value, 0)

    def test_cosine_has_no_zero_or_nonfinite_direction(self):
        for value in ([0, 0], [1, np.inf], [[1, 0], [0, 0]]):
            with self.assertRaises(ValueError):
                research.unit(value)
        with self.assertRaises(ValueError):
            research.pool(np.ones((9, 200)), 200)

    def test_no_same_recording_segment_leaks_into_neighbor_candidates(self):
        report, labels = fixture()
        result = research.audit(report, labels, "test")
        for summary in result["representations"].values():
            self.assertEqual(6, summary["baseContextLabelAgreement"])
            self.assertFalse(summary["neighborhoodGate"])
            self.assertTrue(summary["stabilityGate"])
            for r in summary["records"]:
                self.assertTrue(all(q["neighborId"] != r["trackId"] for q in r["queries"]))
        self.assertFalse(result["identityGeneralizationEstablished"])
        self.assertEqual(1, result["positiveArtistCount"])

    def test_tied_neighbors_do_not_count_as_support(self):
        report, labels = fixture(((1, 0), (1, 0), (1, 0)))
        result = research.audit(report, labels, "test")
        for s in result["representations"].values():
            self.assertFalse(s["neighborhoodGate"])
            self.assertEqual(0, s["baseContextLabelAgreement"])

    def test_shift_to_opposite_neighborhood_fails_stability(self):
        report, labels = fixture()
        for name in research.DIMENSIONS:
            report["records"][0]["segments"][3]["vectors"][name] = report["records"][2]["segments"][0]["vectors"][name]
        result = research.audit(report, labels, "test")
        for s in result["representations"].values():
            self.assertFalse(s["stabilityGate"])

    def test_incomplete_wrong_protocol_and_production_cache_are_rejected(self):
        report, _ = fixture()
        for key, value in (("state", "incomplete"), ("protocolSha256", "other"),
                           ("cacheCompatible", True), ("requestedRecordCount", 8),
                           ("model", "MSD_musicnn"), ("checkpointSha256", "wrong")):
            invalid = {**report, key: value}
            with self.assertRaises(ValueError):
                research.validate_report(invalid, "test")

    def test_missing_duplicate_or_invalid_vectors_are_rejected(self):
        original, _ = fixture()
        variants = []
        report = copy.deepcopy(original)
        report["records"][0]["segments"].pop()
        variants.append(report)
        report = copy.deepcopy(original)
        report["records"][0]["segments"][1] = report["records"][0]["segments"][0]
        variants.append(report)
        for vector in ([1], [float("nan")]*200, [0]*200, [1]*200):
            report = copy.deepcopy(original)
            report["records"][0]["segments"][0]["vectors"]["penultimate"] = vector
            variants.append(report)
        for report in variants:
            with self.assertRaises(ValueError):
                research.validate_report(report, "test")

    def test_unknown_or_proxy_labels_do_not_become_workout_truth(self):
        report, labels = fixture()
        for key, value in (("label_source", "danceability"), ("independent", True), ("used_for_tuning", False),
                           ("label", "UNKNOWN"), ("artist", "")):
            invalid = copy.deepcopy(labels)
            invalid["records"][0][key] = value
            with self.assertRaises(ValueError):
                research.audit(report, invalid, "test")
        labels["records"].pop()
        with self.assertRaises(ValueError):
            research.audit(report, labels, "test")

    def test_overlapping_cue_and_clamped_contexts_are_rejected(self):
        sampling = research.load_module("sampling_control", Path(__file__).with_name("research-workout-beats.py"))
        row = dict(track=dict(id=1, durationMs=180000), explanation=dict(evidence=dict(segments=[
            dict(start_ms=t, end_ms=t+10000) for t in (30000, 80000, 130000)])))
        self.assertEqual([(20000, 50000), (70000, 100000), (120000, 150000)], research.contexts(row, sampling))
        for field in ("cue", "overlap", "edge"):
            invalid = copy.deepcopy(row)
            if field == "cue":
                invalid["track"]["cueTrackNo"] = 1
            else:
                invalid["explanation"]["evidence"]["segments"][0] = dict(
                    start_ms=75000 if field == "overlap" else 0,
                    end_ms=85000 if field == "overlap" else 10000)
            with self.assertRaises(ValueError):
                research.contexts(invalid, sampling)


if __name__ == "__main__":
    unittest.main()
