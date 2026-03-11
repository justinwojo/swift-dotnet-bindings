#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
Tests for sim_manager.py — validates simulator lifecycle management.

Run: python3 -m pytest scripts/ci/test_sim_manager.py -v
  or: python3 scripts/ci/test_sim_manager.py   (direct execution)

Tests are split into:
  - Unit tests (mocked subprocess, no real simulator needed)
  - Integration tests (require macOS with Xcode, create real simulators)

Integration tests are marked with @pytest.mark.integration and skipped
by default. Run with: pytest -m integration
"""

import json
import os
import subprocess
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock, PropertyMock

# Add parent directory to path
sys.path.insert(0, str(Path(__file__).parent))
from sim_manager import (
    SimManager, SimConfig, SimError,
    SimctlCommandError, SimulatorBootTimeout, SimulatorReadinessTimeout,
    SimulatorNotFound, DeviceState,
)


# ---------------------------------------------------------------------------
# Unit tests (mocked)
# ---------------------------------------------------------------------------

class TestSimctlRetry(unittest.TestCase):
    """Test that _run_simctl retries correctly."""

    def test_succeeds_on_first_try(self):
        mgr = SimManager()
        with patch("subprocess.run") as mock_run:
            mock_run.return_value = MagicMock(returncode=0, stdout="ok", stderr="")
            result = mgr._run_simctl(["list", "devices"])
            self.assertEqual(result.returncode, 0)
            self.assertEqual(mock_run.call_count, 1)

    def test_retries_on_failure(self):
        mgr = SimManager(SimConfig(command_max_retries=3, command_backoff_base=0.01, command_backoff_max=0.02))
        with patch("subprocess.run") as mock_run:
            # Fail twice, succeed third time
            mock_run.side_effect = [
                MagicMock(returncode=1, stdout="", stderr="error1"),
                MagicMock(returncode=1, stdout="", stderr="error2"),
                MagicMock(returncode=0, stdout="ok", stderr=""),
            ]
            result = mgr._run_simctl(["boot", "fake-udid"])
            self.assertEqual(result.returncode, 0)
            self.assertEqual(mock_run.call_count, 3)

    def test_raises_after_max_retries(self):
        mgr = SimManager(SimConfig(command_max_retries=2, command_backoff_base=0.01, command_backoff_max=0.02))
        with patch("subprocess.run") as mock_run:
            mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="persistent error")
            with self.assertRaises(SimctlCommandError) as ctx:
                mgr._run_simctl(["boot", "fake-udid"])
            self.assertIn("persistent error", str(ctx.exception))
            self.assertEqual(mock_run.call_count, 2)

    def test_retries_on_timeout(self):
        mgr = SimManager(SimConfig(command_max_retries=2, command_timeout=0.1, command_backoff_base=0.01))
        with patch("subprocess.run") as mock_run:
            mock_run.side_effect = [
                subprocess.TimeoutExpired(cmd=["xcrun"], timeout=0.1),
                MagicMock(returncode=0, stdout="ok", stderr=""),
            ]
            result = mgr._run_simctl(["list"])
            self.assertEqual(result.returncode, 0)

    def test_no_retry_when_check_false(self):
        mgr = SimManager()
        with patch("subprocess.run") as mock_run:
            mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="error")
            result = mgr._run_simctl(["terminate", "fake"], check=False)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(mock_run.call_count, 1)


class TestDeviceDiscovery(unittest.TestCase):
    """Test device/runtime discovery logic."""

    SAMPLE_DEVICES = {
        "devices": {
            "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [
                {"name": "iPhone 15", "udid": "AAA-111", "state": "Shutdown", "isAvailable": True},
                {"name": "iPhone 16", "udid": "BBB-222", "state": "Shutdown", "isAvailable": True},
                {"name": "iPad Air", "udid": "CCC-333", "state": "Shutdown", "isAvailable": True},
            ],
            "com.apple.CoreSimulator.SimRuntime.tvOS-18-0": [
                {"name": "Apple TV", "udid": "DDD-444", "state": "Shutdown", "isAvailable": True},
            ],
        }
    }

    SAMPLE_RUNTIMES = {
        "runtimes": [
            {"name": "iOS 17.5", "identifier": "com.apple.CoreSimulator.SimRuntime.iOS-17-5", "isAvailable": True},
            {"name": "iOS 18.2", "identifier": "com.apple.CoreSimulator.SimRuntime.iOS-18-2", "isAvailable": True},
            {"name": "tvOS 18.0", "identifier": "com.apple.CoreSimulator.SimRuntime.tvOS-18-0", "isAvailable": True},
        ]
    }

    def _make_mgr(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        return mgr

    def test_find_existing_device_preferred(self):
        mgr = self._make_mgr()
        with patch.object(mgr, "list_devices", return_value=self.SAMPLE_DEVICES):
            udid = mgr.find_existing_device()
            # Should find iPhone 16 (first in preferred list)
            self.assertEqual(udid, "BBB-222")

    def test_find_existing_device_skips_booted(self):
        devices = {
            "devices": {
                "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [
                    {"name": "iPhone 16", "udid": "BBB-222", "state": "Booted", "isAvailable": True},
                    {"name": "iPhone 15 Pro", "udid": "EEE-555", "state": "Shutdown", "isAvailable": True},
                ],
            }
        }
        mgr = self._make_mgr()
        with patch.object(mgr, "list_devices", return_value=devices):
            udid = mgr.find_existing_device()
            # Should skip booted iPhone 16 and find iPhone 15 Pro
            self.assertEqual(udid, "EEE-555")

    def test_find_existing_device_runtime_filter(self):
        mgr = self._make_mgr()
        with patch.object(mgr, "list_devices", return_value=self.SAMPLE_DEVICES):
            udid = mgr.find_existing_device(runtime_prefix="tvOS")
            # No iPhones in tvOS runtime
            self.assertIsNone(udid)

    def test_find_runtime_preferred(self):
        mgr = self._make_mgr()
        with patch.object(mgr, "list_runtimes", return_value=self.SAMPLE_RUNTIMES):
            runtime = mgr.find_runtime("iOS-18")
            self.assertIn("iOS-18", runtime)

    def test_find_runtime_fallback(self):
        mgr = self._make_mgr()
        runtimes = {"runtimes": [
            {"name": "iOS 16.0", "identifier": "com.apple.CoreSimulator.SimRuntime.iOS-16-0", "isAvailable": True},
        ]}
        with patch.object(mgr, "list_runtimes", return_value=runtimes):
            # None of the preferred runtimes match, should fallback
            runtime = mgr.find_runtime()
            self.assertIn("iOS-16-0", runtime)

    def test_find_runtime_no_available(self):
        mgr = self._make_mgr()
        with patch.object(mgr, "list_runtimes", return_value={"runtimes": []}):
            with self.assertRaises(SimulatorNotFound):
                mgr.find_runtime()


class TestBootSequence(unittest.TestCase):
    """Test boot and readiness polling."""

    def test_boot_already_booted(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        with patch.object(mgr, "get_device_state", return_value=DeviceState.BOOTED):
            with patch.object(mgr, "_run_simctl") as mock_simctl:
                mgr.boot("fake-udid")
                # Should not call simctl boot
                mock_simctl.assert_not_called()

    def test_boot_handles_already_booting(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        with patch.object(mgr, "get_device_state", return_value=DeviceState.SHUTDOWN):
            with patch.object(mgr, "_run_simctl") as mock_simctl:
                mock_simctl.side_effect = SimctlCommandError(
                    ["boot"], 1, "Unable to boot device in current state: Booted"
                )
                # Should not raise — "already booted" is fine
                mgr.boot("fake-udid")

    def test_wait_until_booted_success(self):
        mgr = SimManager(SimConfig(boot_poll_interval=0.01, boot_timeout=1))
        call_count = [0]
        def mock_state(udid):
            call_count[0] += 1
            if call_count[0] >= 3:
                return DeviceState.BOOTED
            return DeviceState.SHUTDOWN
        with patch.object(mgr, "get_device_state", side_effect=mock_state):
            mgr.wait_until_booted("fake-udid")

    def test_wait_until_booted_timeout(self):
        mgr = SimManager(SimConfig(boot_poll_interval=0.01, boot_timeout=0.05))
        with patch.object(mgr, "get_device_state", return_value=DeviceState.SHUTDOWN):
            with self.assertRaises(SimulatorBootTimeout):
                mgr.wait_until_booted("fake-udid")

    def test_wait_until_responsive_success(self):
        mgr = SimManager(SimConfig(readiness_poll_interval=0.01, readiness_timeout=1))
        with patch.object(mgr, "_run_simctl") as mock_simctl:
            mock_simctl.return_value = MagicMock(returncode=0)
            mgr.wait_until_responsive("fake-udid")

    def test_wait_until_responsive_timeout(self):
        mgr = SimManager(SimConfig(readiness_poll_interval=0.01, readiness_timeout=0.05))
        with patch.object(mgr, "_run_simctl") as mock_simctl:
            mock_simctl.side_effect = SimctlCommandError(["spawn"], 1, "not ready")
            with self.assertRaises(SimulatorReadinessTimeout):
                mgr.wait_until_responsive("fake-udid")


class TestCleanup(unittest.TestCase):
    """Test cleanup never raises."""

    def test_cleanup_swallows_errors(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        with patch.object(mgr, "shutdown", side_effect=SimError("shutdown failed")):
            with patch.object(mgr, "delete", side_effect=SimError("delete failed")):
                # Should not raise
                mgr.cleanup("fake-udid")


class TestCreateSimulator(unittest.TestCase):
    """Test simulator creation."""

    def test_create_returns_udid(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        with patch.object(mgr, "find_runtime", return_value="com.apple.CoreSimulator.SimRuntime.iOS-18-2"):
            with patch.object(mgr, "find_device_type", return_value="com.apple.CoreSimulator.SimDeviceType.iPhone-16"):
                with patch.object(mgr, "_run_simctl") as mock_simctl:
                    mock_simctl.return_value = MagicMock(stdout="AAAA-BBBB-CCCC\n")
                    udid = mgr.create_simulator()
                    self.assertEqual(udid, "AAAA-BBBB-CCCC")
                    # Verify create was called with correct args
                    call_args = mock_simctl.call_args[0][0]
                    self.assertEqual(call_args[0], "create")


class TestDiagnostics(unittest.TestCase):
    """Test diagnostics collection."""

    def test_collect_diagnostics_creates_dir(self):
        mgr = SimManager(SimConfig(command_backoff_base=0.01))
        import tempfile
        with tempfile.TemporaryDirectory() as tmpdir:
            diag_dir = os.path.join(tmpdir, "diag")
            with patch.object(mgr, "_run_simctl") as mock_simctl:
                mock_simctl.return_value = MagicMock(stdout='{"devices":{}}')
                with patch("subprocess.run") as mock_run:
                    mock_run.return_value = MagicMock(stdout="", returncode=0)
                    files = mgr.collect_diagnostics("fake-udid", diag_dir)
                    self.assertTrue(os.path.isdir(diag_dir))


# ---------------------------------------------------------------------------
# Integration tests (require real macOS + Xcode)
# ---------------------------------------------------------------------------

def _has_simctl():
    """Check if xcrun simctl is available."""
    try:
        result = subprocess.run(
            ["xcrun", "simctl", "list", "runtimes", "-j"],
            capture_output=True, text=True, timeout=10,
        )
        return result.returncode == 0
    except Exception:
        return False


SKIP_INTEGRATION = not _has_simctl()
SKIP_REASON = "xcrun simctl not available (requires macOS + Xcode)"


@unittest.skipIf(SKIP_INTEGRATION, SKIP_REASON)
class TestIntegrationDiscovery(unittest.TestCase):
    """Integration: test real device/runtime discovery."""

    def test_list_devices(self):
        mgr = SimManager()
        devices = mgr.list_devices()
        self.assertIn("devices", devices)

    def test_list_runtimes(self):
        mgr = SimManager()
        runtimes = mgr.list_runtimes()
        self.assertIn("runtimes", runtimes)

    def test_find_runtime(self):
        mgr = SimManager()
        runtime = mgr.find_runtime()
        self.assertIn("iOS", runtime)

    def test_find_existing_device(self):
        mgr = SimManager()
        # May or may not find one — just shouldn't crash
        udid = mgr.find_existing_device()
        if udid:
            self.assertTrue(len(udid) > 0)


@unittest.skipIf(SKIP_INTEGRATION, SKIP_REASON)
class TestIntegrationLifecycle(unittest.TestCase):
    """Integration: test full simulator lifecycle (create, boot, cleanup).

    This test creates a real simulator and cleans it up afterward.
    Takes ~30-60 seconds.
    """

    def test_full_lifecycle(self):
        mgr = SimManager(SimConfig(
            boot_timeout=120,
            readiness_timeout=60,
        ))
        udid = None
        try:
            # Create
            udid = mgr.create_simulator()
            self.assertIsNotNone(udid)
            self.assertTrue(len(udid) > 10)

            # Verify state
            state = mgr.get_device_state(udid)
            self.assertEqual(state, DeviceState.SHUTDOWN)

            # Boot
            mgr.boot(udid)

            # Wait for booted
            mgr.wait_until_booted(udid)
            state = mgr.get_device_state(udid)
            self.assertEqual(state, DeviceState.BOOTED)

            # Wait for responsive
            mgr.wait_until_responsive(udid)

        finally:
            if udid:
                mgr.cleanup(udid)
                # Verify deleted (state should be None)
                time.sleep(1)
                state = mgr.get_device_state(udid)
                self.assertIsNone(state)

    def test_prepare_simulator(self):
        """Test the high-level prepare_simulator convenience method."""
        mgr = SimManager(SimConfig(
            boot_timeout=120,
            readiness_timeout=60,
        ))
        udid = None
        try:
            udid = mgr.prepare_simulator()
            self.assertIsNotNone(udid)
            state = mgr.get_device_state(udid)
            self.assertEqual(state, DeviceState.BOOTED)
        finally:
            if udid:
                mgr.cleanup(udid)


# ---------------------------------------------------------------------------
# CLI test
# ---------------------------------------------------------------------------

class TestCLI(unittest.TestCase):
    """Test CLI argument parsing."""

    def test_help_exits_cleanly(self):
        result = subprocess.run(
            [sys.executable, str(Path(__file__).parent / "sim_manager.py"), "--help"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0)
        self.assertIn("iOS Simulator lifecycle manager", result.stdout)

    def test_create_help(self):
        result = subprocess.run(
            [sys.executable, str(Path(__file__).parent / "sim_manager.py"), "create", "--help"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    unittest.main(verbosity=2)
