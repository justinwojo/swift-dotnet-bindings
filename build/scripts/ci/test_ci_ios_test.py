#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""Focused acceptance tests for ci_ios_test.run_tests."""

import contextlib
import io
import json
import re
from pathlib import Path
import subprocess
import unittest
from unittest.mock import MagicMock, patch

import ci_ios_test

# Read only the current helper's string literals: a stale copied fixture must
# not keep passing after the load-bearing launcher wording changes.
def current_launcher_abort_text():
    root = Path(__file__).resolve().parents[3]
    source = (root / "build/Build.RuntimeTests.cs").read_text()
    helper = source.split("static void RetryKnownLauncherAbort(", 1)[1].split(
        "\n    // ============================================================", 1)[0]
    expression = re.search(r"throw new Exception\((.*?)\);", helper, re.S).group(1)
    pieces = re.findall(r'\$?"((?:\\.|[^"\\])*)"', expression)
    text = "".join(json.loads('"' + piece + '"') for piece in pieces)
    diagnostics = (root / "build/Models/LaunchDiagnostics.cs").read_text()
    budget = re.search(r"MaxLauncherAbortAttempts\s*=\s*(\d+)", diagnostics).group(1)
    text = text.replace("{legLabel}", "iOS Simulator").replace(
        "{LaunchDiagnostics.MaxLauncherAbortAttempts}", budget)
    assert "{" not in text and "}" not in text, "Update the bounded fixture for new interpolations"
    return text, helper


EXHAUSTED_LAUNCH, CURRENT_ABORT_HELPER = current_launcher_abort_text()


class ManagedTargetTests(unittest.TestCase):
    def test_default_and_profile_framework(self):
        with patch.dict(ci_ios_test.os.environ, {}, clear=True):
            self.assertEqual(ci_ios_test.managed_framework(), "net10.0")
        with patch.dict(ci_ios_test.os.environ, {"SWIFT_BINDINGS_MANAGED_FRAMEWORK": "net11.0"}):
            self.assertEqual(ci_ios_test.managed_framework(), "net11.0")
            with self.assertRaises(ValueError):
                ci_ios_test.managed_framework("net10.0")

    def test_new_target_build_and_bundle_are_same_lane(self):
        completed = subprocess.CompletedProcess([], 0, "", "")
        with patch.dict(ci_ios_test.os.environ, {}, clear=True), \
                patch.object(ci_ios_test.subprocess, "run", return_value=completed) as run, \
                patch.object(ci_ios_test.os.path, "isdir", return_value=True) as exists:
            ci_ios_test.run_build("/fixture/BindingTests", binding_test_framework="net11.0", binding_test_configuration="Release")
            self.assertIn("-p:BtTargetFramework=net11.0", run.call_args.args[0])
            self.assertEqual(run.call_args.args[0][3], "Release")
            self.assertIn("bin/coreclr/Release/net11.0-ios/iossimulator-arm64", exists.call_args.args[0])

    def test_new_target_test_command_keeps_identity(self):
        completed = subprocess.CompletedProcess([], 0, "TEST SUCCESS", "")
        with patch.dict(ci_ios_test.os.environ, {}, clear=True), \
                patch.object(ci_ios_test.subprocess, "run", return_value=completed) as run:
            self.assertEqual(ci_ios_test.run_tests("/fixture/BindingTests", "sim", binding_test_framework="net11.0", binding_test_configuration="Release", allow_unqualified_runtime=True), 0)
            command = run.call_args.args[0]
            self.assertEqual(command[command.index("--binding-test-framework") + 1], "net11.0")
            self.assertEqual(command[command.index("--binding-test-configuration") + 1], "Release")
            self.assertIn("--allow-unqualified-runtime", command)


