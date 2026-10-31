"""Leakage, grouping and fixed low-capacity objective controls, not music accuracy."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np

SPEC = importlib.util.spec_from_file_location("preference", Path(__file__).with_name("research-workout-preference.py"))
P = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(P)
FIX = P.REP.load_module("representation_controls", Path(__file__).with_name("test-workout-representations.py"))


class PreferenceControls(unittest.TestCase):
    def test_balanced_mean_loss_ridge_has_zero_objective_gradient(self):
        x = np.array([[1, 0, 0], [0, 1, 0], [0, 0, 1], [.6, .8, 0]])
        y = np.array([1, 1, 1, -1])
        m = P.fit(x, y)
        z = (x-m['center']) @ m['axes'] / m['scale']
        weights = np.array([1/3, 1/3, 1/3, 1.]) / 2
        residual = P.score(m, x)-y
        np.testing.assert_allclose(z.T @ (weights*residual)+m['coef'], 0, atol=1e-12)
        self.assertAlmostEqual(0, weights @ residual, places=12)
        self.assertEqual((3, 2), m['axes'].shape)

    def test_held_out_extreme_query_cannot_change_training_transform(self):
        x = np.eye(4)
        m = P.fit(x, [-1, 1, -1, 1])
        before = copy.deepcopy(m)
        P.score(m, np.full(4, 1e8))
        for key in m:
            np.testing.assert_array_equal(before[key], m[key])
        np.testing.assert_array_equal(m['center'], x.mean(axis=0))

    def test_missing_class_and_rank_failure_do_not_become_predictions(self):
        for x, y in [(np.eye(3), [1, 1, 1]), (np.ones((3, 3)), [-1, 1, 1])]:
            with self.assertRaises(ValueError):
                P.fit(x, y)

    def test_grouping_excludes_entire_artist_and_album(self):
        rows = [dict(trackId=i, artist='A' if i < 4 else 'B', album='X' if i < 3 else None)
                for i in range(1, 6)]
        artist = P.folds(rows, 'artist')
        self.assertIn(([4, 5], [1, 2, 3]), artist)
        album = P.folds(rows, 'album')
        self.assertIn(([3, 4, 5], [1, 2]), album)
        self.assertIn(([1, 2, 4, 5], [3]), album)
        for train, test in artist+album+P.folds(rows, 'recording'):
            self.assertFalse(set(train) & set(test))
            self.assertEqual(set(range(1, 6)), set(train+test))

    def test_new_pair_never_enters_first_training_fold(self):
        rows = [dict(trackId=i) for i in list(range(1, 10))+[353, 354]]
        self.assertEqual([(list(range(1, 10)), [353, 354])], P.folds(rows, 'first_pair'))

    def test_pooling_is_one_row_per_recording_condition(self):
        report, _ = FIX.fixture()
        r = report['records'][0]
        self.assertEqual((200,), P.pooled(r, 'base').shape)
        before = P.pooled(r, 'base').copy()
        for s in r['segments']:
            if s['condition'] != 'base':
                s['vectors']['penultimate'] = report['records'][2]['segments'][0]['vectors']['penultimate']
        np.testing.assert_array_equal(before, P.pooled(r, 'base'))
        r['segments'] = [s for s in r['segments'] if not (s['context'] == 2 and s['condition'] == 'base')]
        with self.assertRaises(ValueError):
            P.pooled(r, 'base')

    def test_overlap_incomplete_and_mismatched_preprocessing_rejected(self):
        r, _ = FIX.fixture()
        r.update(normalizeRms=.1, sourceCommit='516acb2a0ff5ef73f64547898e018e793152c506')
        with self.assertRaises(ValueError):
            P.combine([r, r], 'test')
        for key, value in [('state', 'incomplete'), ('normalizeRms', .2), ('sourceCommit', 'other')]:
            with self.assertRaises(ValueError):
                P.combine([{**r, key: value}], 'test')

    def test_first_pair_neighbors_use_only_prior_recordings(self):
        report, labels = FIX.fixture()
        for r, label, tid in zip(report['records'], labels['records'], [1, 353, 354]):
            r['trackId'] = label['trackId'] = tid
        output = P.first_pair_neighbors(report, labels)
        for queries in output.values():
            self.assertEqual(30, len(queries))
            self.assertEqual({1}, {q['neighborId'] for q in queries})

    def test_unknown_truth_is_rejected_before_learning(self):
        report, labels = FIX.fixture()
        labels['records'][0]['label_source'] = 'automatic'
        with self.assertRaises(ValueError):
            P.evaluate(report, labels)

    def test_confusion_reports_negative_errors_and_majority_separately(self):
        predictions = [dict(label='NON_SPORT', scores=dict(base=.1), baselineSport=False),
                       dict(label='SPORT', scores=dict(base=0), baselineSport=False)]
        self.assertEqual(dict(tp=0, fn=1, tn=0, fp=1, sportRecall=0, nonSportRecall=0), P.confusion(predictions))
        self.assertEqual(1, P.confusion(predictions, True)['tn'])

    def test_budget_or_cancel_never_publishes_completed_index(self):
        report, labels = FIX.fixture()
        report.update(normalizeRms=.1, sourceCommit='516acb2a0ff5ef73f64547898e018e793152c506',
                      protocolSha256=P.sha(P.REP.PROTOCOL))
        for error, reason in [(TimeoutError, 'budget_exhausted'), (KeyboardInterrupt, 'cancelled')]:
            with self.subTest(reason=reason), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                source, feedback, output, index = [root/name for name in ('source.json', 'labels.json', 'out.json', 'index.json')]
                source.write_text(json.dumps(report), encoding='utf-8')
                feedback.write_text(json.dumps(labels), encoding='utf-8')
                with patch.object(sys, 'argv', ['probe', '--report', str(source), '--feedback', str(feedback),
                    '--output', str(output), '--index', str(index)]), patch.object(P, 'evaluate', side_effect=error):
                    with self.assertRaises(error):
                        P.main()
                saved = json.loads(output.read_text(encoding='utf-8'))
                self.assertEqual(('incomplete', reason), (saved['state'], saved['stopReason']))
                self.assertNotIn('evaluations', saved)
                self.assertFalse(index.exists())


if __name__ == '__main__':
    unittest.main()
