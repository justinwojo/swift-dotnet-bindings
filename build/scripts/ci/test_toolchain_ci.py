#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""The workflows take every toolchain pin from build/toolchains/*.json, never a literal.

Run: python3 -B build/scripts/ci/test_toolchain_ci.py -v (also part of `nuke test`).
"""

import json
import os
from pathlib import Path
import re
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parent))
import toolchain_ci  # noqa: E402

REPO = toolchain_ci.REPO
GITHUB = REPO / ".github"


def workflow_files():
    files = sorted((GITHUB / "workflows").glob("*.yml")) + sorted((GITHUB / "actions").glob("**/*.yml"))
    assert files, "no workflow files found"
    return files


def job_block(text, job):
    """The text of one top-level job in a workflow (two-space-indented key under jobs:)."""
    match = re.search(rf"^  {re.escape(job)}:\n(.*?)(?=^  \S|\Z)", text, re.M | re.S)
    if not match:
        raise AssertionError(f"job {job} not found")
    return match.group(1)


class WorkflowPinTests(unittest.TestCase):
    # Literals that would pin a toolchain outside the profile manifests.
    FORBIDDEN = {
        # SDK (10.0.107), workload set (11.0.100.1) or runtime (10.0.15); the repository's own
        # package versions (0.8.0, 26.2.0-smoke) do not match.
        ".NET version": re.compile(r"(?<![\w.])(?:[5-9]|\d{2})\.0\.\d+(\.\d+)?(?![\w.])"),
        # Profile names (net10-xcode26) are allowed; they are checked against the manifests below.
        "Xcode version or path": re.compile(r"(?<![\w-])Xcode[ _-]?\d", re.I),
        "literal dotnet-version": re.compile(r"dotnet-version:\s*['\"]?\d"),
        "literal macOS runner label": re.compile(r"runs-on:\s*['\"]?(macos|xcode)-", re.I),
        "literal xcode-select path": re.compile(r"xcode-select\s+(-s|--switch)\s+['\"]?/"),
        "literal workload set": re.compile(r"workload\s+(install|update)\b[^\n]*--version\s+\d"),
    }

    def test_workflows_hard_code_no_toolchain_pin(self):
        pins = set()
        for name in toolchain_ci.available_profiles():
            profile = toolchain_ci.load_profile(name)
            pins |= {profile["sdkVersion"], profile["workloadVersion"], *profile.get("hostRuntimes", [])}
        for path in workflow_files():
            text = path.read_text()
            for line_number, line in enumerate(text.splitlines(), 1):
                for label, pattern in self.FORBIDDEN.items():
                    with self.subTest(file=path.name, line=line_number, rule=label):
                        self.assertIsNone(pattern.search(line), f"{path.name}:{line_number} hard-codes a {label}: {line.strip()}")
                for pin in pins:
                    with self.subTest(file=path.name, line=line_number, pin=pin):
                        self.assertNotRegex(line, rf"(?<![\w.]){re.escape(pin)}(?![\w.])",
                                            f"{path.name}:{line_number} repeats the profile pin {pin}")

    def test_profile_names_in_workflows_exist(self):
        known = set(toolchain_ci.available_profiles())
        for path in workflow_files():
            for name in set(re.findall(r"\bnet\d+-xcode\d+\b", path.read_text())):
                with self.subTest(file=path.name, profile=name):
                    self.assertIn(name, known)

    def test_every_profile_gates_publish(self):
        text = (GITHUB / "workflows" / "release.yml").read_text()
        matrix = re.search(r"^  profile-gates:\n.*?^\s+profile:\s*\[([^\]]*)\]", text, re.M | re.S)
        self.assertIsNotNone(matrix, "release.yml has no profile-gates matrix")
        profiled = {p.strip() for p in matrix.group(1).split(",") if p.strip()}
        self.assertEqual(profiled | {toolchain_ci.UNPROFILED_PROFILE}, set(toolchain_ci.available_profiles()))
        self.assertNotIn(toolchain_ci.UNPROFILED_PROFILE, profiled)
        gates = job_block(text, "profile-gates")
        for gate in ("-- test", "--strict --compile-only", "ci_ios_test.py"):
            self.assertIn(gate, gates)
        self.assertNotIn("--class-filter", gates)
        self.assertIn("fail-fast: false", gates)
        publish = job_block(text, "publish-release")
        needs = set(re.findall(r"^      - ([A-Za-z0-9_-]+)$", publish.split("steps:", 1)[0], re.M))
        self.assertTrue({"profile-gates", "build-and-test", "bindingtests", "release-pack"} <= needs, needs)
        # The unprofiled lane's gates are the existing jobs; packing happens there, once.
        for job in ("build-and-test", "bindingtests", "release-pack"):
            self.assertIn(f"['{toolchain_ci.UNPROFILED_PROFILE}']", job_block(text, job))
        self.assertNotRegex(gates, r"--\s+pack\b|nuke pack|dotnet pack")

    def test_new_release_jobs_get_no_secrets_and_read_only_tokens(self):
        text = (GITHUB / "workflows" / "release.yml").read_text()
        for job in ("toolchains", "profile-gates"):
            block = job_block(text, job)
            self.assertNotIn("secrets.", block)
            self.assertRegex(block, r"permissions:\n\s+contents: read\n")
        self.assertNotIn("pull_request_target", text)

    def test_ci_runs_the_net11_compile_gate_and_coreclr_canary(self):
        text = (GITHUB / "workflows" / "ci.yml").read_text()
        job = job_block(text, "net11-coreclr")
        self.assertIn("['net11-xcode27']", job)
        self.assertIn("--strict --compile-only", job)
        self.assertIn('--class-filter "$class"', job)
        configuration = re.search(r"^  CORECLR_CANARY_CONFIGURATION:\s*(\S+)$", text, re.M)
        self.assertIsNotNone(configuration)
        self.assertIn(configuration.group(1), ("Release", "Debug"))
        classes = re.search(r"^  CORECLR_CANARY_CLASSES:\s*>-\n((?:    .*\n)+)", text, re.M)
        self.assertIsNotNone(classes)
        names = classes.group(1).split()
        self.assertGreaterEqual(len(names), 3)
        sources = "\n".join(p.read_text() for p in (REPO / "BindingTests" / "RuntimeTestsApp").rglob("*.cs"))
        for name in names:
            with self.subTest(test_class=name):
                self.assertRegex(sources, rf"\bpublic\s+(sealed\s+)?class\s+{re.escape(name)}\s*:\s*TestBase\b")


class ProfileTests(unittest.TestCase):
    def test_global_json_is_the_unprofiled_lane(self):
        # The repository workflow (no --toolchain-profile) is net10-xcode26. A profile run reads
        # the launcher workspace, whose global.json is the launcher's exact pin instead; the
        # unprofiled `nuke test` checks the checked-in file.
        pin = json.loads((REPO / "global.json").read_text())
        running = os.environ.get("SWIFT_BINDINGS_TOOLCHAIN_PROFILE")
        if running:
            self.assertEqual(pin, toolchain_ci.global_json(toolchain_ci.load_profile(running)))
            return
        pin = pin["sdk"]
        lane = toolchain_ci.load_profile(toolchain_ci.UNPROFILED_PROFILE)
        self.assertEqual(pin["workloadVersion"], lane["workloadVersion"])
        self.assertEqual(pin.get("rollForward"), "latestPatch")
        band = lambda v: tuple(int(x) for x in v.split("-")[0].split("."))
        sdk, wanted = band(pin["version"]), band(lane["sdkVersion"])
        # latestPatch stays within the feature band (10.0.1xx), so the pin must reach the lane's SDK.
        self.assertEqual((sdk[0], sdk[1], sdk[2] // 100), (wanted[0], wanted[1], wanted[2] // 100))
        self.assertLessEqual(sdk, wanted)

    def test_every_profile_resolves_to_a_hosted_runner(self):
        for name in toolchain_ci.available_profiles():
            with self.subTest(profile=name):
                resolved = toolchain_ci.resolve(name)
                profile = toolchain_ci.load_profile(name)
                self.assertEqual(resolved["sdk_version"], profile["sdkVersion"])
                self.assertEqual(resolved["workload_version"], profile["workloadVersion"])
                self.assertEqual(resolved["xcode_app"], f"/Applications/Xcode_{profile['xcodeVersion']}.app")
                self.assertEqual(resolved["developer_dir"], resolved["xcode_app"] + "/Contents/Developer")
                self.assertIn(resolved["runner"], toolchain_ci.HOSTED_RUNNERS.values())
                self.assertEqual(resolved["managed_framework"], profile.get("managedFramework", "net10.0"))
                self.assertIn(resolved["simulator_configuration"], ("Debug", "Release"))
                self.assertEqual(resolved["unprofiled"], name == toolchain_ci.UNPROFILED_PROFILE)

    def test_launch_global_json_matches_the_launcher(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("run_toolchain", REPO / "build/scripts/run-toolchain.py")
        launcher = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(launcher)
        for name in toolchain_ci.available_profiles():
            with self.subTest(profile=name):
                profile = toolchain_ci.load_profile(name)
                self.assertEqual(toolchain_ci.global_json(profile), launcher.global_json(profile))
                self.assertEqual(toolchain_ci.host_runtimes(profile), profile.get("hostRuntimes", []))

    def test_unknown_profile_is_rejected(self):
        with self.assertRaises(SystemExit):
            toolchain_ci.load_profile("net12-xcode28")
        with self.assertRaises(SystemExit):
            toolchain_ci.load_profile("../global")


class SimulatorSelectionTests(unittest.TestCase):
    @staticmethod
    def runtime(version, platform="iOS", available=True, devices=("iPhone 18 Pro", "iPhone 17")):
        return {"platform": platform, "version": version, "isAvailable": available,
                "name": f"{platform} {version}",
                "identifier": f"com.apple.CoreSimulator.SimRuntime.{platform}-{version.replace('.', '-')}",
                "supportedDeviceTypes": [
                    {"name": d, "productFamily": "Apple TV" if platform == "tvOS" else "iPhone",
                     "identifier": "com.apple.CoreSimulator.SimDeviceType." + d.replace(" ", "-")} for d in devices]
                + [{"name": "iPad Air", "productFamily": "iPad", "identifier": "ipad"}]}

    def test_prefers_the_sdk_version_then_the_nearest_newer_runtime(self):
        runtimes = [self.runtime("27.0"), self.runtime("27.3"), self.runtime("27.2"), self.runtime("28.0")]
        runtime, device = toolchain_ci.select_simulator("iOS", "27.2", runtimes)
        self.assertEqual(runtime["version"], "27.2")
        self.assertEqual(device["name"], "iPhone 18 Pro")
        # Xcode 26.3's simulator SDK is 26.2; a 26.3 runtime serves it when 26.2 is absent.
        runtime, _ = toolchain_ci.select_simulator("iOS", "26.2", [self.runtime("26.3"), self.runtime("27.2")])
        self.assertEqual(runtime["version"], "26.3")

    def test_never_falls_back_to_another_major_platform_or_unavailable_runtime(self):
        runtimes = [self.runtime("26.4"), self.runtime("28.0"), self.runtime("27.2", available=False),
                    self.runtime("27.2", platform="tvOS", devices=("Apple TV 4K",)), self.runtime("27.1")]
        self.assertIsNone(toolchain_ci.select_simulator("iOS", "27.2", runtimes))
        runtime, device = toolchain_ci.select_simulator("tvOS", "27.2", runtimes)
        self.assertEqual((runtime["platform"], device["name"]), ("tvOS", "Apple TV 4K"))

    def test_a_runtime_without_a_matching_device_family_is_an_error(self):
        with self.assertRaises(SystemExit):
            toolchain_ci.select_simulator("iOS", "27.2", [self.runtime("27.2", devices=())])


if __name__ == "__main__":
    # stdout, so Nuke does not log passing results as errors.
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
