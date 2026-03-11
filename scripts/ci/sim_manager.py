#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
iOS Simulator lifecycle manager for CI environments.

Provides reliable simulator creation, boot, readiness detection, and cleanup
with retry logic and structured diagnostics. Designed for GitHub Actions macOS
runners where `xcrun simctl bootstatus -b` hangs.

Usage (standalone):
    python3 sim_manager.py create --runtime iOS-18 --device-type iPhone-16
    python3 sim_manager.py boot <udid>
    python3 sim_manager.py wait-ready <udid>
    python3 sim_manager.py cleanup <udid>
    python3 sim_manager.py diagnose <udid> --output-dir /tmp/sim-diag

Usage (as module):
    from sim_manager import SimManager
    mgr = SimManager()
    udid = mgr.create_simulator("iOS-18", "iPhone-16")
    mgr.boot_and_wait(udid)
    # ... run tests ...
    mgr.cleanup(udid)
"""

import argparse
import json
import logging
import os
import subprocess
import sys
import time
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Optional

log = logging.getLogger("sim_manager")


# ---------------------------------------------------------------------------
# Error hierarchy
# ---------------------------------------------------------------------------

class SimError(Exception):
    """Base error for simulator operations."""
    pass

class SimctlCommandError(SimError):
    """A simctl command failed after retries."""
    def __init__(self, cmd, returncode, stderr):
        self.cmd = cmd
        self.returncode = returncode
        self.stderr = stderr
        super().__init__(f"simctl {' '.join(cmd)} failed (rc={returncode}): {stderr}")

class SimulatorBootTimeout(SimError):
    """Simulator did not reach Booted state in time."""
    pass

class SimulatorReadinessTimeout(SimError):
    """Simulator booted but did not become responsive."""
    pass

class SimulatorNotFound(SimError):
    """No matching simulator runtime or device type found."""
    pass


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

class DeviceState(str, Enum):
    SHUTDOWN = "Shutdown"
    BOOTED = "Booted"
    SHUTTING_DOWN = "Shutting Down"
    CREATING = "Creating"


@dataclass
class SimConfig:
    """Tunable parameters for simulator lifecycle."""
    # Retry settings for individual simctl commands
    command_max_retries: int = 3
    command_backoff_base: float = 2.0      # seconds
    command_backoff_max: float = 8.0       # seconds
    command_timeout: float = 60.0          # per-command timeout (GHA simctl can be slow)

    # Boot phase settings
    boot_poll_interval: float = 3.0        # seconds between state polls
    boot_timeout: float = 180.0            # max seconds to wait for Booted

    # Readiness probe settings (phase 2 after Booted)
    readiness_poll_interval: float = 2.0
    readiness_timeout: float = 120.0       # GHA runners can be very slow
    readiness_probe_timeout: float = 30.0  # per-probe timeout (GHA simctl spawn is slow)

    # Preferred device types (in order)
    preferred_devices: list = field(default_factory=lambda: [
        "iPhone 16", "iPhone 17 Pro", "iPhone 15 Pro", "iPhone 15"
    ])

    # Preferred iOS runtimes (prefix match, newest first)
    preferred_runtimes: list = field(default_factory=lambda: [
        "iOS-19", "iOS-18", "iOS-17"
    ])


# ---------------------------------------------------------------------------
# SimManager
# ---------------------------------------------------------------------------

class SimManager:
    """Manages iOS Simulator lifecycle with retries and diagnostics."""

    def __init__(self, config: Optional[SimConfig] = None):
        self.config = config or SimConfig()

    # -------------------------------------------------------------------
    # Low-level simctl wrapper with retries
    # -------------------------------------------------------------------

    def _run_simctl(
        self,
        args: list[str],
        *,
        check: bool = True,
        retries: Optional[int] = None,
        timeout: Optional[float] = None,
        capture: bool = True,
    ) -> subprocess.CompletedProcess:
        """Run an xcrun simctl command with retry and timeout."""
        max_retries = retries if retries is not None else self.config.command_max_retries
        cmd_timeout = timeout if timeout is not None else self.config.command_timeout
        full_cmd = ["xcrun", "simctl"] + args
        last_err = None

        for attempt in range(1, max_retries + 1):
            try:
                log.debug("simctl attempt %d/%d: %s", attempt, max_retries, " ".join(full_cmd))
                result = subprocess.run(
                    full_cmd,
                    capture_output=capture,
                    text=True,
                    timeout=cmd_timeout,
                )
                if check and result.returncode != 0:
                    raise SimctlCommandError(args, result.returncode, result.stderr.strip())
                return result
            except subprocess.TimeoutExpired:
                last_err = SimctlCommandError(args, -1, f"timed out after {cmd_timeout}s")
                log.warning("simctl %s timed out (attempt %d/%d)", args[0], attempt, max_retries)
            except SimctlCommandError as e:
                last_err = e
                log.warning("simctl %s failed (attempt %d/%d): %s", args[0], attempt, max_retries, e.stderr)

            if attempt < max_retries:
                backoff = min(
                    self.config.command_backoff_base ** attempt,
                    self.config.command_backoff_max,
                )
                log.info("Retrying in %.1fs...", backoff)
                time.sleep(backoff)

        raise last_err

    # -------------------------------------------------------------------
    # Device discovery
    # -------------------------------------------------------------------

    def list_devices(self) -> dict:
        """Return parsed simctl device list JSON."""
        result = self._run_simctl(["list", "devices", "-j"], retries=2)
        return json.loads(result.stdout)

    def list_runtimes(self) -> dict:
        """Return parsed simctl runtime list JSON."""
        result = self._run_simctl(["list", "runtimes", "-j"], retries=2)
        return json.loads(result.stdout)

    def find_runtime(self, prefix: Optional[str] = None) -> str:
        """Find the best available iOS runtime identifier.

        Args:
            prefix: Runtime prefix like "iOS-18" or "iOS-19". If None, uses
                    config.preferred_runtimes in order.

        Returns:
            Full runtime identifier string (e.g. "com.apple.CoreSimulator.SimRuntime.iOS-18-2")
        """
        runtimes_data = self.list_runtimes()
        runtimes = runtimes_data.get("runtimes", [])

        # Filter to available iOS runtimes
        available = [
            r for r in runtimes
            if r.get("isAvailable", False) and "iOS" in r.get("name", "")
        ]

        if not available:
            raise SimulatorNotFound("No available iOS runtimes found")

        prefixes = [prefix] if prefix else self.config.preferred_runtimes
        for pref in prefixes:
            # Match against identifier (e.g. "com.apple.CoreSimulator.SimRuntime.iOS-18-2")
            # and name (e.g. "iOS 18.2")
            normalized = pref.replace("-", " ").replace("iOS ", "iOS-")
            matches = [
                r for r in available
                if pref.replace("-", ".") in r.get("name", "")
                or pref in r.get("identifier", "")
            ]
            if matches:
                # Sort by version (higher is better) — use identifier as proxy
                matches.sort(key=lambda r: r.get("identifier", ""), reverse=True)
                chosen = matches[0]
                log.info("Selected runtime: %s (%s)", chosen["name"], chosen["identifier"])
                return chosen["identifier"]

        # Fallback: newest available
        available.sort(key=lambda r: r.get("identifier", ""), reverse=True)
        chosen = available[0]
        log.info("Fallback runtime: %s (%s)", chosen["name"], chosen["identifier"])
        return chosen["identifier"]

    def find_device_type(self, name: Optional[str] = None) -> str:
        """Find a device type identifier by name.

        Args:
            name: Device name like "iPhone 16". If None, returns first preferred match.

        Returns:
            Device type identifier (e.g. "com.apple.CoreSimulator.SimDeviceType.iPhone-16")
        """
        result = self._run_simctl(["list", "devicetypes", "-j"], retries=2)
        device_types = json.loads(result.stdout).get("devicetypes", [])

        names_to_check = [name] if name else self.config.preferred_devices
        for n in names_to_check:
            for dt in device_types:
                if dt.get("name") == n:
                    log.info("Selected device type: %s (%s)", dt["name"], dt["identifier"])
                    return dt["identifier"]

        # Fallback: any iPhone
        for dt in device_types:
            if "iPhone" in dt.get("name", ""):
                log.info("Fallback device type: %s (%s)", dt["name"], dt["identifier"])
                return dt["identifier"]

        raise SimulatorNotFound("No iPhone device type found")

    def find_existing_device(self, runtime_prefix: Optional[str] = None) -> Optional[str]:
        """Find an existing available iPhone simulator (not booted).

        Returns UDID or None.
        """
        devices_data = self.list_devices()
        for runtime_id, devices in devices_data.get("devices", {}).items():
            if "iOS" not in runtime_id:
                continue
            if runtime_prefix and runtime_prefix not in runtime_id:
                continue
            for pref in self.config.preferred_devices:
                for d in devices:
                    if (d.get("name") == pref
                            and d.get("isAvailable", False)
                            and d.get("state") != DeviceState.BOOTED):
                        log.info("Found existing device: %s (%s)", d["name"], d["udid"])
                        return d["udid"]
            # Fallback: any available iPhone
            for d in devices:
                if (d.get("isAvailable", False)
                        and "iPhone" in d.get("name", "")
                        and d.get("state") != DeviceState.BOOTED):
                    log.info("Found existing device (fallback): %s (%s)", d["name"], d["udid"])
                    return d["udid"]
        return None

    # -------------------------------------------------------------------
    # Lifecycle operations
    # -------------------------------------------------------------------

    def create_simulator(
        self,
        runtime_prefix: Optional[str] = None,
        device_name: Optional[str] = None,
    ) -> str:
        """Create a fresh simulator device.

        Args:
            runtime_prefix: e.g. "iOS-18". Auto-resolves to full identifier.
            device_name: e.g. "iPhone 16". Auto-resolves to device type.

        Returns:
            UDID of the created simulator.
        """
        runtime_id = self.find_runtime(runtime_prefix)
        device_type_id = self.find_device_type(device_name)

        # Unique name to avoid collisions on shared runners
        sim_name = f"ci-sim-{int(time.time())}-{os.getpid()}"

        log.info("Creating simulator: %s (type=%s, runtime=%s)", sim_name, device_type_id, runtime_id)
        result = self._run_simctl(["create", sim_name, device_type_id, runtime_id])
        udid = result.stdout.strip()
        log.info("Created simulator: %s (udid=%s)", sim_name, udid)
        return udid

    def get_device_state(self, udid: str) -> Optional[str]:
        """Poll the current state of a device by UDID."""
        try:
            devices_data = self.list_devices()
            for runtime_id, devices in devices_data.get("devices", {}).items():
                for d in devices:
                    if d.get("udid") == udid:
                        return d.get("state")
        except SimError:
            return None
        return None

    def boot(self, udid: str) -> None:
        """Send the boot command. Does not wait for readiness."""
        state = self.get_device_state(udid)
        if state == DeviceState.BOOTED:
            log.info("Simulator %s already booted", udid)
            return
        log.info("Booting simulator %s (current state: %s)...", udid, state)
        try:
            self._run_simctl(["boot", udid])
        except SimctlCommandError as e:
            # "Unable to boot device in current state: Booted" is fine
            if "Booted" in str(e.stderr):
                log.info("Simulator %s was already booting/booted", udid)
                return
            raise

    def wait_until_booted(self, udid: str) -> None:
        """Phase 1: Poll until device state is Booted."""
        deadline = time.time() + self.config.boot_timeout
        log.info("Waiting for simulator %s to reach Booted state (timeout: %.0fs)...",
                 udid, self.config.boot_timeout)

        while time.time() < deadline:
            state = self.get_device_state(udid)
            if state == DeviceState.BOOTED:
                elapsed = self.config.boot_timeout - (deadline - time.time())
                log.info("Simulator %s reached Booted state in %.1fs", udid, elapsed)
                return
            log.debug("Simulator state: %s (%.0fs remaining)", state, deadline - time.time())
            time.sleep(self.config.boot_poll_interval)

        state = self.get_device_state(udid)
        raise SimulatorBootTimeout(
            f"Simulator {udid} did not boot within {self.config.boot_timeout}s "
            f"(final state: {state})"
        )

    def wait_until_responsive(self, udid: str) -> None:
        """Phase 2: Verify simulator is actually responsive after Booted state.

        Uses `simctl spawn` to run a command inside the simulator, confirming
        the runtime is truly ready (not just reported as Booted).
        """
        deadline = time.time() + self.config.readiness_timeout
        log.info("Probing simulator %s responsiveness (timeout: %.0fs)...",
                 udid, self.config.readiness_timeout)

        while time.time() < deadline:
            try:
                self._run_simctl(
                    ["spawn", udid, "launchctl", "print", "system"],
                    retries=1,
                    timeout=self.config.readiness_probe_timeout,
                )
                elapsed = self.config.readiness_timeout - (deadline - time.time())
                log.info("Simulator %s is responsive (probe succeeded in %.1fs)", udid, elapsed)
                return
            except (SimctlCommandError, SimError) as e:
                log.debug("Readiness probe failed: %s", e)
                time.sleep(self.config.readiness_poll_interval)

        raise SimulatorReadinessTimeout(
            f"Simulator {udid} not responsive within {self.config.readiness_timeout}s"
        )

    def boot_and_wait(self, udid: str) -> None:
        """Full boot sequence: boot + wait for Booted state + wait for responsiveness."""
        self.boot(udid)
        self.wait_until_booted(udid)
        self.wait_until_responsive(udid)

    def shutdown(self, udid: str) -> None:
        """Shut down a simulator (best effort)."""
        try:
            self._run_simctl(["shutdown", udid], retries=2, timeout=15)
            log.info("Simulator %s shut down", udid)
        except SimError as e:
            log.warning("Shutdown failed (non-fatal): %s", e)

    def delete(self, udid: str) -> None:
        """Delete a simulator (best effort)."""
        try:
            self._run_simctl(["delete", udid], retries=2, timeout=15)
            log.info("Simulator %s deleted", udid)
        except SimError as e:
            log.warning("Delete failed (non-fatal): %s", e)

    def cleanup(self, udid: str) -> None:
        """Best-effort shutdown + delete. Never raises."""
        log.info("Cleaning up simulator %s...", udid)
        try:
            self.shutdown(udid)
        except Exception as e:
            log.warning("Cleanup shutdown failed (non-fatal): %s", e)
        try:
            self.delete(udid)
        except Exception as e:
            log.warning("Cleanup delete failed (non-fatal): %s", e)

    def erase(self, udid: str) -> None:
        """Erase simulator contents and settings."""
        self._run_simctl(["erase", udid])
        log.info("Simulator %s erased", udid)

    # -------------------------------------------------------------------
    # App lifecycle (for test execution)
    # -------------------------------------------------------------------

    def install_app(self, udid: str, app_path: str) -> None:
        """Install an app bundle on the simulator."""
        log.info("Installing %s on %s...", app_path, udid)
        self._run_simctl(["install", udid, app_path], timeout=60)

    def launch_app(
        self,
        udid: str,
        bundle_id: str,
        args: Optional[list[str]] = None,
    ) -> subprocess.Popen:
        """Launch an app and return the Popen handle for output capture.

        Uses --console --terminate-running-process for clean test runs.
        """
        cmd = [
            "xcrun", "simctl", "launch", "--console",
            "--terminate-running-process", udid, bundle_id,
        ]
        if args:
            cmd.extend(args)
        log.info("Launching %s on %s with args: %s", bundle_id, udid, args or [])
        return subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def terminate_app(self, udid: str, bundle_id: str) -> None:
        """Terminate a running app (best effort, with timeout)."""
        try:
            self._run_simctl(
                ["terminate", udid, bundle_id],
                retries=1,
                timeout=10,
                check=False,
            )
        except SimError:
            pass

    # -------------------------------------------------------------------
    # Diagnostics
    # -------------------------------------------------------------------

    def collect_diagnostics(self, udid: str, output_dir: str) -> list[str]:
        """Collect diagnostic info for debugging CI failures.

        Returns list of paths to collected diagnostic files.
        """
        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)
        collected = []

        # 1. Device list snapshot
        try:
            result = self._run_simctl(["list", "devices", "-j"], retries=1, timeout=10)
            devices_file = output_path / "simctl-devices.json"
            devices_file.write_text(result.stdout)
            collected.append(str(devices_file))
            log.info("Saved device list to %s", devices_file)
        except SimError as e:
            log.warning("Failed to collect device list: %s", e)

        # 2. Runtime list
        try:
            result = self._run_simctl(["list", "runtimes", "-j"], retries=1, timeout=10)
            runtimes_file = output_path / "simctl-runtimes.json"
            runtimes_file.write_text(result.stdout)
            collected.append(str(runtimes_file))
        except SimError as e:
            log.warning("Failed to collect runtime list: %s", e)

        # 3. Simulator device log (last 5 minutes)
        try:
            result = self._run_simctl(
                ["spawn", udid, "log", "show", "--last", "5m",
                 "--predicate",
                 'process == "RuntimeTestsApp" OR '
                 '(process == "ReportCrash" AND eventMessage CONTAINS "RuntimeTestsApp")',
                 "--style", "compact"],
                retries=1,
                timeout=30,
                check=False,
            )
            if result.stdout:
                device_log_file = output_path / "device-log.txt"
                device_log_file.write_text(result.stdout)
                collected.append(str(device_log_file))
                log.info("Saved device log to %s", device_log_file)
        except SimError as e:
            log.warning("Failed to collect device log: %s", e)

        # 4. Process snapshot
        try:
            result = subprocess.run(
                ["ps", "aux"],
                capture_output=True, text=True, timeout=5,
            )
            # Filter to simulator-related processes
            lines = [l for l in result.stdout.splitlines()
                     if any(k in l.lower() for k in ["simulator", "coresim", "simctl", "runtime"])]
            if lines:
                ps_file = output_path / "simulator-processes.txt"
                ps_file.write_text("\n".join(lines))
                collected.append(str(ps_file))
        except Exception as e:
            log.warning("Failed to collect process list: %s", e)

        # 5. Crash logs
        crash_dir = Path.home() / "Library" / "Logs" / "DiagnosticReports"
        try:
            crash_files = sorted(crash_dir.glob("RuntimeTestsApp*.ips"), key=lambda p: p.stat().st_mtime, reverse=True)
            for cf in crash_files[:3]:  # Latest 3
                dest = output_path / cf.name
                dest.write_bytes(cf.read_bytes())
                collected.append(str(dest))
                log.info("Saved crash log: %s", cf.name)
        except Exception as e:
            log.warning("Failed to collect crash logs: %s", e)

        # 6. Xcode version
        try:
            result = subprocess.run(
                ["xcodebuild", "-version"],
                capture_output=True, text=True, timeout=5,
            )
            xcode_file = output_path / "xcode-version.txt"
            xcode_file.write_text(result.stdout)
            collected.append(str(xcode_file))
        except Exception:
            pass

        log.info("Collected %d diagnostic files in %s", len(collected), output_dir)
        return collected

    # -------------------------------------------------------------------
    # High-level convenience
    # -------------------------------------------------------------------

    def prepare_simulator(
        self,
        runtime_prefix: Optional[str] = None,
        device_name: Optional[str] = None,
        create_fresh: bool = True,
    ) -> str:
        """Create (or find) and fully boot a simulator.

        Args:
            runtime_prefix: e.g. "iOS-18"
            device_name: e.g. "iPhone 16"
            create_fresh: If True, always create new. If False, reuse existing.

        Returns:
            UDID of the ready simulator.
        """
        if create_fresh:
            udid = self.create_simulator(runtime_prefix, device_name)
        else:
            udid = self.find_existing_device(runtime_prefix)
            if not udid:
                log.info("No existing device found, creating fresh...")
                udid = self.create_simulator(runtime_prefix, device_name)

        self.boot_and_wait(udid)
        return udid


# ---------------------------------------------------------------------------
# CLI interface
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="iOS Simulator lifecycle manager for CI",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable debug logging")

    sub = parser.add_subparsers(dest="command", required=True)

    # create
    p_create = sub.add_parser("create", help="Create a fresh simulator")
    p_create.add_argument("--runtime", help="Runtime prefix (e.g. iOS-18)")
    p_create.add_argument("--device-type", help="Device name (e.g. 'iPhone 16')")

    # boot
    p_boot = sub.add_parser("boot", help="Boot a simulator by UDID")
    p_boot.add_argument("udid", help="Simulator UDID")

    # wait-ready
    p_wait = sub.add_parser("wait-ready", help="Wait for simulator to be fully responsive")
    p_wait.add_argument("udid", help="Simulator UDID")

    # prepare (create + boot + wait)
    p_prep = sub.add_parser("prepare", help="Create, boot, and wait for a simulator")
    p_prep.add_argument("--runtime", help="Runtime prefix (e.g. iOS-18)")
    p_prep.add_argument("--device-type", help="Device name (e.g. 'iPhone 16')")
    p_prep.add_argument("--reuse", action="store_true", help="Reuse existing device if available")

    # cleanup
    p_clean = sub.add_parser("cleanup", help="Shutdown and delete a simulator")
    p_clean.add_argument("udid", help="Simulator UDID")

    # diagnose
    p_diag = sub.add_parser("diagnose", help="Collect diagnostic info")
    p_diag.add_argument("udid", help="Simulator UDID")
    p_diag.add_argument("--output-dir", default="/tmp/sim-diagnostics", help="Output directory")

    # find
    p_find = sub.add_parser("find", help="Find an existing available simulator")
    p_find.add_argument("--runtime", help="Runtime prefix filter")

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    mgr = SimManager()

    if args.command == "create":
        udid = mgr.create_simulator(args.runtime, args.device_type)
        print(udid)

    elif args.command == "boot":
        mgr.boot(args.udid)

    elif args.command == "wait-ready":
        mgr.wait_until_booted(args.udid)
        mgr.wait_until_responsive(args.udid)

    elif args.command == "prepare":
        udid = mgr.prepare_simulator(
            args.runtime, args.device_type,
            create_fresh=not args.reuse,
        )
        print(udid)

    elif args.command == "cleanup":
        mgr.cleanup(args.udid)

    elif args.command == "diagnose":
        files = mgr.collect_diagnostics(args.udid, args.output_dir)
        for f in files:
            print(f)

    elif args.command == "find":
        udid = mgr.find_existing_device(args.runtime)
        if udid:
            print(udid)
        else:
            log.error("No available simulator found")
            sys.exit(1)


if __name__ == "__main__":
    main()
