#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
CI orchestrator for iOS Simulator tests.

Manages the full lifecycle: simulator creation, parallel boot + build,
test execution, diagnostics collection, and cleanup. Designed to replace
inline bash/YAML in GitHub Actions workflows.

Usage:
    # Full pipeline (create fresh sim, parallel boot+build, test, cleanup)
    python3 ci_ios_test.py

    # Use existing device
    python3 ci_ios_test.py --reuse-device

    # With specific runtime
    python3 ci_ios_test.py --runtime iOS-18

    # Just prepare sim + output UDID (for multi-step workflows)
    python3 ci_ios_test.py --prepare-only

    # Just run tests with pre-booted sim
    python3 ci_ios_test.py --device-udid <UDID> --skip-prepare
"""

import argparse
import logging
import os
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, Future
from pathlib import Path
from typing import Optional

# Add parent directory so we can import sim_manager
sys.path.insert(0, str(Path(__file__).parent))
from sim_manager import (
    SimManager, SimConfig, SimError,
    SimulatorBootTimeout, SimulatorReadinessTimeout, SimulatorNotFound,
)

log = logging.getLogger("ci_ios_test")

# ---------------------------------------------------------------------------
# Error classification
# ---------------------------------------------------------------------------

# Patterns in stderr/output that indicate infrastructure (retryable) failures
INFRA_FAILURE_PATTERNS = [
    "failed to boot",
    "unable to boot",
    "unable to lookup in current state",
    "CoreSimulatorService connection interrupted",
    "timed out waiting",
    "launchd",
    "bootstrap",
    "SimulatorBootTimeout",
    "SimulatorReadinessTimeout",
    "domain error",
    "Unable to negotiate with CoreSimulatorService",
]


def is_infra_failure(error: Exception) -> bool:
    """Classify whether an error is retryable infrastructure vs real test failure."""
    if isinstance(error, (SimulatorBootTimeout, SimulatorReadinessTimeout, SimulatorNotFound)):
        return True
    msg = str(error).lower()
    return any(pat.lower() in msg for pat in INFRA_FAILURE_PATTERNS)


# ---------------------------------------------------------------------------
# Build step
# ---------------------------------------------------------------------------

def run_build(test_framework_dir: str, skip_regen: bool = True) -> None:
    """Run the TestFramework build steps.

    When used in the full CI pipeline, build-and-test.sh has already run
    by this point. We just need to ensure the RuntimeTestsApp is built.
    """
    log.info("=== BUILD: Starting dotnet build ===")
    app_dir = os.path.join(test_framework_dir, "RuntimeTestsApp")

    result = subprocess.run(
        ["dotnet", "build", "-c", "Debug"],
        cwd=app_dir,
        capture_output=True,
        text=True,
        timeout=300,
    )

    if result.returncode != 0:
        log.error("Build failed:\n%s", result.stderr[-2000:] if result.stderr else result.stdout[-2000:])
        raise RuntimeError(f"dotnet build failed with exit code {result.returncode}")

    # Verify app bundle exists
    app_bundle = os.path.join(
        app_dir, "bin", "Debug", "net10.0-ios", "iossimulator-arm64", "RuntimeTestsApp.app"
    )
    if not os.path.isdir(app_bundle):
        raise RuntimeError(f"App bundle not found at {app_bundle}")

    log.info("=== BUILD: Success ===")


# ---------------------------------------------------------------------------
# Test execution
# ---------------------------------------------------------------------------

def run_tests(
    test_framework_dir: str,
    device_udid: str,
    tier: int = 2,
    timeout: int = 90,
    safe_only: bool = True,
    skip_regen: bool = True,
    max_test_retries: int = 1,
    deadline: Optional[float] = None,
) -> int:
    """Run runtime tests using the existing run-runtime-tests.sh script.

    We delegate to the existing script to preserve all the nuanced crash
    detection, Mono JIT tolerance, and result classification logic.

    Retries once on timeout/infrastructure failure (app hang, launch failure),
    but only if enough time remains before the deadline.

    Args:
        deadline: Absolute time.time() by which we must finish. Used to
                  skip retries that can't complete and to shrink subprocess
                  timeouts on later attempts.

    Returns:
        Exit code from run-runtime-tests.sh
    """
    cmd = [
        "./run-runtime-tests.sh",
        "--tier", str(tier),
        "--timeout", str(timeout),
        "--device-udid", device_udid,
    ]
    if safe_only:
        cmd.append("--safe-only")
    if skip_regen:
        cmd.append("--skip-regen")

    # First attempt: generous timeout (build + test).
    # Retry overhead depends on whether the previous build completed.
    FIRST_ATTEMPT_OVERHEAD = 300  # seconds for wrapper+bridge+app build
    RETRY_OVERHEAD_CLEAN = 300    # full rebuild if build was interrupted
    RETRY_OVERHEAD_CACHED = 120   # incremental rebuild if build completed
    APP_BUNDLE_ID = "com.swiftbindings.runtimetestsapp"
    last_output = ""  # Track output from previous attempt for smart cleanup

    for attempt in range(1, max_test_retries + 2):
        if attempt > 1:
            # Determine if we need a full rebuild or incremental
            # If "Step 3" appeared in last output, build completed — artifacts are fine
            build_completed = "Step 3: Run on iOS Simulator" in last_output
            retry_overhead = RETRY_OVERHEAD_CACHED if build_completed else RETRY_OVERHEAD_CLEAN

            # Check if we have enough time for a retry
            min_retry_time = timeout + retry_overhead
            if deadline is not None:
                remaining = deadline - time.time()
                if remaining < min_retry_time:
                    log.warning(
                        "Only %.0fs remaining (need %ds for retry) — skipping retry",
                        remaining, min_retry_time,
                    )
                    return 1
                log.info("%.0fs remaining — enough for retry (need %ds)", remaining, min_retry_time)

            # Clean up after previous attempt to prevent stale state
            log.info("Cleaning up before retry (build_completed=%s)...", build_completed)
            try:
                mgr = SimManager()
                mgr.terminate_app(device_udid, APP_BUNDLE_ID)
            except Exception:
                pass  # App may not be running — that's fine

            if not build_completed:
                # Build was interrupted — clean corrupted AOT artifacts
                app_dir = os.path.join(test_framework_dir, "RuntimeTestsApp")
                for subdir in ("obj", "bin"):
                    path = os.path.join(app_dir, subdir)
                    if os.path.isdir(path):
                        log.info("Cleaning corrupted build artifacts: %s", path)
                        shutil.rmtree(path, ignore_errors=True)

            time.sleep(2)  # Let simulator settle

            log.info("=== TESTS: Retry attempt %d (previous run timed out) ===", attempt)
            gha_warning(f"Test retry attempt {attempt} after timeout/hang")

        # Calculate subprocess timeout
        if attempt == 1:
            overhead = FIRST_ATTEMPT_OVERHEAD
        else:
            overhead = RETRY_OVERHEAD_CLEAN if not build_completed else RETRY_OVERHEAD_CACHED
        if deadline is not None:
            remaining = deadline - time.time()
            # Use the lesser of our standard timeout and remaining time (minus 30s safety margin)
            subprocess_timeout = min(timeout + overhead, max(remaining - 30, timeout + 60))
        else:
            subprocess_timeout = timeout + overhead

        log.info("=== TESTS: Running runtime tests (tier=%d, timeout=%ds, attempt=%d, subprocess_timeout=%.0fs) ===",
                 tier, timeout, attempt, subprocess_timeout)
        log.info("Command: %s", " ".join(cmd))

        try:
            result = subprocess.run(
                cmd,
                cwd=test_framework_dir,
                capture_output=True,
                text=True,
                timeout=subprocess_timeout,
            )

            # Print output so it appears in GHA logs
            last_output = result.stdout or ""
            if result.stdout:
                print(result.stdout, end="", flush=True)
            if result.stderr:
                print(result.stderr, end="", file=sys.stderr, flush=True)

            if result.returncode == 0:
                log.info("=== TESTS: PASSED ===")
                return 0

            # Check if this is a timeout/hang (retryable) vs real test failure
            if "RUNTIME TESTS TIMEOUT" in last_output or "launch_failure" in last_output:
                if attempt <= max_test_retries:
                    log.warning("Tests timed out / app hung — will retry")
                    continue
            # Real test failure (assertions, etc.) — don't retry
            log.error("=== TESTS: FAILED (exit code %d) ===", result.returncode)
            return result.returncode

        except subprocess.TimeoutExpired as e:
            # subprocess itself timed out — capture output for smart cleanup
            if e.stdout:
                last_output = e.stdout if isinstance(e.stdout, str) else e.stdout.decode()
                print(last_output, end="", flush=True)
            else:
                last_output = ""
            if attempt <= max_test_retries:
                log.warning("Test subprocess timed out — will retry")
                continue
            log.error("=== TESTS: TIMED OUT (subprocess) ===")
            return 1

    return 1  # Should not reach here


# ---------------------------------------------------------------------------
# GitHub Actions helpers
# ---------------------------------------------------------------------------

def set_gha_output(name: str, value: str) -> None:
    """Set a GitHub Actions output variable."""
    output_file = os.environ.get("GITHUB_OUTPUT")
    if output_file:
        with open(output_file, "a") as f:
            f.write(f"{name}={value}\n")
        log.info("Set GHA output: %s=%s", name, value)
    else:
        log.debug("Not in GHA environment, skipping output: %s=%s", name, value)


def set_gha_env(name: str, value: str) -> None:
    """Set a GitHub Actions environment variable for subsequent steps."""
    env_file = os.environ.get("GITHUB_ENV")
    if env_file:
        with open(env_file, "a") as f:
            f.write(f"{name}={value}\n")


def gha_group(title: str) -> None:
    """Start a GitHub Actions log group."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::group::{title}", flush=True)


