#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Resolve, provision and verify a toolchain profile for GitHub-hosted macOS runners.

The profile manifests in build/toolchains/ are the only place a .NET SDK, workload set or
Xcode version is pinned. Workflows read every pin through this script, so a profile change
moves CI with it; test_toolchain_ci.py fails if a workflow hard-codes one.

    toolchain_ci.py resolve [--profile NAME ...]   # JSON (or GITHUB_OUTPUT) of every pin
    toolchain_ci.py provision --profile NAME --dotnet-root DIR
    toolchain_ci.py verify --profile NAME [--dotnet-root DIR]
    toolchain_ci.py simulator --platform iOS [--create NAME]  # runtime/device for the selected Xcode

`provision` installs an isolated .NET root for the profile launcher (build.sh
--toolchain-profile), which validates the same pins again before it runs Nuke. `verify`
without --dotnet-root checks the unprofiled repository workflow: the SDK and workload set
that global.json selects from the PATH `dotnet`, and the selected Xcode.
"""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.request

REPO = Path(__file__).resolve().parents[3]
PROFILES = REPO / "build" / "toolchains"
# The unprofiled repository workflow (global.json + the PATH dotnet) is this profile's lane.
UNPROFILED_PROFILE = "net10-xcode26"
REQUIRED_WORKLOADS = ("ios", "tvos", "macos", "maccatalyst")
# GitHub-hosted image label per Xcode major. The images install each Xcode at
# /Applications/Xcode_<version>.app. xcode-27 is the actions/runner-images public preview
# (https://github.com/actions/runner-images/issues/14404); move to its GA label when it ships.
HOSTED_RUNNERS = {26: "macos-26", 27: "xcode-27"}
# Microsoft's official install script, the one actions/setup-dotnet wraps. It installs into an
# explicit directory, which setup-dotnet cannot do for a runtime-only install.
DOTNET_INSTALL_SCRIPT = "https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh"
# Build configuration of each framework's full simulator suite in the release gates. net10 is
# Mono JIT, which the harness builds Debug. net11 is CoreCLR: Release is the lane with a seeded
# .NET 11 identity floor, and Debug runs the interpreter, which cannot call Swift until the
# interpreter bridge lands. Moving net11 to Debug needs that lane's floor seeded first.
SIMULATOR_CONFIGURATIONS = {"net10.0": "Debug", "net11.0": "Release"}
VERSION = re.compile(r"\d+(\.\d+){1,3}(-[0-9A-Za-z.]+)?")


def available_profiles():
    return sorted(path.stem for path in PROFILES.glob("*.json"))


def load_profile(name):
    path = PROFILES / f"{name}.json"
    if not re.fullmatch(r"[a-z0-9-]+", name) or not path.is_file():
        raise SystemExit(f"Unknown toolchain profile '{name}'. Known: {', '.join(available_profiles())}")
    return json.loads(path.read_text())


def resolve(name):
    """Every value a workflow needs for one profile, derived only from its manifest."""
    profile = load_profile(name)
    for key in ("sdkVersion", "workloadVersion", "xcodeVersion"):
        if not VERSION.fullmatch(str(profile.get(key, ""))):
            raise SystemExit(f"{name}: {key} is not a version: {profile.get(key)!r}")
    xcode = profile["xcodeVersion"]
    major = int(xcode.split(".")[0])
    if major not in HOSTED_RUNNERS:
        raise SystemExit(f"{name}: no hosted runner image is mapped for Xcode {major} (HOSTED_RUNNERS)")
    app = f"/Applications/Xcode_{xcode}.app"
    framework = profile.get("managedFramework", "net10.0")
    return {
        "profile": name,
        "unprofiled": name == UNPROFILED_PROFILE,
        "sdk_version": profile["sdkVersion"],
        "workload_version": profile["workloadVersion"],
        "host_runtimes": " ".join(host_runtimes(profile)),
        "xcode_version": xcode,
        "xcode_app": app,
        "developer_dir": f"{app}/Contents/Developer",
        "managed_framework": framework,
        "ios_tfm": f"{framework}-ios",
        "runner": HOSTED_RUNNERS[major],
        "simulator_configuration": SIMULATOR_CONFIGURATIONS[framework],
    }


def host_runtimes(profile):
    runtimes = profile.get("hostRuntimes", [])
    if not isinstance(runtimes, list) or not all(isinstance(r, str) and VERSION.fullmatch(r) for r in runtimes):
        raise SystemExit(f"hostRuntimes must be a list of Microsoft.NETCore.App versions: {runtimes!r}")
    return runtimes


def global_json(profile):
    # The launcher's exact pin (run-toolchain.py global_json): never roll forward.
    return {"sdk": {"version": profile["sdkVersion"], "rollForward": "disable",
                    "allowPrerelease": profile.get("allowPrerelease", False),
                    "workloadVersion": profile["workloadVersion"]}}


def run(args, **kwargs):
    print("+ " + " ".join(str(a) for a in args), flush=True)
    return subprocess.run([str(a) for a in args], check=True, **kwargs)


def capture(args, cwd=None, env=None):
    return subprocess.run([str(a) for a in args], check=True, cwd=cwd, env=env,
                          capture_output=True, text=True).stdout.strip()


def github_append(variable, lines):
    target = os.environ.get(variable)
    if not target:
        return
    with open(target, "a") as handle:
        for line in lines:
            handle.write(line + "\n")


def profile_env(dotnet_root, developer_dir):
    env = dict(os.environ)
    for key in ("MSBuildSDKsPath", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", "DOTNET_ROLL_FORWARD"):
        env.pop(key, None)
    env.update({"DOTNET_ROOT": str(dotnet_root), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_NOLOGO": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true", "DEVELOPER_DIR": developer_dir,
                "PATH": os.pathsep.join([str(dotnet_root), env.get("PATH", os.defpath)])})
    return env


def check_installation(name, profile, dotnet, cwd, env, developer_dir, require_workloads=True):
    """The checks the launcher repeats before Nuke runs, surfaced early with CI-friendly errors.

    The unprofiled workflow restores only the workloads its projects ask for (`dotnet workload
    restore`), so it is held to the SDK, the workload set and Xcode, not the full workload list.
    """
    problems = []
    sdk = capture([dotnet, "--version"], cwd=cwd, env=env)
    if sdk != profile["sdkVersion"]:
        problems.append(f"SDK {sdk} is selected; {name} pins {profile['sdkVersion']}")
    workloads = capture([dotnet, "workload", "--info"], cwd=cwd, env=env)
    if not re.search(r"Workload version:\s*" + re.escape(profile["workloadVersion"]) + r"\s", workloads + "\n"):
        problems.append(f"workload set {profile['workloadVersion']} is not selected:\n{workloads}")
    if require_workloads:
        problems += [f"workload {w} is not installed" for w in REQUIRED_WORKLOADS if f"[{w}]" not in workloads]
    runtimes = capture([dotnet, "--list-runtimes"], cwd=cwd, env=env)
    problems += [f"Microsoft.NETCore.App {r} is not installed" for r in host_runtimes(profile)
                 if not re.search(rf"^Microsoft\.NETCore\.App {re.escape(r)} ", runtimes, re.M)]
    xcode = capture(["xcodebuild", "-version"], env=dict(env, DEVELOPER_DIR=developer_dir)).splitlines()
    if not xcode or xcode[0] != "Xcode " + profile["xcodeVersion"]:
        problems.append(f"{developer_dir} is {' '.join(xcode)}; {name} pins Xcode {profile['xcodeVersion']}")
    if problems:
        raise SystemExit(f"{name} toolchain mismatch:\n  " + "\n  ".join(problems))
    print(f"{name}: SDK {sdk}, workload set {profile['workloadVersion']}, {' '.join(xcode)}", flush=True)


def provision(name, dotnet_root, developer_dir=None):
    profile = load_profile(name)
    resolved = resolve(name)
    developer_dir = developer_dir or resolved["developer_dir"]
    if not Path(developer_dir).is_dir():
        raise SystemExit(f"{name} needs Xcode {profile['xcodeVersion']} at {resolved['xcode_app']}; "
                         f"this runner image does not provide it. Hosted label for this Xcode: {resolved['runner']}.")
    root = Path(dotnet_root).expanduser().resolve()
    if root.exists() and any(root.iterdir()) and not (root / "dotnet").is_file():
        raise SystemExit(f"Refusing to install into a non-empty, non-.NET directory: {root}")
    root.mkdir(parents=True, exist_ok=True)
    env = profile_env(root, developer_dir)
    with tempfile.TemporaryDirectory(prefix="toolchain-ci-") as scratch:
        scratch = Path(scratch)
        script = scratch / "dotnet-install.sh"
        with urllib.request.urlopen(DOTNET_INSTALL_SCRIPT, timeout=120) as response:
            script.write_bytes(response.read())
        common = ["bash", script, "--install-dir", root, "--no-path"]
        run([*common, "--version", profile["sdkVersion"]], env=env)
        for runtime in host_runtimes(profile):
            run([*common, "--runtime", "dotnet", "--version", runtime], env=env)
        # Install from a directory pinned exactly like the launcher's, so neither the
        # repository global.json nor another SDK on the runner selects the workload band.
        # The pin's workloadVersion selects the workload set; the SDK rejects a --version
        # alongside it.
        pin = scratch / "pin"
        pin.mkdir()
        (pin / "global.json").write_text(json.dumps(global_json(profile), indent=2) + "\n")
        run([root / "dotnet", "workload", "install", *REQUIRED_WORKLOADS], cwd=pin, env=env)
        check_installation(name, profile, root / "dotnet", pin, env, developer_dir)
    github_append("GITHUB_ENV", [f"SWIFT_BINDINGS_CI_DOTNET_ROOT={root}",
                                 f"SWIFT_BINDINGS_CI_DEVELOPER_DIR={developer_dir}",
                                 f"DEVELOPER_DIR={developer_dir}"])


def verify(name, dotnet_root=None, developer_dir=None):
    profile = load_profile(name)
    developer_dir = developer_dir or resolve(name)["developer_dir"]
    if dotnet_root:
        root = Path(dotnet_root).expanduser().resolve()
        with tempfile.TemporaryDirectory(prefix="toolchain-ci-") as pin:
            (Path(pin) / "global.json").write_text(json.dumps(global_json(profile)) + "\n")
            check_installation(name, profile, root / "dotnet", pin, profile_env(root, developer_dir), developer_dir)
        return
    if name != UNPROFILED_PROFILE:
        raise SystemExit(f"The unprofiled workflow is the {UNPROFILED_PROFILE} lane; run {name} through "
                         "build.sh --toolchain-profile with --dotnet-root")
    # What the repository workflow actually selects: global.json from the checkout root, the
    # PATH dotnet and the xcode-select'ed (or DEVELOPER_DIR) Xcode.
    selected = capture(["xcode-select", "-p"]) if "DEVELOPER_DIR" not in os.environ else os.environ["DEVELOPER_DIR"]
    check_installation(name, profile, "dotnet", REPO, dict(os.environ), selected, require_workloads=False)


SIMULATOR_FAMILIES = {"iOS": ("iphonesimulator", "iPhone"), "tvOS": ("appletvsimulator", "Apple TV")}


def select_simulator(platform, sdk, runtimes):
    """The runtime the selected Xcode's simulator SDK targets, and a device type it supports.

    Same major as the SDK and not older; the SDK's own version when it is installed (Xcode
    26.3's SDK is 26.2, and a runtime is never required to match the Xcode version).
    Returns None when no such runtime is installed.
    """
    family = SIMULATOR_FAMILIES[platform][1]
    wanted = tuple(int(x) for x in sdk.split(".")[:2])

    def version(runtime):
        return tuple(int(x) for x in runtime.get("version", "0").split(".")[:2])
    candidates = sorted((r for r in runtimes if r.get("platform") == platform and r.get("isAvailable")
                         and version(r)[0] == wanted[0] and version(r) >= wanted), key=version)
    if not candidates:
        return None
    runtime = candidates[0]
    devices = [d for d in runtime.get("supportedDeviceTypes", []) if d.get("productFamily") == family]
    if not devices:
        raise SystemExit(f"{runtime['name']} supports no {family} device type")
    # simctl lists supported device types newest first; any one exercises the same runtime.
    return runtime, devices[0]


def simulator(platform, create=None, download_missing=False):
    sdk = capture(["xcrun", "--sdk", SIMULATOR_FAMILIES[platform][0], "--show-sdk-version"])

    def installed():
        return json.loads(capture(["xcrun", "simctl", "list", "runtimes", "-j"]))["runtimes"]
    runtimes = installed()
    chosen = select_simulator(platform, sdk, runtimes)
    if chosen is None and download_missing:
        # Hosted images ship the runtime for each installed Xcode; this covers an image that
        # dropped it. The download needs no Apple ID.
        print(f"::warning::No {platform} {sdk} simulator runtime on this image; downloading it", flush=True)
        run(["xcodebuild", "-downloadPlatform", platform])
        runtimes = installed()
        chosen = select_simulator(platform, sdk, runtimes)
    if chosen is None:
        found = ", ".join(f"{r.get('name')} ({'available' if r.get('isAvailable') else 'unavailable'})"
                          for r in runtimes if r.get("platform") == platform) or "none"
        raise SystemExit(f"No {platform} {sdk} simulator runtime is installed (found: {found}). "
                         f"Install it with: xcodebuild -downloadPlatform {platform}")
    runtime, device = chosen
    lines = [f"runtime={runtime['identifier']}", f"runtime_name={runtime['name']}",
             f"device_type={device['identifier']}", f"device_name={device['name']}"]
    if create:
        udid = capture(["xcrun", "simctl", "create", create, device["identifier"], runtime["identifier"]])
        # Booted in the foreground so the device is already Booted when the job's
        # `simctl bootstatus -b` waits for it to be ready. A boot still in flight then makes
        # bootstatus send a second boot request, which fails once the first one lands.
        run(["xcrun", "simctl", "boot", udid])
        lines.append(f"udid={udid}")
        github_append("GITHUB_ENV", [f"SIM_UDID={udid}"])
    print("\n".join(lines))
    github_append("GITHUB_OUTPUT", lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    resolve_cmd = commands.add_parser("resolve", help="print profile pins as JSON")
    resolve_cmd.add_argument("--profile", action="append", help="profile name (repeatable; default: all)")
    resolve_cmd.add_argument("--github-output", metavar="NAME",
                             help="also write the JSON object to GITHUB_OUTPUT as NAME")
    provision_cmd = commands.add_parser("provision", help="install an isolated .NET root for a profile")
    provision_cmd.add_argument("--profile", required=True)
    provision_cmd.add_argument("--dotnet-root", required=True)
    provision_cmd.add_argument("--developer-dir", help="override the hosted image's Xcode path (local use)")
    verify_cmd = commands.add_parser("verify", help="check the selected toolchain matches a profile")
    verify_cmd.add_argument("--profile", required=True)
    verify_cmd.add_argument("--dotnet-root")
    verify_cmd.add_argument("--developer-dir", help="override the hosted image's Xcode path (local use)")
    simulator_cmd = commands.add_parser("simulator", help="simulator runtime and device type for the selected Xcode")
    simulator_cmd.add_argument("--platform", choices=("iOS", "tvOS"), default="iOS")
    simulator_cmd.add_argument("--create", metavar="NAME", help="create and boot a device, export SIM_UDID")
    simulator_cmd.add_argument("--download-missing", action="store_true",
                               help="xcodebuild -downloadPlatform when the runtime is not installed")
    options = parser.parse_args(argv)

    if options.command == "resolve":
        names = options.profile or available_profiles()
        resolved = {name: resolve(name) for name in names}
        text = json.dumps(resolved, sort_keys=True)
        print(json.dumps(resolved, indent=2, sort_keys=True))
        if options.github_output:
            github_append("GITHUB_OUTPUT", [f"{options.github_output}={text}"])
    elif options.command == "provision":
        provision(options.profile, options.dotnet_root, options.developer_dir)
    elif options.command == "verify":
        verify(options.profile, options.dotnet_root, options.developer_dir)
    else:
        simulator(options.platform, options.create, options.download_missing)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as error:
        print(f"toolchain_ci: command failed ({error.returncode}): {' '.join(map(str, error.cmd))}", file=sys.stderr)
        if error.stderr:
            print(error.stderr, file=sys.stderr)
        sys.exit(1)
