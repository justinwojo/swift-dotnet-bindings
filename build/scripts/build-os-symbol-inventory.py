#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Builds the OS symbol inventory the generator ships (load-safety property P2).

The inventory records which Swift symbols each supported OS release ships, read from the
text-based stubs (.tbd) of one Xcode's SDKs: every Swift symbol (`$s…`) a public framework,
sub-framework, Mac Catalyst iOSSupport framework or `/usr/lib/swift` library exports or
re-exports, per platform variant (target triple suffix). The generator uses it to find a
member an SDK declares without a floor that the inventory's OS does not ship, and floors that
member at the generating SDK's version so the binding refuses the call before native code
touches the missing symbol.

Format (gzip):
  magic  b"SBOSINV1"
  u32le  header length, then that many bytes of UTF-8 JSON (provenance, platform bits, groups)
  for each header group, in order: `count` hashes as unsigned LEB128 deltas from the previous
  hash of the same group (the first from 0), ascending.
A hash is the low 40 bits of FNV-1a-64 over the UTF-8 symbol without its leading underscore.
Each group lists the hashes present on exactly the platforms set in its `mask`.

Usage:
  build-os-symbol-inventory.py --xcode /Applications/Xcode-26.3.0.app -o <out.bin>
"""

import argparse
import gzip
import json
import os
import plistlib
import re
import struct
import sys

HASH_BITS = 40
HASH_MASK = (1 << HASH_BITS) - 1
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3

# Platform variant (the .tbd target suffix) -> (bit, SDK platform directory)
PLATFORMS = [
    ("ios", 0, "iPhoneOS"),
    ("ios-simulator", 1, "iPhoneSimulator"),
    ("tvos", 2, "AppleTVOS"),
    ("tvos-simulator", 3, "AppleTVSimulator"),
    ("macos", 4, "MacOSX"),
    ("maccatalyst", 5, "MacOSX"),
]

# Directories (relative to the SDK root) whose .tbd files are linkable OS providers.
PROVIDER_ROOTS = [
    "System/Library/Frameworks",
    "System/Library/SubFrameworks",
    "System/iOSSupport/System/Library/Frameworks",
    "System/iOSSupport/System/Library/SubFrameworks",
    "usr/lib/swift",
    "System/iOSSupport/usr/lib/swift",
]

SYMBOL_RE = re.compile(r"'?_(\$s[^'\s,\]]+)'?")


def fnv1a40(symbol: str) -> int:
    h = FNV_OFFSET
    for b in symbol.encode("utf-8"):
        h ^= b
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h & HASH_MASK


def flow_list(text: str, start: int):
    """Returns (content, end) of the YAML flow sequence opening at text[start] == '['."""
    depth = 0
    i = start
    while i < len(text):
        c = text[i]
        if c == "[":
            depth += 1
        elif c == "]":
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i + 1
        elif c == "'":
            j = text.index("'", i + 1)
            i = j
        i += 1
    raise ValueError("unterminated flow sequence")


def parse_tbd(path: str):
    """Yields (install_name, variant, symbol) for every Swift symbol a tbd exports or re-exports."""
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    for doc in re.split(r"^--- !tapi-tbd[^\n]*\n", text, flags=re.M):
        if not doc.strip():
            continue
        m = re.search(r"^install-name:\s*'?([^'\n]+)'?\s*$", doc, flags=re.M)
        install_name = m.group(1).strip() if m else os.path.basename(path)
        # Top-level sections start in column 0. Only exports and reexports are what the library
        # provides; undefineds and allowable-clients are not.
        sections = list(re.finditer(r"^([a-z-]+):", doc, flags=re.M))
        for idx, sec in enumerate(sections):
            if sec.group(1) not in ("exports", "reexports"):
                continue
            end = sections[idx + 1].start() if idx + 1 < len(sections) else len(doc)
            body = doc[sec.end():end]
            for item in re.split(r"^\s*-\s+targets:", body, flags=re.M)[1:]:
                lb = item.index("[")
                targets_text, pos = flow_list(item, lb)
                variants = set()
                for t in targets_text.split(","):
                    t = t.strip()
                    if "-" in t:
                        variants.add(t.split("-", 1)[1])
                for key in re.finditer(r"^\s*(symbols|weak-symbols|thread-local-symbols):\s*", item[pos:], flags=re.M):
                    lst_start = pos + key.end()
                    if lst_start < len(item) and item[lst_start] == "[":
                        content, _ = flow_list(item, lst_start)
                        for sm in SYMBOL_RE.finditer(content):
                            for v in variants:
                                yield install_name, v, sm.group(1)


def sdk_info(sdk: str):
    with open(os.path.join(sdk, "System/Library/CoreServices/SystemVersion.plist"), "rb") as f:
        sv = plistlib.load(f)
    with open(os.path.join(sdk, "SDKSettings.json")) as f:
        settings = json.load(f)
    return {
        "sdk": settings.get("CanonicalName"),
        "sdkVersion": settings.get("Version"),
        "sdkBuild": sv.get("ProductBuildVersion"),
        "osVersion": sv.get("ProductVersion"),
    }


def leb128(n: int) -> bytes:
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--xcode", required=True, help="Xcode.app whose SDKs to read")
    ap.add_argument("-o", "--output", required=True)
    args = ap.parse_args()

    xcode = args.xcode.rstrip("/")
    with open(os.path.join(xcode, "Contents/Info.plist"), "rb") as f:
        xinfo = plistlib.load(f)
    with open(os.path.join(xcode, "Contents/version.plist"), "rb") as f:
        xver = plistlib.load(f)

    masks = {}
    platforms_meta = []
    scanned = {}
    for variant, bit, platform_dir in PLATFORMS:
        sdk = os.path.join(xcode, "Contents/Developer/Platforms", f"{platform_dir}.platform/Developer/SDKs/{platform_dir}.sdk")
        sdk = os.path.realpath(sdk)
        if sdk not in scanned:
            per_variant = {}
            providers = {}
            for root in PROVIDER_ROOTS:
                base = os.path.join(sdk, root)
                if not os.path.isdir(base):
                    continue
                for dirpath, dirnames, filenames in os.walk(base):
                    # Versions/Current duplicates Versions/A on macOS; tbds sit at the bundle root.
                    dirnames[:] = [d for d in dirnames if d != "Versions"]
                    for fn in filenames:
                        if not fn.endswith(".tbd"):
                            continue
                        for install_name, v, sym in parse_tbd(os.path.join(dirpath, fn)):
                            per_variant.setdefault(v, set()).add(fnv1a40(sym))
                            providers.setdefault(v, set()).add(install_name)
            scanned[sdk] = (per_variant, providers)
        per_variant, providers = scanned[sdk]
        hashes = per_variant.get(variant, set())
        if not hashes:
            sys.exit(f"no Swift symbols found for {variant} in {sdk}")
        for h in hashes:
            masks[h] = masks.get(h, 0) | (1 << bit)
        info = sdk_info(sdk)
        platforms_meta.append({
            "variant": variant,
            "bit": bit,
            **info,
            "symbolCount": len(hashes),
            "providerCount": len(providers.get(variant, ())),
        })

    groups = {}
    for h, m in masks.items():
        groups.setdefault(m, []).append(h)
    ordered = sorted(groups.items())
    header = {
        "format": "swift-os-symbol-inventory/1",
        "xcode": {"version": xinfo.get("CFBundleShortVersionString"), "build": xver.get("ProductBuildVersion")},
        "hash": "fnv1a64-low40",
        "symbolForm": "Swift mangled name without the C underscore ($s...)",
        "providers": "exports and reexports of " + ", ".join(PROVIDER_ROOTS),
        "platforms": platforms_meta,
        "groups": [{"mask": m, "count": len(hs)} for m, hs in ordered],
    }
    body = bytearray()
    for _, hs in ordered:
        prev = 0
        for h in sorted(hs):
            body += leb128(h - prev)
            prev = h
    hj = json.dumps(header, separators=(",", ":"), sort_keys=True).encode("utf-8")
    raw = b"SBOSINV1" + struct.pack("<I", len(hj)) + hj + bytes(body)
    with gzip.GzipFile(args.output, "wb", compresslevel=9, mtime=0) as f:
        f.write(raw)
    print(json.dumps({**header, "groups": len(ordered), "union": len(masks), "bytes": os.path.getsize(args.output)}, indent=1))


if __name__ == "__main__":
    main()
