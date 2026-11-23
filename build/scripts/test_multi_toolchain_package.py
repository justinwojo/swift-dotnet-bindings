#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Tests of the multi-toolchain package gate's readers and package doctoring (no toolchain needed)."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("mtp", Path(__file__).with_name("multi-toolchain-package.py"))
mtp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mtp)

NUSPEC = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Acme.Kit</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework="net10.0-ios26.0">
        <dependency id="SwiftBindings.Runtime" version="[0.21.0, 0.22.0)" exclude="Build,Analyzers" />
      </group>
      <group targetFramework="net10.0-ios27.2" />
    </dependencies>
  </metadata>
</package>
"""


def package(directory, name, entries):
    path = Path(directory) / name
    with zipfile.ZipFile(path, "w") as z:
        for entry, data in entries.items():
            z.writestr(entry, data)
    return path


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def kit(self, version="1.0.0"):
        return package(self.dir, f"Acme.Kit.{version}.nupkg", {
            "Acme.Kit.nuspec": NUSPEC.replace("<version>1.0.0</version>", f"<version>{version}</version>"),
            "lib/net10.0-ios26.0/Kit.dll": b"old", "lib/net10.0-ios27.2/Kit.dll": b"new",
            "buildTransitive/net10.0-ios26.0/Acme.Kit.props": b"p26", "buildTransitive/net10.0-ios27.2/Acme.Kit.props": b"p27",
            "native/net10.0-ios26.0/Kit.xcframework/Info.plist": b"x26", "native/net10.0-ios27.2/Kit.xcframework/Info.plist": b"x27",
            ".signature.p7s": b"sig"})

    def test_copy_tfms_reads_each_layout_root(self):
        roots = mtp.copy_tfms(mtp.read_entries(self.kit()))
        self.assertEqual(roots, {r: {"net10.0-ios26.0", "net10.0-ios27.2"} for r in ("lib", "buildTransitive", "native")})

    def test_copy_digest_holds_only_that_copy(self):
        digest = mtp.copy_digest(mtp.read_entries(self.kit()), "net10.0-ios27.2")
        self.assertEqual(sorted(digest), ["buildTransitive/net10.0-ios27.2/Acme.Kit.props", "lib/net10.0-ios27.2/Kit.dll",
                                          "native/net10.0-ios27.2/Kit.xcframework/Info.plist"])
        self.assertEqual(digest["lib/net10.0-ios27.2/Kit.dll"], mtp.sha256(b"new"))

    def test_nuspec_groups_include_empty_groups(self):
        groups = mtp.nuspec_groups(mtp.read_entries(self.kit()))
        self.assertEqual(groups, {"net10.0-ios26.0": [(("exclude", "Build,Analyzers"), ("id", "SwiftBindings.Runtime"),
                                                       ("version", "[0.21.0, 0.22.0)"))], "net10.0-ios27.2": []})

    def test_describe_groups_names_each_copys_dependencies(self):
        groups = mtp.nuspec_groups(mtp.read_entries(self.kit()))
        self.assertEqual(mtp.describe_groups(groups, ["net10.0-ios26.0", "net10.0-ios27.2"]),
                         "net10.0-ios26.0: SwiftBindings.Runtime [0.21.0, 0.22.0); net10.0-ios27.2: ")

    def test_nuspec_groups_see_every_dependency_attribute_and_refuse_a_duplicate_group(self):
        dropped = NUSPEC.replace(' exclude="Build,Analyzers"', "")
        self.assertNotEqual(mtp.nuspec_groups({"a.nuspec": dropped.encode()}), mtp.nuspec_groups({"a.nuspec": NUSPEC.encode()}))
        doubled = NUSPEC.replace('<group targetFramework="net10.0-ios27.2" />',
                                 '<group targetFramework="net10.0-ios27.2" /><group targetFramework="net10.0-ios27.2" />')
        with self.assertRaises(ValueError):
            mtp.nuspec_groups({"a.nuspec": doubled.encode()})

    def test_variant_drops_reversions_and_unsigns(self):
        out = mtp.variant(self.kit(), self.dir / "out", "1.0.0-negb", drops=["buildTransitive/net10.0-ios27.2/"])
        self.assertEqual(out.name, "Acme.Kit.1.0.0-negb.nupkg")
        entries = mtp.read_entries(out)
        self.assertNotIn("buildTransitive/net10.0-ios27.2/Acme.Kit.props", entries)
        self.assertIn("buildTransitive/net10.0-ios26.0/Acme.Kit.props", entries)
        self.assertNotIn(".signature.p7s", entries)
        self.assertIn("<version>1.0.0-negb</version>", entries["Acme.Kit.nuspec"].decode())

    def test_variant_refuses_a_drop_that_matches_nothing(self):
        # A control whose doctoring silently did nothing would pass for the wrong reason.
        with self.assertRaises(ValueError):
            mtp.variant(self.kit(), self.dir / "out", "1.0.0-x", drops=["buildTransitive/net11.0-ios27.2/"])

    def test_variant_copies_one_copys_natives_over_another(self):
        out = mtp.variant(self.kit(), self.dir / "out", "1.0.0-negc", copies=[(None, "native/net10.0-ios26.0/", "native/net10.0-ios27.2/")])
        entries = mtp.read_entries(out)
        self.assertEqual(entries["native/net10.0-ios27.2/Kit.xcframework/Info.plist"], b"x26")
        self.assertEqual(entries["native/net10.0-ios26.0/Kit.xcframework/Info.plist"], b"x26")

    def test_variant_copies_from_another_package(self):
        other = package(self.dir, "Other.nupkg", {"lib/net10.0-ios27.2/Kit.dll": b"other"})
        out = mtp.variant(self.kit(), self.dir / "out", "1.0.0", copies=[(other, "lib/net10.0-ios27.2/", "lib/net10.0-ios26.0/")])
        self.assertEqual(mtp.read_entries(out)["lib/net10.0-ios26.0/Kit.dll"], b"other")

    def test_wrapper_identities_reads_every_slice(self):
        one = b"schema=1;module=Kit;library=KitSwiftBindings;copy=net10.0-ios27.2;sdk=27.2;generator=1.0.0;source=sha256:ab"
        image = self.dir / "Kit"
        image.write_bytes(b"\xcf\xfa\xed\xfe junk " + one + b"\0 more " + one + b"\0 schema=1;module=Other;copy=x\0")
        found = mtp.wrapper_identities(image, "Kit")
        self.assertEqual(found, [one.decode()] * 2)
        self.assertEqual(mtp.identity_field(found[0], "copy"), "net10.0-ios27.2")
        self.assertEqual(mtp.identity_field(found[0], "sdk"), "27.2")
        self.assertIsNone(mtp.identity_field(found[0], "missing"))

    def test_assets_paths_match_the_package_case_insensitively(self):
        assets = self.dir / "project.assets.json"
        assets.write_text(json.dumps({"targets": {"net10.0-ios27.2/iossimulator-arm64": {
            "acme.kit/1.0.0": {"compile": {"lib/net10.0-ios27.2/Kit.dll": {}}, "build": {"buildTransitive/net10.0-ios27.2/Acme.Kit.props": {}}},
            "Acme.KitExtra/1.0.0": {"compile": {"lib/x/Extra.dll": {}}}}}}))
        self.assertEqual(mtp.assets_paths(assets, "Acme.Kit"), {"net10.0-ios27.2/iossimulator-arm64": (
            ["lib/net10.0-ios27.2/Kit.dll"], ["buildTransitive/net10.0-ios27.2/Acme.Kit.props"])})

    def test_windows_budget_matches_the_merge(self):
        # 259 - (9 + 40-character profile + 17 + len("<id>.<version>") + 1): ProximityReader at
        # SwiftBindings.Apple.ProximityReader 27.2.0 leaves 150 (multi-toolchain-packages.md).
        self.assertEqual(mtp.windows_budget("SwiftBindings.Apple.ProximityReader", "27.2.0"), 150)

    def test_copy_lines_keep_the_build_order(self):
        text = ("  SwiftBindings copy: Acme.Kit 1.0.0 -> net10.0-ios26.0 (built on .NET 10.0.107 + Xcode 26.3); lib=a\n"
                "  SwiftBindings copy: Acme.KitExtra 1.0.0 -> net10.0-ios27.2 (built on x); lib=b\n"
                "  SwiftBindings copy: Acme.Kit 1.0.0 -> net10.0-ios27.2 (built on .NET 10.0.404 + Xcode 27.2); lib=c\n")
        self.assertEqual(mtp.copy_lines(text, "Acme.Kit"), [("net10.0-ios26.0", ".NET 10.0.107 + Xcode 26.3"),
                                                            ("net10.0-ios27.2", ".NET 10.0.404 + Xcode 27.2")])

    def test_minor_version_compares_sdks_as_named(self):
        self.assertEqual([mtp.minor_version(v) for v in ("27.2.1", "27.2", "27")], ["27.2", "27.2", "27.0"])
        self.assertNotEqual(mtp.minor_version("27.0"), mtp.minor_version("27.2"))

    def test_control_needs_a_completed_failure_with_one_matching_diagnostic(self):
        gate = object.__new__(mtp.Gate)
        gate.record, gate.lines, gate.failures = {"steps": []}, [], []
        expect = [r"error SWIFTBIND133", r"lib/net10\.0-ios27\.2/\S+ would be refused", r"copy net10\.0-ios26\.0"]
        one = "x.nupkg : error SWIFTBIND133: x: lib/net10.0-ios27.2/X.dll would be refused: generated as copy net10.0-ios26.0\n"
        split = ("x.nupkg : error SWIFTBIND133: x: lib/net10.0-ios27.2/X.dll would be refused: no manifest\n"
                 "x.nupkg : error SWIFTBIND133: x: lib/net10.0-maccatalyst27.2/X.dll: generated as copy net10.0-ios26.0\n")
        cases = [(1, one, True), (-1, one, False), (0, one, False), (1, split, False)]
        for code, text, red in cases:
            gate.failures.clear()
            with self.subTest(code=code, red=red):
                gate.control("c", code, text, expect)
                self.assertEqual(not gate.failures, red)

    def test_failed_lines_are_the_errors(self):
        text = "noise\nfoo.csproj : error NU1202: no\nwarning SWIFTBIND140: long\nfoo.csproj : error NU1202: no\n"
        self.assertEqual(mtp.first_errors(text), "foo.csproj : error NU1202: no | warning SWIFTBIND140: long")


if __name__ == "__main__":
    unittest.main()
