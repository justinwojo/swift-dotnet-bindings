#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Run Nuke in a disposable, provenance-checked toolchain-specific checkout."""

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


MARKER = ".swift-bindings-toolchain.json"
SCHEMA = 1


def execute(args, *, cwd, env=None):
    return subprocess.check_output(args, cwd=cwd, env=env, text=True).strip()


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def global_json(profile):
    return {"sdk": {"version": profile["sdkVersion"], "rollForward": "disable",
                    "allowPrerelease": profile.get("allowPrerelease", False), "workloadVersion": profile["workloadVersion"]}}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def source_inventory(source):
    # Include uncommitted and new source, but never import ignored build products.
    raw = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=source)
    entries = []
    for name in sorted(set(os.fsdecode(x) for x in raw.split(b"\0") if x)):
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise RuntimeError(f"Unsafe source path: {name}")
        path = source / relative
        if not path.exists() and not path.is_symlink():
            continue  # A tracked deletion is part of the current input state.
        if path.is_symlink():
            target = os.readlink(path)
            if not path.resolve().is_relative_to(source):
                raise RuntimeError(f"Source symlink escapes the checkout: {name}")
            entries.append([name, "symlink", target])
        elif path.is_file():
            entries.append([name, path.stat().st_mode & 0o777,
                            hashlib.sha256(path.read_bytes()).hexdigest()])
        else:
            raise RuntimeError(f"Unsupported source entry (submodule/directory): {name}")
    return entries


