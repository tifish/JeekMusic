"""Controlled signal checks. These do not validate human beat/workout recognition."""
import importlib.util
from pathlib import Path
import unittest
import numpy as np

spec = importlib.util.spec_from_file_location("accents", Path(__file__).with_name("research-workout-accents.py"))
accents = importlib.util.module_from_spec(spec)
spec.loader.exec_module(accents)


def train(weak=1, shift=0):
    envelope = np.zeros(3000)
    for i, t in enumerate(np.arange(.2+shift, 30, .5)):
        envelope[round(t*100)] = 1 if i % 2 == 0 else weak
    return envelope


class AccentResearchTest(unittest.TestCase):
    def test_equal_attacks_retain_quantile_ties(self):
        result = accents.accent_measures(train(), 100, [dict(bpm=120)])
        for quantile in ("0", "0.5", "0.75"):
            self.assertAlmostEqual(120, result[quantile]["intervalBpm"])
        self.assertAlmostEqual(1, result["phase"]["alternatingRatio"])

    def test_weak_subdivisions_expose_both_levels_without_selecting_octave(self):
        result = accents.accent_measures(train(.2), 100, [dict(bpm=120)])
        self.assertEqual(120, result["0"]["intervalBpm"])
        self.assertEqual(60, result["0.75"]["intervalBpm"])
        self.assertEqual(120, result["phase"]["bpm"])
        self.assertAlmostEqual(.2, result["phase"]["alternatingRatio"])

    def test_gain_and_grid_phase_do_not_invent_a_different_hierarchy(self):
        reference = accents.accent_measures(train(.2), 100, [dict(bpm=120)])
        for gain in (.125, 8):
            for shift in (0, .17):
                result = accents.accent_measures(train(.2, shift)*gain, 100, [dict(bpm=120)])
                self.assertAlmostEqual(reference["phase"]["alternatingRatio"], result["phase"]["alternatingRatio"])
                self.assertEqual(reference["0.75"]["intervalBpm"], result["0.75"]["intervalBpm"])

    def test_silence_is_no_attacks_and_no_phase_claim(self):
        result = accents.accent_measures(np.zeros(1000), 100, [])
        self.assertNotIn("phase", result)
        self.assertEqual(0, result["0"]["rate"])
        self.assertIsNone(result["0"]["intervalBpm"])

    def test_invalid_and_insufficient_context_rejected(self):
        for envelope, fps in [(np.zeros(300),100), ([0,float("nan")],100), ([-1]*1000,100), (np.zeros((2,1000)),100), (np.zeros(1000),0)]:
            with self.assertRaises(ValueError):
                accents.accent_measures(envelope, fps, [])

    def test_linear_attack_features_are_gain_and_polarity_invariant(self):
        sr = 11025
        times = np.arange(sr*6)/sr
        phase = (times+.1) % .5
        signal = np.sin(2*np.pi*80*phase)*np.exp(-phase/.04)
        reference, _ = accents.attack_features(signal, "bass")
        for gain in (-2, .25):
            result, _ = accents.attack_features(signal*gain, "bass")
            self.assertEqual(reference["phase"]["bpm"], result["phase"]["bpm"])
            self.assertAlmostEqual(reference["phase"]["alternatingRatio"], result["phase"]["alternatingRatio"], places=9)

    def test_local_candidates_preserve_slow_impulse_level(self):
        envelope = np.zeros(3000)
        envelope[np.arange(20,3000,75)] = 1
        result = accents.local_candidates(envelope, 100)
        self.assertEqual(3,len(result))
        for block in result:
            self.assertEqual(80,block[0]["bpm"])

    def test_foreground_energy_is_relative_to_full_mix_and_gain_invariant(self):
        sr = 11025
        time = np.arange(sr*6)/sr
        phase = (time+.1) % .75
        drum = np.sin(2*np.pi*80*phase)*np.exp(-phase/.04)
        mix = drum+.2*np.sin(2*np.pi*440*time)
        reference = accents.foreground_features(mix,drum)["all"]
        scaled = accents.foreground_features(mix*.25,drum*.25)["all"]
        for key in ("energyRatio","punch","peakProminence","widthMedian"):
            self.assertAlmostEqual(reference[key],scaled[key],places=9)
        buried = accents.foreground_features(mix+.8*np.sin(2*np.pi*440*time),drum)["all"]
        self.assertLess(buried["punch"],reference["punch"])
        self.assertLess(buried["energyRatio"],reference["energyRatio"])

    def test_foreground_silence_and_mismatched_stem(self):
        silence = np.zeros(11025*4)
        result = accents.foreground_features(silence)["all"]
        self.assertEqual(0,result["punch"])
        self.assertEqual(0,result["energyRatio"])
        self.assertEqual([],result["modulation"])
        with self.assertRaises(ValueError):
            accents.foreground_features(silence,np.zeros(100))


if __name__ == "__main__":
    unittest.main()
