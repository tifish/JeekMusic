"""Synthetic labels only. No user recordings/answers, no model-performance claim."""
import importlib.util
from pathlib import Path
import unittest
import numpy as np

path=Path(__file__).resolve().parents[1]/"src/JeekMusic.App/Preferences/Runtime/preference_worker.py"
spec=importlib.util.spec_from_file_location("preference_worker",path)
worker=importlib.util.module_from_spec(spec); spec.loader.exec_module(worker)


class PipelineTests(unittest.TestCase):
    def rows(self):
        rows=[]
        rng=np.random.default_rng(13)
        for i in range(24):
            y=1 if i%2 else -1
            vector=np.zeros(512); vector[0]=y; vector[1:5]=rng.normal(0,.05,4)
            rows.append(dict(recordingId=str(i),role="training",exposed=False,identityReviewed=True,
                             componentId=str(i//3),artists=[str(i//3)],label="SPORT" if y==1 else "NON_SPORT",
                             vector=worker.unit(vector).tolist(),rules="NON_SPORT"))
        return rows
    def test_real_fixed_numeric_chain_on_synthetic_labels(self):
        report=worker.evaluate(self.rows(),24)
        self.assertEqual(report["state"],"training_gate_passed")
        self.assertEqual(len(report["predictions"]),24)
        self.assertEqual(len(report["model"]["coef"]),2)
        self.assertIn("blocked",report["activation"])
        self.assertEqual(report["metrics"]["falseAcceptance"],0)
    def test_abstention_boundaries_and_nonprobability_scores(self):
        for value in (-.25,0,.25): self.assertEqual(worker.decision(value),"ABSTAIN")
        self.assertEqual(worker.decision(2),"SPORT")
        self.assertEqual(worker.decision(-2),"NON_SPORT")
    def test_sealed_exposed_and_unreviewed_rows_fail_closed(self):
        for key,value in (("role","sealed_test"),("exposed",True),("identityReviewed",False)):
            rows=self.rows(); rows[0][key]=value
            with self.assertRaisesRegex(ValueError,"isolation"): worker.evaluate(rows,24)
    def test_fold_transforms_only_see_other_components(self):
        rows=self.rows(); original=worker.fit; seen=[]
        def fit(x,y): seen.append(np.asarray(x)); return original(x,y)
        worker.fit=fit
        try: worker.evaluate(rows,24)
        finally: worker.fit=original
        self.assertEqual(len(seen),9) # 8 out-of-component folds + frozen final fit
        for group,x in enumerate(seen[:8]):
            expected=np.array([r["vector"] for r in rows if r["componentId"]!=str(group)])
            np.testing.assert_array_equal(x,expected)
    def test_material_rank_duplicate_and_missing_baseline(self):
        self.assertEqual(worker.evaluate(self.rows()[:6],6)["state"],"insufficient_material")
        with self.assertRaisesRegex(ValueError,"rank"): worker.fit(np.ones((4,512)),[-1,1,-1,1])
        rows=self.rows(); rows[0]["recordingId"]=rows[1]["recordingId"]
        with self.assertRaisesRegex(ValueError,"Duplicate"): worker.evaluate(rows,24)
        rows=self.rows(); rows[0]["rules"]="ABSTAIN"
        self.assertEqual(worker.evaluate(rows,24)["state"],"validation_failed")
    def test_exact_one_sided_intervals(self):
        self.assertAlmostEqual(worker.binomial_upper(0,30),1-.05**(1/30),places=12)
        self.assertAlmostEqual(worker.binomial_upper(0,15),1-.05**(1/15),places=12)
        self.assertIsNone(worker.binomial_upper(0,0))
    def test_frozen_evaluator_never_fits_and_requires_stability(self):
        model=worker.fit([r["vector"] for r in self.rows()],[1 if r["label"]=="SPORT" else -1 for r in self.rows()])
        rows=[]
        for i in range(48):
            row={**self.rows()[i%24],"recordingId":str(i),"role":"sealed_test"}
            rows.append(row)
        original=worker.fit
        worker.fit=lambda *a: self.fail("Evaluator must not fit")
        try:
            result=worker.evaluate_frozen(dict(stage="sealed_test",rows=rows,model=model))
            self.assertEqual(result["state"],"inconclusive")
        finally: worker.fit=original
    def test_all_four_independent_heads_fit_real_numeric_pipeline(self):
        for dimension,head in worker.HEADS.items():
            rows=self.rows()
            for r in rows:
                r["dimension"]=dimension
                r["label"]=head["positive"] if r["label"]=="SPORT" else head["negative"]
                r["rules"]=head["negative"]
            result=worker.evaluate(rows,24,dimension)
            self.assertEqual(result["state"],"training_gate_passed",dimension)
            self.assertEqual(result["dimension"],dimension)
            self.assertEqual(result["margin"],head["margin"])
            rows[0]["label"]=""
            with self.assertRaisesRegex(ValueError,"cannot become a negative"): worker.evaluate(rows,24,dimension)


if __name__=="__main__": unittest.main()
