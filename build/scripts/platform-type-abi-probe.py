#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Measures how the SDK's Swift represents every platform type the generator projects onto a
# managed carrier, and writes the table PlatformTypeAbiAuditTests holds the carriers to.
#
# The projections come from two places: the bundled type databases
# (src/Swift.Runtime/src/Swift/*Database.xml) and the Apple framework registry's typeRemaps and
# valueTypes (src/Swift.Bindings/src/Data/apple-frameworks.json). For each Swift type named there
# the script compiles a small program against the SDK that prints the type's size, alignment, raw
# value type and, for a trivial value, the scalar leaves of its stored representation. A name the
# SDK does not declare under that spelling is recorded as absent, with the compiler's reason.
#
# Usage:
#   python3 build/scripts/platform-type-abi-probe.py [--xcode /Applications/Xcode-26.3.0.app]
#
# Rerun it after adding or changing a projection entry, or when moving to a new SDK, and commit
# the table with the change that needs it.

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
XML_GLOB = os.path.join(REPO, "src", "Swift.Runtime", "src", "Swift", "*Database.xml")
REGISTRY = os.path.join(REPO, "src", "Swift.Bindings", "src", "Data", "apple-frameworks.json")
OUTPUT = os.path.join(REPO, "build", "baselines", "platform-type-abi.json")
TARGET = "arm64-apple-ios26.0-macabi"

# Modules whose values the runtime itself declares carriers for; they are not platform projections.
RUNTIME_MODULES = {"Swift", "SwiftUI", "_Concurrency"}

# Spellings the databases use for a type Swift declares in another module.
SPELLING_FIXUPS = {
    "Foundation.Selector": "ObjectiveC.Selector",
    "Foundation.ObjCBool": "ObjectiveC.ObjCBool",
}

PRELUDE = r'''
import Foundation
import CoreGraphics

func leafName(_ t: Any.Type) -> String? {
    switch t {
    case is Int.Type, is Int64.Type: return "i64"
    case is UInt.Type, is UInt64.Type: return "u64"
    case is Int32.Type: return "i32"
    case is UInt32.Type: return "u32"
    case is Int16.Type: return "i16"
    case is UInt16.Type: return "u16"
    case is Int8.Type: return "i8"
    case is UInt8.Type: return "u8"
    case is Bool.Type: return "bool"
    case is Float.Type: return "f32"
    case is Double.Type, is CGFloat.Type: return "f64"
    case is OpaquePointer.Type, is UnsafeRawPointer.Type, is UnsafeMutableRawPointer.Type: return "ptr"
    default: return nil
    }
}

func vectorBits<V: SIMD>(_ v: V) -> Int { MemoryLayout<V>.size * 8 }

func flatten(_ v: Any, into out: inout [String]) {
    let t = type(of: v)
    if let l = leafName(t) { out.append(l); return }
    // A SIMD vector is one vector register's worth of lanes, not a struct of scalars.
    if let vector = v as? any SIMD { out.append("vec\(vectorBits(vector))"); return }
    let m = Mirror(reflecting: v)
    if m.children.isEmpty { out.append("?\(t)"); return }
    for c in m.children { flatten(c.value, into: &out) }
}

func emit(_ name: String, _ t: Any.Type, size: Int, align: Int, pod: Bool, raw: String?, leaves: [String]?) {
    var d: [String: Any] = ["swift": name, "size": size, "align": align, "pod": pod, "kind": t is AnyClass ? "class" : "value"]
    if let raw { d["rawValue"] = raw }
    if let leaves { d["leaves"] = leaves }
    let data = try! JSONSerialization.data(withJSONObject: d, options: [.sortedKeys])
    print(String(data: data, encoding: .utf8)!)
}

func podLeaves<T>(_ t: T.Type) -> [String]? {
    guard _isPOD(T.self), !(T.self is AnyClass) else { return nil }
    let bytes = max(MemoryLayout<T>.size, 1)
    let p = UnsafeMutableRawPointer.allocate(byteCount: bytes, alignment: MemoryLayout<T>.alignment)
    defer { p.deallocate() }
    p.initializeMemory(as: UInt8.self, repeating: 0, count: bytes)
    if let l = leafName(T.self) { return [l] }
    if T.self is any SIMD.Type { return ["vec\(MemoryLayout<T>.size * 8)"] }
    var out: [String] = []
    flatten(p.load(as: T.self), into: &out)
    return out
}

func probe<T: RawRepresentable>(_ name: String, _ t: T.Type) {
    emit(name, t, size: MemoryLayout<T>.size, align: MemoryLayout<T>.alignment, pod: _isPOD(T.self), raw: "\(T.RawValue.self)", leaves: podLeaves(t))
}
func probe<T>(_ name: String, _ t: T.Type) {
    emit(name, t, size: MemoryLayout<T>.size, align: MemoryLayout<T>.alignment, pod: _isPOD(T.self), raw: nil, leaves: podLeaves(t))
}
'''

SCALARS = {"Int": "i64", "UInt": "u64", "Int64": "i64", "UInt64": "u64", "Int32": "i32", "UInt32": "u32",
           "Int16": "i16", "UInt16": "u16", "Int8": "i8", "UInt8": "u8", "Float": "f32", "Double": "f64",
           "CGFloat": "f64", "Bool": "bool"}


