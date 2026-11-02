"""Isolated synthetic PCM and error contracts. No model download or user-library training."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import numpy as np

path=Path(__file__).resolve().parents[1]/"src/JeekMusic.App/Preferences/Runtime/preference_worker.py"
spec=importlib.util.spec_from_file_location("worker",path)
worker=importlib.util.module_from_spec(spec); spec.loader.exec_module(worker)

class LearningFailureTests(unittest.TestCase):
    def test_real_resampling_has_exact_length_and_does_not_pad_short_audio(self):
        with tempfile.TemporaryDirectory(prefix="jeek-decode-test-") as tmp:
            for seconds in (12,3):
                audio=Path(tmp)/f"synthetic-{seconds}.wav"
                subprocess.run(["ffmpeg","-v","error","-f","lavfi","-i",f"sine=frequency=440:sample_rate=44100:duration={seconds}",str(audio)],check=True,timeout=10)
                if seconds==12:
                    after=worker.decode_context(audio,0,10)
                    before=np.frombuffer(subprocess.run(["ffmpeg","-v","error","-nostdin","-threads","2","-ss","0","-i",str(audio),"-t","10","-ac","1","-ar","48000","-f","f32le","pipe:1"],capture_output=True,check=True,timeout=10).stdout,dtype="<f4")
                    self.assertEqual(len(after),480000); np.testing.assert_array_equal(before[:480000],after)
                else:
                    with self.assertRaises(worker.WorkerFailure) as cm: worker.decode_context(audio,0,10)
                    self.assertEqual(cm.exception.code,"decode_length"); self.assertLess(cm.exception.details["actualSamples"],480000)
    def test_invalid_pcm_and_nonfinite_samples_have_distinct_codes(self):
        for pcm,code in [(b"bad","decode_length"),(np.full(480000,np.nan,dtype="<f4").tobytes(),"decode_nonfinite")]:
            with patch.object(Path,"is_file",return_value=True),patch.object(worker.subprocess,"run",return_value=subprocess.CompletedProcess([],0,pcm,b"")):
                with self.assertRaises(worker.WorkerFailure) as cm:worker.decode_context("synthetic",0,1)
                self.assertEqual(code,cm.exception.code)
    def test_missing_file_decoder_failure_and_missing_dependency_are_distinct(self):
        with patch.object(Path,"is_file",return_value=False):
            with self.assertRaises(worker.WorkerFailure) as cm:worker.decode_context("synthetic",0,1)
            self.assertEqual(cm.exception.code,"recording_missing")
        for error,code in [(FileNotFoundError("SECRET"),"dependency_missing"),(subprocess.CalledProcessError(1,["SECRET"]),"decode_unsupported")]:
            with patch.object(Path,"is_file",return_value=True),patch.object(worker.subprocess,"run",side_effect=error):
                with self.assertRaises(worker.WorkerFailure) as cm:worker.decode_context("synthetic",0,1)
                self.assertEqual(cm.exception.code,code)
    def test_failure_result_never_contains_exception_text(self):
        errors=[(TimeoutError("SECRET"),"extract","time_budget"),(MemoryError("SECRET"),"train","memory_budget"),(ValueError("Official component hash mismatch: SECRET"),"prepare","download_integrity"),(subprocess.CalledProcessError(1,["SECRET"]),"prepare","download_failed"),(RuntimeError("SECRET"),"evaluate","worker_exit")]
        for error,stage,code in errors:
            result=worker.failure_report(error,stage)
            self.assertEqual(code,result["code"]); self.assertEqual(stage,result["stage"]);self.assertNotIn("SECRET",str(result))

if __name__=="__main__":unittest.main()