def environment(profile, state, inherited=None):
    env = dict(os.environ if inherited is None else inherited)
    framework = profile.get("managedFramework", "net10.0")
    if framework not in ("net10.0", "net11.0"):
        raise RuntimeError(f"Unsupported profile managed framework: {framework}")
    root = str(Path(profile["dotnetRoot"]).expanduser().resolve())
    developer = str(Path(profile["developerDirectory"]).expanduser().resolve())
    for key in ("MSBuildSDKsPath", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
                "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
                "SDKROOT", "SWIFT_INTERFACE_PARSER_PATH", "GIT_DIR", "GIT_WORK_TREE",
                "GIT_INDEX_FILE", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE"):
        env.pop(key, None)
    env.update({
        "SWIFT_BINDINGS_MANAGED_FRAMEWORK": framework,
        "DOTNET_ROOT": root, "DOTNET_ROOT_ARM64": root,
        "DOTNET_HOST_PATH": str(Path(root) / "dotnet"),
        "DOTNET_CLI_HOME": str(state / "cli-home"),
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        # A packaged generator discovers development source by walking its ancestors.
        # Keep installed packages outside the owner checkout so verification uses the
        # packaged Runtime rather than the owner's unrelated SDK/workload pin.
        "NUGET_PACKAGES": str(temporary_root(state) / "packages"),
        "NUGET_HTTP_CACHE_PATH": str(state / "http-cache"),
        # Scratch consumers inherit only the profile pin, outside repository build props.
        # A short external path also leaves room for Unix socket names.
        "TMPDIR": str(temporary_root(state)) + "/", "DEVELOPER_DIR": developer,
        "PATH": os.pathsep.join([root, developer + "/Toolchains/XcodeDefault.xctoolchain/usr/bin",
                                  developer + "/usr/bin", env.get("PATH", os.defpath)]),
    })
    return env


def temporary_root(state):
    return Path("/private/tmp") / f"swift-bindings-tc-{os.getuid()}-{digest(str(state.resolve()))[:16]}"


def prepare_temporary_directory(path, state, profile):
    if path.is_symlink():
        raise RuntimeError(f"Refusing a symlink temporary directory: {path}")
    marker = path / ".swift-bindings-profile-owner.json"
    owner = {"state": str(state.resolve())}
    if path.exists():
        if marker.is_symlink() or not marker.is_file() or json.loads(marker.read_text()) != owner:
            raise RuntimeError(f"Refusing an unowned temporary directory: {path}")
    else:
        path.mkdir(mode=0o700, parents=True)
        write_json(marker, owner)
    pin = path / "global.json"
    if pin.is_symlink():
        raise RuntimeError(f"Refusing a symlink temporary SDK pin: {pin}")
    write_json(pin, global_json(profile))


def toolchain_receipt(profile, state, env):
    control = state / "control"
    control.mkdir(exist_ok=True)
    write_json(control / "global.json", global_json(profile))
    host = env["DOTNET_HOST_PATH"]
    sdk = execute([host, "--version"], cwd=control, env=env)
    workloads = execute([host, "workload", "--info"], cwd=control, env=env)
    xcode = execute(["xcodebuild", "-version"], cwd=control, env=env)
    if sdk != profile["sdkVersion"]:
        raise RuntimeError(f"Expected SDK {profile['sdkVersion']}, got {sdk}")
    if not re.search(r"Workload version:\s*" + re.escape(profile["workloadVersion"]) + r"\s", workloads + "\n"):
        raise RuntimeError(f"Workload set {profile['workloadVersion']} is not selected:\n{workloads}")
    if xcode.splitlines()[0] != "Xcode " + profile["xcodeVersion"]:
        raise RuntimeError(f"Unexpected Xcode: {xcode}")
    for workload in ("ios", "tvos", "macos", "maccatalyst"):
        if f"[{workload}]" not in workloads:
            raise RuntimeError(f"Required workload is not installed: {workload}")
    # Include exact manifest identities, compiler build, paths and SDK versions, not mtimes.
    ref_packs = {}
    for family in sorted((Path(env["DOTNET_ROOT"]) / "packs").glob("Microsoft.*.Ref*")):
        for assembly in sorted(family.glob("*/ref/net*/*.dll")):
            ref_packs[str(assembly.relative_to(Path(env["DOTNET_ROOT"])))] = hashlib.sha256(assembly.read_bytes()).hexdigest()
    return {"sdk": sdk, "workloads": workloads, "xcode": xcode, "referencePacks": ref_packs,
            # Preserve the legacy receipt shape/cache identity; the new profile additionally
            # records the distinct managed app target and its side-by-side build-host runtime.
            **({"managedFramework": env["SWIFT_BINDINGS_MANAGED_FRAMEWORK"],
                "runtimes": execute([host, "--list-runtimes"], cwd=control, env=env)}
               if env["SWIFT_BINDINGS_MANAGED_FRAMEWORK"] != "net10.0" else {}),
            "dotnetRoot": env["DOTNET_ROOT"], "developerDirectory": env["DEVELOPER_DIR"],
            "temporaryDirectory": env["TMPDIR"], "nugetPackages": env["NUGET_PACKAGES"],
            "swift": execute(["xcrun", "swiftc", "--version"], cwd=control, env=env),
            "nativeSdks": {sdk: {
                "path": execute(["xcrun", "--sdk", sdk, "--show-sdk-path"], cwd=control, env=env),
                "version": execute(["xcrun", "--sdk", sdk, "--show-sdk-version"], cwd=control, env=env)}
                for sdk in ("iphoneos", "iphonesimulator", "appletvos", "appletvsimulator", "macosx")}}


@contextlib.contextmanager
def profile_lock(state):
    with (state / "run.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError(f"Another invocation owns this profile: {state}") from None
        yield


def validate_arguments(arguments):
    # A qualification run may compare canonical baselines, but cannot reseed/promote them.
    for argument in arguments:
        normalized = argument.lstrip("-").split("=", 1)[0].lower()
        if normalized.startswith("seed-") or normalized == "promote-validation-baseline":
            raise RuntimeError(f"Baseline mutation is forbidden in a qualification profile: {argument}")
        if normalized in ("root", "root-directory"):
            raise RuntimeError("Nuke root overrides would escape the isolated workspace")


def parser_inputs(inventory):
    return [entry for entry in inventory
            if entry[0].startswith("tools/SwiftInterfaceParser/")
            or entry[0] == "build/Build.SwiftInterfaceParser.cs"]


def reusable_parser_cache(workspace, state, previous, identity, inventory):
    # A loose source.json (or an incomplete snapshot) cannot authorize reuse.
    receipt_path = state / "source.json"
    if (previous.get("incomplete") or previous.get("toolchain") != identity["toolchain"]
            or previous.get("globalJson") != identity["globalJson"]
            or receipt_path.is_symlink() or not receipt_path.is_file()):
        return None
    try:
        receipt = json.loads(receipt_path.read_text())
        files = receipt["files"]
        if (receipt["identity"] != previous or digest(files) != previous["source"]
                or parser_inputs(files) != parser_inputs(inventory)):
            return None
    except (ValueError, KeyError, TypeError, IndexError):
        return None
    cache = workspace
    for part in ("tools", "SwiftInterfaceParser", ".build"):
        cache /= part
        if cache.is_symlink() or not cache.is_dir():
            return None
    return cache


def prepare_workspace(source, state, identity, inventory, arguments):
    workspace = state / "workspace"
    marker = workspace / MARKER
    if workspace.is_symlink():
        raise RuntimeError(f"Refusing a symlink workspace: {workspace}")
    if marker.is_symlink():
        raise RuntimeError(f"Refusing a symlink workspace marker: {marker}")
    previous = json.loads(marker.read_text()) if marker.is_file() else None
    if previous == identity:
        return workspace
    if any(arg.split("=", 1)[0] in ("--skip-regen", "--skip-build", "--quick") for arg in arguments):
        raise RuntimeError("Cannot reuse artifacts: source/toolchain receipt changed or is missing. Run without reuse flags.")
    if workspace.exists() and (not previous or previous.get("owner") != str(source)
                               or previous.get("schema") != SCHEMA):
        raise RuntimeError(f"Refusing to replace an unowned workspace: {workspace}")
    cache = reusable_parser_cache(workspace, state, previous, identity, inventory) if previous else None
    # This invocation alone owns the holder. A crash may leave it behind, but no future
    # invocation trusts or removes it. Only .build survives; staging/managed outputs do not.
    with tempfile.TemporaryDirectory(prefix="parser-cache-", dir=state) as holding:
        saved = Path(holding) / ".build"
        if cache:
            cache.rename(saved)
        if workspace.exists():
            shutil.rmtree(workspace)
        subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "clone", "--quiet", "--shared",
                        "--no-checkout", "--", str(source), str(workspace)], check=True)
        subprocess.run(["git", "-C", str(workspace), "-c", "core.hooksPath=/dev/null",
                        "checkout", "--quiet", "--detach", identity["head"]], check=True)
        write_json(marker, {"owner": str(source), "schema": SCHEMA, "incomplete": True})
        with (workspace / ".git/info/exclude").open("a") as exclusions:
            exclusions.write("\n/" + MARKER + "\n")
        wanted = {entry[0] for entry in inventory}
        # Remove tracked deletions before overlaying the uncommitted working tree.
        tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=workspace)
        for name in (os.fsdecode(x) for x in tracked.split(b"\0") if x):
            path = workspace / name
            if name not in wanted and (path.exists() or path.is_symlink()):
                path.unlink()
        for name, kind, _ in inventory:
            origin, target = source / name, workspace / name
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.is_symlink() or target.exists():
                target.unlink()
            if kind == "symlink":
                target.symlink_to(os.readlink(origin))
            else:
                shutil.copy2(origin, target)
        if source_inventory(source) != inventory:
            # Mark ownership so the next invocation can replace the incomplete snapshot safely.
            write_json(marker, {"owner": str(source), "schema": SCHEMA, "incomplete": True})
            raise RuntimeError("Source changed while preparing the snapshot; retry after edits finish.")
        write_json(workspace / "global.json", identity["globalJson"])
        # Give git-based Nuke source discovery a real index, including new source files.
        subprocess.run(["git", "-C", str(workspace), "add", "--all", "--", "."], check=True)
        if saved.exists():
            destination = workspace / "tools/SwiftInterfaceParser/.build"
            # Source can contain symlinks; never restore through one or replace a source file.
            parent = workspace
            for part in ("tools", "SwiftInterfaceParser"):
                parent /= part
                if parent.is_symlink() or not parent.is_dir():
                    raise RuntimeError(f"Refusing an unsafe parser cache destination: {parent}")
            if destination.exists() or destination.is_symlink():
                raise RuntimeError(f"Refusing to replace a parser cache destination: {destination}")
            saved.rename(destination)
        write_json(marker, identity)
    return workspace


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True, choices=("net10-xcode26", "net10-xcode27", "net11-xcode27"))
    parser.add_argument("--dotnet-root")
    parser.add_argument("--developer-dir")
    parser.add_argument("--state-root", type=Path)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("arguments", nargs=argparse.REMAINDER, help="Nuke target and options after --")
    options = parser.parse_args(argv)
    if sys.platform != "darwin":
        parser.error("Apple toolchain qualification requires macOS")
    source = Path(__file__).resolve().parents[2]
    profile = json.loads((source / "build/toolchains" / (options.profile + ".json")).read_text())
    if options.dotnet_root:
        profile["dotnetRoot"] = options.dotnet_root
    if options.developer_dir:
        profile["developerDirectory"] = options.developer_dir
    arguments = options.arguments
    if arguments[:1] == ["--"]:
        arguments = arguments[1:]
    if not arguments and not options.prepare_only:
        parser.error("Specify a Nuke target after -- (or use --prepare-only)")
    validate_arguments(arguments)
    state = (options.state_root or source / "artifacts/toolchains").expanduser().resolve() / options.profile
    if state.is_relative_to(source):
        ignored = subprocess.run(["git", "check-ignore", "--quiet", str(state)], cwd=source)
        if ignored.returncode != 0:
            raise RuntimeError("A state directory inside the source checkout must be Git-ignored")
    state.mkdir(parents=True, exist_ok=True)
    with profile_lock(state):
        for name in ("cli-home", "http-cache", "logs"):
            (state / name).mkdir(exist_ok=True)
        env = environment(profile, state)
        prepare_temporary_directory(Path(env["TMPDIR"]), state, profile)
        Path(env["NUGET_PACKAGES"]).mkdir(exist_ok=True)
        receipt = toolchain_receipt(profile, state, env)
        inventory = source_inventory(source)
        identity = {"schema": SCHEMA, "owner": str(source), "profile": options.profile,
                    "head": execute(["git", "rev-parse", "HEAD"], cwd=source),
                    "source": digest(inventory), "toolchain": digest(receipt),
                    "globalJson": global_json(profile)}
        workspace = prepare_workspace(source, state, identity, inventory, arguments)
        write_json(state / "toolchain.json", receipt)
        write_json(state / "source.json", {"identity": identity, "files": inventory})
        env.update({"SWIFT_BINDINGS_TOOLCHAIN_PROFILE": options.profile,
                    "SWIFT_BINDINGS_TOOLCHAIN_FINGERPRINT": identity["toolchain"],
                    "SWIFT_BINDINGS_SOURCE_FINGERPRINT": identity["source"]})
        print(f"Profile {options.profile}: SDK {receipt['sdk']}; {receipt['xcode'].replace(chr(10), ', ')}", flush=True)
        print(f"Isolated workspace: {workspace}", flush=True)
        if options.prepare_only:
            return 0
        subprocess.run([env["DOTNET_HOST_PATH"], "tool", "restore"], cwd=workspace, env=env, check=True)
        return subprocess.call([env["DOTNET_HOST_PATH"], "nuke", *arguments], cwd=workspace, env=env)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, subprocess.CalledProcessError, OSError, ValueError) as error:
        print(f"Toolchain profile error: {error}", file=sys.stderr)
        sys.exit(1)