def gha_endgroup() -> None:
    """End a GitHub Actions log group."""
    if os.environ.get("GITHUB_ACTIONS"):
        print("::endgroup::", flush=True)


def gha_error(message: str) -> None:
    """Emit a GitHub Actions error annotation."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::error::{message}", flush=True)


def gha_warning(message: str) -> None:
    """Emit a GitHub Actions warning annotation."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::warning::{message}", flush=True)


# ---------------------------------------------------------------------------
# Orchestrator
# ---------------------------------------------------------------------------

def run_pipeline(
    test_framework_dir: str,
    runtime_prefix: Optional[str] = None,
    device_name: Optional[str] = None,
    device_udid: Optional[str] = None,
    create_fresh: bool = True,
    prepare_only: bool = False,
    skip_prepare: bool = False,
    skip_build: bool = False,
    tier: int = 2,
    test_timeout: int = 90,
    safe_only: bool = True,
    skip_regen: bool = True,
    max_infra_retries: int = 1,
    diag_dir: str = "/tmp/sim-diagnostics",
    step_timeout: int = 900,
) -> int:
    """Full CI pipeline: prepare simulator, build, test, cleanup.

    Args:
        step_timeout: Total wall-clock budget in seconds (matches the GHA
                      timeout-minutes value). Used to compute a deadline so
                      retries are skipped when insufficient time remains.

    Returns:
        0 on success, non-zero on failure.
    """
    pipeline_start = time.time()
    deadline = pipeline_start + step_timeout  # absolute time we must finish by
    mgr = SimManager()
    created_udid = None  # Track what we created for cleanup

    for attempt in range(1, max_infra_retries + 2):  # +2 because range is exclusive and attempt 1 is first try
        try:
            if attempt > 1:
                log.info("")
                log.info("=" * 60)
                log.info("RETRY attempt %d/%d (infrastructure failure)", attempt, max_infra_retries + 1)
                log.info("=" * 60)

            # Phase 1: Prepare simulator (parallel with build)
            if not skip_prepare and not device_udid:
                if skip_build:
                    # Sequential: just prepare the simulator
                    gha_group("Prepare iOS Simulator")
                    log.info("=== PREPARE: Creating and booting simulator ===")
                    created_udid = mgr.prepare_simulator(
                        runtime_prefix, device_name,
                        create_fresh=create_fresh,
                    )
                    device_udid = created_udid
                    gha_endgroup()
                else:
                    # Parallel: boot simulator + build simultaneously
                    gha_group("Parallel: Boot Simulator + Build")
                    log.info("=== PARALLEL: Starting simulator boot + dotnet build ===")

                    with ThreadPoolExecutor(max_workers=2) as executor:
                        sim_future: Future = executor.submit(
                            mgr.prepare_simulator,
                            runtime_prefix, device_name,
                            create_fresh,
                        )
                        build_future: Future = executor.submit(
                            run_build, test_framework_dir, skip_regen,
                        )

                        # Wait for both, collect errors
                        errors = []
                        for name, future in [("simulator", sim_future), ("build", build_future)]:
                            try:
                                result = future.result(timeout=360)
                                if name == "simulator":
                                    created_udid = result
                                    device_udid = result
                                    log.info("Simulator ready: %s", device_udid)
                            except Exception as e:
                                log.error("%s failed: %s", name, e)
                                errors.append((name, e))

                        if errors:
                            # If simulator failed, raise that (possibly retryable)
                            for name, err in errors:
                                if name == "simulator":
                                    raise err
                            # Otherwise build failed (not retryable)
                            _, err = errors[0]
                            raise err

                    gha_endgroup()
                    skip_build = True  # Don't rebuild on retry

                set_gha_output("device_udid", device_udid)
                set_gha_env("SIM_UDID", device_udid)
                log.info("Simulator UDID: %s", device_udid)

            elif device_udid:
                log.info("Using provided simulator: %s", device_udid)

            if prepare_only:
                log.info("=== PREPARE-ONLY: Simulator ready, exiting ===")
                print(device_udid)
                return 0

            # Phase 2: Build (if not already done in parallel)
            if not skip_build:
                gha_group("Build RuntimeTestsApp")
                run_build(test_framework_dir, skip_regen)
                gha_endgroup()
                skip_build = True

            # Phase 3: Run tests
            gha_group("Run iOS Simulator Tests")
            exit_code = run_tests(
                test_framework_dir,
                device_udid,
                tier=tier,
                timeout=test_timeout,
                safe_only=safe_only,
                skip_regen=skip_regen,
                deadline=deadline,
            )
            gha_endgroup()

            if exit_code == 0:
                return 0

            # Test failed — but is it infra or real?
            # For now, test failures from run-runtime-tests.sh are considered real
            # (that script already handles Mono JIT tolerance internally)
            return exit_code

        except Exception as e:
            log.error("Pipeline error: %s", e)

            if is_infra_failure(e) and attempt <= max_infra_retries:
                gha_warning(f"Infrastructure failure (attempt {attempt}): {e}")
                log.info("Collecting diagnostics before retry...")
                if created_udid:
                    try:
                        mgr.collect_diagnostics(created_udid, diag_dir)
                    except Exception as diag_err:
                        log.warning("Diagnostics collection failed: %s", diag_err)
                    mgr.cleanup(created_udid)
                    created_udid = None
                    device_udid = None
                continue

            # Non-retryable or out of retries
            gha_error(f"Pipeline failed: {e}")
            if created_udid:
                log.info("Collecting diagnostics...")
                try:
                    mgr.collect_diagnostics(created_udid, diag_dir)
                except Exception as diag_err:
                    log.warning("Diagnostics collection failed: %s", diag_err)
            return 1

        finally:
            # Cleanup on the last attempt or on success
            # (Don't cleanup on retry — we do it explicitly above)
            if attempt > max_infra_retries or not is_infra_failure(sys.exc_info()[1] or Exception()):
                if created_udid:
                    gha_group("Cleanup Simulator")
                    mgr.cleanup(created_udid)
                    gha_endgroup()

    return 1  # Should not reach here


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="CI orchestrator for iOS Simulator tests",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Full pipeline (parallel boot+build, test, cleanup)
  python3 ci_ios_test.py --test-framework-dir TestFramework

  # Just prepare simulator and output UDID
  python3 ci_ios_test.py --prepare-only

  # Run tests with pre-booted simulator
  python3 ci_ios_test.py --device-udid ABC123 --skip-prepare

  # Use existing device instead of creating fresh
  python3 ci_ios_test.py --reuse-device
