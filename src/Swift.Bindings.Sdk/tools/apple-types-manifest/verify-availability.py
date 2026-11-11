#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Check every platform column of the Apple types manifest against the Swift compiler.

The manifest builder reads availability from swift-api-digester dumps, and a dump cannot
state everything Swift knows:

* The digester writes no ``intro_macCatalyst`` key. The builder therefore floors Mac
  Catalyst at the iOS record, which is wrong for a type Apple introduces later on Mac
  Catalyst (``@available(iOS 15, macCatalyst 16, *)``).
* A type unavailable on a platform is left out of that platform's dump, so a null
  column is inferred from an absence rather than read from a record.

swiftc applies Apple's real ``@available`` records, so each column is checked with it:

* A version ``v`` must type-check a reference to the type at the deployment target
  ``max(v, supplement minimum)``. A floor below the supplement's own minimum is never
  enforced, so it is not tested below that.
* A null column must fail to type-check at the platform's current SDK version, with a
  diagnostic that the type is unavailable, too new, or missing from the platform.

Exit status is 0 when every column agrees with swiftc, 1 when one disagrees, and 2 on a
usage or toolchain error. Uses the Xcode that ``xcrun`` resolves (``DEVELOPER_DIR``).
"""

from __future__ import annotations

import argparse
import itertools
import json
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Platform:
    column: str
    sdk: str
    # Target triple with a {version} placeholder.
    triple: str
    # SDK whose version names this platform's current OS version. Mac Catalyst numbers
    # its releases like iOS, so it reads the iOS SDK's version.
    version_sdk: str
    # The SwiftBindings.Apple supplement's minimum OS version on this platform; keep in
    # step with SupportedOSPlatformVersion in src/Swift.Bindings.Apple/Swift.Bindings.Apple.csproj.
    supplement_minimum: str


PLATFORMS = [
    Platform("ios", "iphonesimulator", "arm64-apple-ios{version}-simulator", "iphonesimulator", "15.0"),
    Platform("maccatalyst", "macosx", "arm64-apple-ios{version}-macabi", "iphonesimulator", "15.0"),
    Platform("tvos", "appletvsimulator", "arm64-apple-tvos{version}-simulator", "appletvsimulator", "15.0"),
    Platform("macos", "macosx", "arm64-apple-macos{version}", "macosx", "12.0"),
]

# Diagnostics that mean "this type cannot be used on this platform at this version".
UNAVAILABLE_DIAGNOSTICS = re.compile(
    r"is unavailable in|is only available in|only available in .* or newer|"
    r"no such module|cannot find type|is not a member type")

IDENTITY = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$")


def version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


def normalize(version: str) -> str:
    return version if "." in version else version + ".0"


def xcrun(*args: str) -> str:
    result = subprocess.run(["xcrun", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"xcrun {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout.strip()


_probe_ids = itertools.count()


def typecheck(platform: Platform, sdk_path: str, version: str, source: str, workdir: Path) -> tuple[bool, str]:
    path = workdir / f"probe{next(_probe_ids)}.swift"
    path.write_text(source)
    triple = platform.triple.format(version=version)
    result = subprocess.run(
        ["xcrun", "swiftc", "-typecheck", "-target", triple, "-sdk", sdk_path, str(path)],
        capture_output=True, text=True)
    return result.returncode == 0, result.stderr


def probe_source(identities: list[str]) -> str:
    modules = sorted({identity.split(".", 1)[0] for identity in identities})
    lines = [f"import {module}" for module in modules]
    for index, identity in enumerate(identities):
        lines.append(f"func probe{index}() {{ _ = MemoryLayout<{identity}>.size }}")
    return "\n".join(lines) + "\n"


def first_error(stderr: str) -> str:
    errors = [line.strip() for line in stderr.splitlines() if ": error: " in line]
    return "\n".join(errors) if errors else stderr.strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--skip-dump", action="append", default=[], metavar="MODULE:PLATFORM",
                        help="Leave unchecked the columns a dump that was not produced would have "
                             "filled (regenerate.sh with ALLOW_PARTIAL=1). Repeatable.")
    options = parser.parse_args()
    columns = {platform.column for platform in PLATFORMS}
    skipped: set[tuple[str, str]] = set()
    for value in options.skip_dump:
        module, _, column = value.partition(":")
        if not module or column not in columns:
            print(f"error: --skip-dump expects MODULE:PLATFORM with PLATFORM one of "
                  f"{sorted(columns)}, got '{value}'", file=sys.stderr)
            return 2
        skipped.add((module, column))

    manifest = json.loads(options.manifest.read_text())
    entries = []
    for module in manifest["modules"].values():
        for entry in module["types"]:
            identity = entry["swift_identity"]
            if not IDENTITY.match(identity):
                print(f"error: cannot form a Swift type reference for '{identity}'", file=sys.stderr)
                return 2
            accessor = entry.get("metadata_accessor") or {}
            entries.append((identity, accessor.get("availability") or {}))

    failures: list[str] = []
    checked = 0
    with tempfile.TemporaryDirectory(prefix="apple-types-availability-") as temp:
        workdir = Path(temp)
        for platform in PLATFORMS:
            platform_entries = [(identity, availability) for identity, availability in entries
                                if (identity.split(".", 1)[0], platform.column) not in skipped]
            if len(platform_entries) < len(entries):
                print(f"[verify-availability] Not checking {len(entries) - len(platform_entries)} "
                      f"{platform.column} column(s) whose dump was skipped.", file=sys.stderr)
            if not platform_entries:
                continue
            try:
                sdk_path = xcrun("--sdk", platform.sdk, "--show-sdk-path")
                current = normalize(xcrun("--sdk", platform.version_sdk, "--show-sdk-version"))
            except RuntimeError as error:
                print(f"error: {error}", file=sys.stderr)
                return 2

            # A failing probe proves nothing unless a trivial one passes with the same flags.
            ok, stderr = typecheck(platform, sdk_path, platform.supplement_minimum, "import Foundation\n", workdir)
            if not ok:
                print(f"error: swiftc cannot type-check for {platform.column}:\n{stderr}", file=sys.stderr)
                return 2

            supported: dict[str, list[str]] = {}
            for identity, availability in platform_entries:
                floor = availability.get(platform.column)
                if floor is None:
                    ok, stderr = typecheck(platform, sdk_path, current, probe_source([identity]), workdir)
                    checked += 1
                    if ok:
                        failures.append(
                            f"{identity}: manifest marks {platform.column} unsupported, but it "
                            f"type-checks at {platform.column} {current}")
                    elif not UNAVAILABLE_DIAGNOSTICS.search(stderr):
                        failures.append(
                            f"{identity}: {platform.column} probe failed for a reason other "
                            f"than availability:\n{first_error(stderr)}")
                    continue
                target = max(normalize(floor), platform.supplement_minimum, key=version_key)
                supported.setdefault(target, []).append(identity)

            for target, identities in sorted(supported.items(), key=lambda item: version_key(item[0])):
                ok, stderr = typecheck(platform, sdk_path, target, probe_source(identities), workdir)
                checked += len(identities)
                if ok:
                    continue
                # Re-probe one type at a time so the report names only the types swiftc rejects.
                rejected = []
                for identity in identities:
                    alone_ok, alone_stderr = typecheck(platform, sdk_path, target, probe_source([identity]), workdir)
                    if not alone_ok:
                        rejected.append((identity, alone_stderr))
                for identity, reason in rejected or [(", ".join(identities), stderr)]:
                    failures.append(
                        f"{identity}: manifest supports {platform.column} at {target}, but swiftc "
                        f"rejects the reference there:\n{first_error(reason)}")

    if failures:
        print("[verify-availability] Manifest availability disagrees with swiftc:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1
    print(f"[verify-availability] {checked} type/platform columns agree with swiftc.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
