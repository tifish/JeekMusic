"""Workflow guard tests with mocked clients/processes; never launch JeekMusic or use audio."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


mcp = load("mcp_workflow", "test-mcp.py")
build = load("build_workflow", "test-build-isolation.py")
licenses = load("collect_licenses", "collect-licenses.py")
sample_rates = load("sample_rate_workflow", "test-sample-rates.py")


class WorkflowGuards(unittest.TestCase):
    def test_sample_rate_hardware_checks_refuse_other_clients_before_opening_any_client(self):
        with patch.object(sys, "argv", ["test-sample-rates.py", "--audio"]), \
             patch.object(sample_rates, "running_clients", return_value=[12345]), \
             patch.object(sample_rates, "McpClient") as client, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                sample_rates.main()
            self.assertEqual(error.exception.code, 2)
            client.assert_not_called()

    def test_license_collector_uses_isolated_publish_graph_and_rejects_ambiguous_or_stale_graphs(self):
        with tempfile.TemporaryDirectory(prefix="jeek-license-graph-") as temp:
            project = Path(temp)
            legacy = project / "obj/project.assets.json"
            legacy.parent.mkdir()
            legacy.write_text("stale legacy graph")
            with self.assertRaises(ValueError):
                licenses.resolve_assets(project)
            correct = project / "obj/publish/win-x64/aot-true_sc-_psc-/project.assets.json"
            correct.parent.mkdir(parents=True)
            correct.write_text("matching publish graph")
            self.assertEqual(licenses.resolve_assets(project), correct)
            other = project / "obj/publish/portable/project.assets.json"
            other.parent.mkdir(parents=True)
            other.write_text("another configuration")
            with self.assertRaises(ValueError):
                licenses.resolve_assets(project)
            self.assertEqual(licenses.resolve_assets(project, correct), correct)
            with self.assertRaises(ValueError):
                licenses.resolve_assets(project, project / "missing.json")

    def test_default_mcp_only_reads_diagnostics_and_never_generates_or_plays_audio(self):
        calls = []

        class Client:
            def __init__(self, exe, data):
                self.exe, self.data = exe, data

            def __enter__(self):
                return self

            def __exit__(self, *_):
                pass

            def call(self, name, arguments):
                calls.append(name)
                if name == "get_diagnostics":
                    value = dict(processId=12345, dataDirectory=str(self.data), processPath=str(self.exe),
                                 processArchitecture="X64", dynamicCodeSupported=False,
                                 frameworkDescription=".NET test")
                    value["typography"] = dict(initialized=True, defaultFamilyChain="Microsoft YaHei, Segoe UI",
                        effectiveDefaultFamily="Microsoft YaHei, Segoe UI", microsoftYaHeiAvailable=True,
                        normalFamilies=["Microsoft YaHei"], boldFamilies=["Microsoft YaHei"])
                elif name == "get_status":
                    value = {"trackCount": 0}
                else:
                    raise AssertionError(f"Default diagnostics tried a business/audio call: {name}")
                return {"structuredContent": value}

            def request(self, method):
                calls.append(method)
                return {"tools": [{"name": "get_diagnostics", "inputSchema": {
                    "properties": {}, "additionalProperties": False}}] + [{"name": f"tool{i}"} for i in range(21)]}

        with patch.object(sys, "argv", ["test-mcp.py", "--exe", __file__]), \
             patch.object(mcp, "McpClient", Client), \
             patch.object(mcp, "close_player") as close, \
             patch.object(mcp.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="", stderr="")) as process, \
             patch.object(mcp.wave, "open") as audio, \
             contextlib.redirect_stdout(io.StringIO()):
            mcp.main()
        self.assertEqual(calls, ["get_diagnostics", "get_status", "tools/list"])
        audio.assert_not_called()
        close.assert_called_once_with(12345)
        self.assertEqual(process.call_args.args[0][1:], ["--mcp"])

    def test_mcp_exclusive_requires_explicit_audio_and_conflicting_modes_fail_before_client_creation(self):
        for options in (["--exclusive"], ["--startup-only", "--audio"]):
            with self.subTest(options=options), patch.object(sys, "argv", ["test-mcp.py", *options]), \
                 patch.object(mcp, "McpClient") as client, contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as error:
                    mcp.main()
                self.assertEqual(error.exception.code, 2)
                client.assert_not_called()

    def test_default_build_smoke_explicitly_requests_startup_only_and_compares_real_catalogs(self):
        with tempfile.TemporaryDirectory(prefix="jeek-workflow-mock-") as temp:
            def fake_run(label, command, **kwargs):
                catalog = Path(command[command.index("--catalog") + 1])
                catalog.write_text(json.dumps({"tools": [{"name": "diagnostics", "inputSchema": {}}]}), encoding="utf-8")

            with patch.object(build, "LOGS", Path(temp)), patch.object(build, "CATALOGS", []), \
                 patch.object(build, "BUILD_ONLY", False), patch.object(build, "MCP_OPTIONS", ["--startup-only"]), \
                 patch.object(build, "run", side_effect=fake_run) as process:
                build.smoke("mock-first", Path(temp) / "never-executed.exe")
                build.smoke("mock-second", Path(temp) / "never-executed.exe")
            for invocation in process.call_args_list:
                command = invocation.args[1]
                self.assertIn("--startup-only", command)
                self.assertNotIn("--audio", command)
                self.assertNotIn("--exclusive", command)

    def test_build_only_smoke_never_calls_process_runner(self):
        with tempfile.TemporaryDirectory(prefix="jeek-workflow-mock-") as temp:
            exe = Path(temp) / "never-executed.exe"
            exe.write_bytes(b"static fixture only")
            with patch.object(build, "BUILD_ONLY", True), patch.object(build, "run") as process, \
                 contextlib.redirect_stdout(io.StringIO()):
                build.smoke("mock-native", exe, aot=True)
            process.assert_not_called()

    def test_build_rejects_audio_mode_conflicts_before_any_build_or_client(self):
        for options in (["--exclusive"], ["--build-only", "--audio"], ["--startup-only", "--audio"]):
            with self.subTest(options=options), patch.object(sys, "argv", ["test-build-isolation.py", *options]), \
                 patch.object(build, "build") as compile_step, patch.object(build, "smoke") as client, \
                 contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as error:
                    build.main()
                self.assertEqual(error.exception.code, 2)
                compile_step.assert_not_called()
                client.assert_not_called()


if __name__ == "__main__":
    unittest.main()