def projection_entries():
    """Every Swift type name the databases or the registry project onto a managed carrier."""
    names = {}
    for path in sorted(glob.glob(XML_GLOB)):
        for entity in ET.parse(path).getroot().iter("entity"):
            decl = entity.find("typedeclaration")
            if decl is None or decl.get("module") in RUNTIME_MODULES:
                continue
            names.setdefault(f"{decl.get('module')}.{decl.get('name')}", os.path.basename(path))
    with open(REGISTRY) as f:
        registry = json.load(f)
    for framework in registry["frameworks"]:
        module = framework["module"]
        for swift_name in framework.get("typeRemaps", {}):
            names.setdefault(f"{module}.{swift_name}", "apple-frameworks.json typeRemaps")
        for entry in framework.get("valueTypes", []):
            swift_name = entry["name"] if isinstance(entry, dict) else entry
            names.setdefault(f"{module}.{swift_name}", "apple-frameworks.json valueTypes")
    return names


def swift_expression(name):
    expr = SPELLING_FIXUPS.get(name, name)
    if expr.startswith("_LocationEssentials."):
        expr = "CoreLocation." + expr.split(".", 1)[1]
    return expr


def leaves_of(row):
    """The stored representation as scalar leaves, or an opaque size when it is not plain data."""
    if row["kind"] == "class":
        return ["ptr"]
    if row.get("rawValue") == "String" and row["size"] == 8:
        return ["ptr"]  # an NSString-backed newtype stores the object reference
    leaves = row.get("leaves")
    if leaves and not any(leaf.startswith("?") for leaf in leaves):
        return leaves
    if row.get("rawValue") in SCALARS and row["size"] == {"i64": 8, "u64": 8, "i32": 4, "u32": 4, "i16": 2, "u16": 2,
                                                           "i8": 1, "u8": 1, "f32": 4, "f64": 8, "bool": 1}[SCALARS[row["rawValue"]]]:
        return [SCALARS[row["rawValue"]]]
    if leaves and len(leaves) == 1 and "Selector" in leaves[0] and row["size"] == 8:
        return ["ptr"]
    if row["pod"] and leaves and len(leaves) == 1 and row["size"] in (1, 2, 4, 8) and row["align"] == row["size"]:
        return [f"int{row['size'] * 8}"]  # a trivial value with no reflected fields: one integer of its size
    return [f"opaque{row['size']}"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xcode", default="/Applications/Xcode-26.3.0.app")
    parser.add_argument("--output", default=OUTPUT)
    args = parser.parse_args()

    env = dict(os.environ, DEVELOPER_DIR=os.path.join(args.xcode, "Contents", "Developer"))
    sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], env=env, text=True).strip()
    sdk_version = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-version"], env=env, text=True).strip()
    support = os.path.join(sdk, "System", "iOSSupport")
    flags = ["-Fsystem", os.path.join(support, "System", "Library", "Frameworks"),
             "-I", os.path.join(support, "usr", "lib", "swift"), "-L", os.path.join(support, "usr", "lib", "swift")]

    entries = projection_entries()
    absent = {}
    with tempfile.TemporaryDirectory() as work:
        source = os.path.join(work, "main.swift")
        binary = os.path.join(work, "probe")
        while True:
            names = [n for n in sorted(entries) if n not in absent]
            modules = sorted({swift_expression(n).split(".")[0] for n in names} - {"Foundation", "CoreGraphics"})
            lines = [f"import {m}" for m in modules] + [f'probe("{n}", {swift_expression(n)}.self)' for n in names]
            prelude_lines = PRELUDE.count("\n")
            with open(source, "w") as f:
                f.write(PRELUDE + "\n".join(lines) + "\n")
            result = subprocess.run(["xcrun", "swiftc", "-target", TARGET, "-sdk", sdk, *flags, "-o", binary, source],
                                    env=env, capture_output=True, text=True)
            if result.returncode == 0:
                break
            failed = {}
            for line, message in re.findall(r"^.*main\.swift:(\d+):\d+: error: (.*)$", result.stderr, re.M):
                index = int(line) - prelude_lines - 1
                if not (0 <= index < len(lines)) or not lines[index].startswith("probe("):
                    sys.exit(f"probe source failed to compile outside a probe line:\n{result.stderr[-4000:]}")
                failed.setdefault(re.search(r'probe\("([^"]+)"', lines[index]).group(1), message)
            if not failed:
                sys.exit(f"probe failed without a located error:\n{result.stderr[-4000:]}")
            absent.update(failed)
        output = subprocess.check_output([binary], text=True)

    rows = {}
    for line in output.splitlines():
        row = json.loads(line)
        rows[row["swift"]] = row
    types = {}
    for name, source_name in sorted(entries.items()):
        if name in absent:
            types[name] = {"source": source_name, "unprobed": absent[name]}
            continue
        row = rows[name]
        record = {"source": source_name, "kind": row["kind"], "size": row["size"], "align": row["align"],
                  "leaves": leaves_of(row)}
        if "rawValue" in row:
            record["rawValue"] = row["rawValue"]
        types[name] = record
    with open(args.output, "w") as f:
        json.dump({"target": TARGET, "sdk": f"macosx{sdk_version}",
                   "generatedBy": "build/scripts/platform-type-abi-probe.py", "types": types},
                  f, indent=2, sort_keys=False)
        f.write("\n")
    print(f"{len(types)} projections, {len(absent)} the SDK does not name under that spelling -> {os.path.relpath(args.output, REPO)}")


if __name__ == "__main__":
    main()