""",
    )

    # Simulator options
    sim_group = parser.add_argument_group("simulator")
    sim_group.add_argument("--runtime", help="iOS runtime prefix (e.g. iOS-18)")
    sim_group.add_argument("--device-type", help="Device name (e.g. 'iPhone 16')")
    sim_group.add_argument("--device-udid", help="Use pre-booted simulator UDID")
    sim_group.add_argument("--reuse-device", action="store_true",
                          help="Reuse existing device instead of creating fresh")
    sim_group.add_argument("--prepare-only", action="store_true",
                          help="Only prepare simulator, print UDID, and exit")
    sim_group.add_argument("--skip-prepare", action="store_true",
                          help="Skip simulator preparation (requires --device-udid)")

    # Build/test options
    test_group = parser.add_argument_group("test")
    test_group.add_argument("--test-framework-dir", default="TestFramework",
                           help="Path to TestFramework directory (default: TestFramework)")
    test_group.add_argument("--tier", type=int, default=2, help="Test tier (default: 2)")
    test_group.add_argument("--timeout", type=int, default=90, help="Test timeout in seconds (default: 90)")
    test_group.add_argument("--safe-only", action="store_true", default=True,
                           help="Skip [CrashRisk] classes (default: true)")
    test_group.add_argument("--no-safe-only", action="store_false", dest="safe_only",
                           help="Include [CrashRisk] classes")
    test_group.add_argument("--skip-regen", action="store_true", default=True,
                           help="Skip binding regeneration (default: true)")
    test_group.add_argument("--skip-build", action="store_true",
                           help="Skip dotnet build (app already built)")

    # Resilience options
    resilience_group = parser.add_argument_group("resilience")
    resilience_group.add_argument("--max-retries", type=int, default=1,
                                 help="Max infrastructure retries (default: 1)")
    resilience_group.add_argument("--diag-dir", default="/tmp/sim-diagnostics",
                                 help="Directory for diagnostic artifacts")
    resilience_group.add_argument("--step-timeout", type=int, default=1140,
                                 help="Total wall-clock budget in seconds (default: 1140 = 19 min)")

    # Logging
    parser.add_argument("-v", "--verbose", action="store_true", help="Debug logging")

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.skip_prepare and not args.device_udid:
        parser.error("--skip-prepare requires --device-udid")

    # Resolve test framework directory relative to repo root
    tf_dir = args.test_framework_dir
    if not os.path.isabs(tf_dir):
        # If running from repo root, TestFramework is a subdirectory
        # If running from .github/workflows context, adjust
        if not os.path.isdir(tf_dir):
            # Try relative to script location
            script_dir = Path(__file__).parent
            repo_root = script_dir.parent.parent
            tf_dir = str(repo_root / tf_dir)

    if not os.path.isdir(tf_dir):
        log.error("TestFramework directory not found: %s", tf_dir)
        sys.exit(1)

    exit_code = run_pipeline(
        test_framework_dir=tf_dir,
        runtime_prefix=args.runtime,
        device_name=args.device_type,
        device_udid=args.device_udid,
        create_fresh=not args.reuse_device,
        prepare_only=args.prepare_only,
        skip_prepare=args.skip_prepare,
        skip_build=args.skip_build,
        tier=args.tier,
        test_timeout=args.timeout,
        safe_only=args.safe_only,
        skip_regen=args.skip_regen,
        max_infra_retries=args.max_retries,
        diag_dir=args.diag_dir,
        step_timeout=args.step_timeout,
    )
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
