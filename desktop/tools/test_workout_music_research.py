"""Numerical safeguards for offline measurements; no user music or weights needed."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


beats = module("beat_research", "research-workout-beats.py")
pulse = module("pulse_research", "research-workout-pulse.py")


def clicks(bpm):
    times = np.arange(3000) / 100
    return sum(np.exp(-.5*((times-t)/.025)**2) for t in np.arange(.4, 30, 60/bpm))


class SamplingTests(unittest.TestCase):
    def test_same_centres_longer_context_and_bounded_short_track(self):
        segments = [dict(start_ms=30000, end_ms=40000), dict(start_ms=50000, end_ms=60000)]
        self.assertEqual([(30000, 40000), (50000, 60000)], beats.intervals(100000, segments, None))
        self.assertEqual([(20000, 50000), (40000, 70000)], beats.intervals(100000, segments, 30))
        self.assertEqual([(0, 12000)], beats.intervals(12000, [dict(start_ms=0, end_ms=12000)], 30))

    def test_invalid_bounds_are_rejected_instead_of_sampling_wrong_recording(self):
        for duration, segment in [(0, dict(start_ms=0, end_ms=100)), (1000, dict(start_ms=-1, end_ms=100)),
                                  (1000, dict(start_ms=500, end_ms=2000))]:
            with self.assertRaises(ValueError):
                beats.intervals(duration, [segment], 30)

    def test_atomic_reports_leave_no_partial_json(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder)/"report.json"
            beats.write_report(target, dict(cacheCompatible=False, records=[]))
            beats.write_report(target, dict(cacheCompatible=False, records=[dict(trackId=1)]))
            self.assertEqual(1, json.loads(target.read_text())["records"][0]["trackId"])
            self.assertFalse(target.with_name("report.json.tmp").exists())


class PulseTests(unittest.TestCase):
    def test_known_pulse_and_range_endpoints(self):
        for tempo in (40, 80, 120, 178, 240):
            result = pulse.pulse_candidates(clicks(tempo), 100)
            best = max(result, key=lambda c: c["product"])
            self.assertLessEqual(abs(best["bpm"]-tempo), 1)

    def test_empty_interbeats_do_not_justify_doubling_slow_pulse(self):
        result = {c["bpm"]: c for c in pulse.pulse_candidates(clicks(80), 100)}
        self.assertGreater(result[160]["coherence"], .5)  # Fourier harmonic exists.
        self.assertLess(result[160]["correlation"], 0)  # No intervening sound events.
        self.assertEqual(0, result[160]["product"])
        self.assertGreater(result[80]["product"], .5)

    def test_envelope_scale_does_not_change_normalized_salience(self):
        a = pulse.pulse_candidates(clicks(120), 100)
        b = pulse.pulse_candidates(clicks(120)*.1, 100)
        np.testing.assert_allclose([c["product"] for c in a], [c["product"] for c in b], atol=1e-12)

    def test_silence_and_short_context_do_not_manufacture_evidence(self):
        self.assertEqual([], pulse.pulse_candidates(np.zeros(1000), 100))
        self.assertEqual([], pulse.pulse_candidates(np.ones(300), 100))

    def test_bad_values_are_rejected(self):
        for envelope, fps in [([1, float("nan")], 100), ([-1, 2], 100), ([1, 2], 0)]:
            with self.assertRaises(ValueError):
                pulse.pulse_candidates(envelope, fps)


if __name__ == "__main__":
    unittest.main()