class RunTestsAcceptanceTests(unittest.TestCase):
    def _run(self, side_effect, max_test_retries=1):
        calls = []

        def fake_run(cmd, **kwargs):
            calls.append(cmd)
            outcome = side_effect[len(calls) - 1]
            if isinstance(outcome, BaseException):
                raise outcome
            return outcome

        simulator = MagicMock()
        with patch.object(ci_ios_test.subprocess, "run", side_effect=fake_run), \
                patch.object(ci_ios_test, "SimManager", return_value=simulator), \
                patch.object(ci_ios_test.time, "sleep"), \
                contextlib.redirect_stdout(io.StringIO()), \
                contextlib.redirect_stderr(io.StringIO()):
            result = ci_ios_test.run_tests(
                "/nonexistent/audit/BindingTests",
                "test-udid",
                timeout=1,
                max_test_retries=max_test_retries,
            )
        return result, calls

    def test_failure_then_outer_timeout_is_not_retried(self):
        output = (
            "THE APP NEVER LAUNCHED: launcher aborted before the app started\n"
            "Running on iOS Simulator\n"
            "[FAIL] OwnershipTests.CallbackReleasedEarly\n"
        )
        timed_out = subprocess.TimeoutExpired(["dotnet"], 1, output=output)
        success = subprocess.CompletedProcess(["dotnet"], 0, stdout="RUNTIME TESTS PASSED\n")

        result, calls = self._run([timed_out, success])

        self.assertEqual(result, 1)
        self.assertEqual(len(calls), 1)

    def test_crash_then_outer_timeout_is_not_retried(self):
        timed_out = subprocess.TimeoutExpired(
            ["dotnet"],
            1,
            output="THE APP NEVER LAUNCHED: launcher aborted before the app started\n",
            stderr=(
                "RUNTIME TESTS CRASHED (Simulator)\n"
                "[CRASH] LifetimeTests.ReleaseAfterCallback\n"
                "Crash detected in class: LifetimeTests\n"
            ),
        )
        success = subprocess.CompletedProcess(["dotnet"], 0, stdout="RUNTIME TESTS PASSED\n")

        result, calls = self._run([timed_out, success])

        self.assertEqual(result, 1)
        self.assertEqual(len(calls), 1)

    def test_current_exception_alone_is_a_retryable_pretest_diagnostic(self):
        self.assertIn("prestart-abort budget exhausted", EXHAUSTED_LAUNCH)
        self.assertIn("any earlier observed product failure remains a failed gate", EXHAUSTED_LAUNCH)
        self.assertTrue(ci_ios_test.is_retryable_pretest_startup_failure(EXHAUSTED_LAUNCH))

    def test_current_per_abort_warning_retains_the_ci_contract(self):
        # This is the actual warning literal, not a second hand-copied fixture.
        warning = re.search(r'"(\{Leg\}: the launcher aborted[^"\n]+)"', CURRENT_ABORT_HELPER).group(1)
        self.assertTrue(ci_ios_test.is_retryable_pretest_startup_failure(warning))

    def test_pretest_startup_failure_can_recover(self):
        startup_failure = subprocess.CompletedProcess(
            ["dotnet"],
            1,
            stdout=EXHAUSTED_LAUNCH,
            stderr="",
        )
        success = subprocess.CompletedProcess(["dotnet"], 0, stdout="RUNTIME TESTS PASSED\n", stderr="")

        result, calls = self._run([startup_failure, success])

        self.assertEqual(result, 0)
        self.assertEqual(len(calls), 2)

    def test_pretest_startup_failure_exhaustion_is_bounded(self):
        startup_output = EXHAUSTED_LAUNCH
        outcomes = [
            subprocess.CompletedProcess(["dotnet"], 1, stdout=startup_output, stderr=""),
            subprocess.TimeoutExpired(["dotnet"], 1, output=startup_output),
            subprocess.CompletedProcess(["dotnet"], 0, stdout="RUNTIME TESTS PASSED\n", stderr=""),
        ]

        result, calls = self._run(outcomes, max_test_retries=1)

        self.assertEqual(result, 1)
        self.assertEqual(len(calls), 2)

    def test_pretest_timeout_can_recover_with_actual_exhaust_text(self):
        timeout = subprocess.TimeoutExpired(["dotnet"], 1, output=EXHAUSTED_LAUNCH.encode())
        success = subprocess.CompletedProcess(["dotnet"], 0, stdout="TEST SUCCESS\n", stderr="")
        result, calls = self._run([timeout, success])
        self.assertEqual(result, 0)
        self.assertEqual(len(calls), 2)

    def test_actual_app_verdict_and_loader_markers_dominate_exhaust_text(self):
        for marker in ("TEST FAILURE: failed", "TEST SUCCESS", "[FAIL] failed",
                       "[CRASH] crashed", "dyld: Library not loaded: missing",
                       "[TEST] --- begun ---", "RESULTS FLUSHED"):
            with self.subTest(marker=marker):
                failure = subprocess.CompletedProcess(["dotnet"], 1, stdout=EXHAUSTED_LAUNCH,
                                                      stderr=marker)
                result, calls = self._run([failure])
                self.assertEqual(result, 1)
                self.assertEqual(len(calls), 1)

    def test_healthy_control_runs_full_simulator_suite_once(self):
        success = subprocess.CompletedProcess(["dotnet"], 0, stdout="RUNTIME TESTS PASSED\n", stderr="")

        result, calls = self._run([success])

        self.assertEqual(result, 0)
        self.assertEqual(len(calls), 1)
        self.assertIn("--sim", calls[0])
        self.assertNotIn("--class-filter", calls[0])
        self.assertNotIn("--tier", calls[0])


if __name__ == "__main__":
    unittest.main()
