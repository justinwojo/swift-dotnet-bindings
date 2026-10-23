# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("run_toolchain", Path(__file__).with_name("run-toolchain.py"))
profile = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profile)


class ToolchainIsolationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.source = self.root / "source"
        self.source.mkdir()
        self.state = self.root / "state"
        self.state.mkdir()
        self.git("init", "--quiet")
        (self.source / ".gitignore").write_text("bin/\nartifacts/\n.build/\n")
        (self.source / "global.json").write_text('{"sdk":{"version":"10.0.100"}}\n')
        (self.source / "original.txt").write_text("original\n")
        (self.source / "deleted.txt").write_text("delete me\n")
        (self.source / "tools/SwiftInterfaceParser/Sources").mkdir(parents=True)
        (self.source / "tools/SwiftInterfaceParser/Package.swift").write_text("package inputs")
        (self.source / "tools/SwiftInterfaceParser/Sources/Parser.swift").write_text("parser inputs")
        (self.source / "build").mkdir()
        (self.source / "build/Build.SwiftInterfaceParser.cs").write_text("parser recipe")
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                 "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "fixture")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.source, text=True).strip()

    def identity(self):
        inventory = profile.source_inventory(self.source)
        return {"schema": profile.SCHEMA, "owner": str(self.source), "head": self.git("rev-parse", "HEAD"),
                "source": profile.digest(inventory), "toolchain": "a" * 64,
                "globalJson": {"sdk": {"version": "10.0.402"}}}, inventory

    def prepare(self, args=()):
        identity, inventory = self.identity()
        workspace = profile.prepare_workspace(self.source, self.state, identity, inventory, args)
        profile.write_json(self.state / "source.json", {"identity": identity, "files": inventory})
        return workspace

    def seed_parser_cache(self, workspace):
        cache = workspace / "tools/SwiftInterfaceParser/.build"
        cache.mkdir()
        (cache / "swift-syntax.o").write_text("compiled dependency")
        return cache

    def test_snapshot_contains_current_source_and_git_context_without_old_outputs(self):
        (self.source / "original.txt").write_text("uncommitted\n")
        (self.source / "new.txt").write_text("untracked\n")
        (self.source / "deleted.txt").unlink()
        (self.source / "bin").mkdir()
        (self.source / "bin/old.dll").write_text("stale product")
        before = self.git("status", "--porcelain")
        workspace = self.prepare()
        self.assertEqual((workspace / "original.txt").read_text(), "uncommitted\n")
        self.assertEqual((workspace / "new.txt").read_text(), "untracked\n")
        self.assertFalse((workspace / "deleted.txt").exists())
        self.assertFalse((workspace / "bin").exists())
        tracked = subprocess.check_output(["git", "ls-files"], cwd=workspace, text=True)
        self.assertIn("new.txt", tracked)
        self.assertEqual(self.git("status", "--porcelain"), before)
        self.assertEqual(json.loads((self.source / "global.json").read_text())["sdk"]["version"], "10.0.100")
        self.assertEqual(json.loads((workspace / "global.json").read_text())["sdk"]["version"], "10.0.402")
        (workspace / "original.txt").write_text("snapshot-only write")
        self.assertEqual((self.source / "original.txt").read_text(), "uncommitted\n")

    def test_changed_source_rejects_shortcut_without_discarding_evidence(self):
        workspace = self.prepare()
        (workspace / "evidence.log").write_text("previous run")
        (self.source / "original.txt").write_text("new source")
        with self.assertRaisesRegex(RuntimeError, "Cannot reuse artifacts"):
            self.prepare(["binding-tests", "--skip-build"])
        self.assertTrue((workspace / "evidence.log").exists())

    def test_same_receipt_reuses_workspace_and_toolchain_change_rebuilds(self):
        workspace = self.prepare()
        (workspace / "old-artifact").write_text("old")
        self.assertEqual(self.prepare(["--skip-regen"]), workspace)
        self.assertTrue((workspace / "old-artifact").exists())
        identity, inventory = self.identity()
        identity["toolchain"] = "b" * 64
        profile.prepare_workspace(self.source, self.state, identity, inventory, [])
        self.assertFalse((workspace / "old-artifact").exists())
        self.assertTrue((self.source / "original.txt").exists())

    def test_unowned_directory_is_never_removed(self):
        workspace = self.state / "workspace"
        workspace.mkdir()
        (workspace / "keep.txt").write_text("valuable")
        with self.assertRaisesRegex(RuntimeError, "unowned"):
            self.prepare()
        self.assertEqual((workspace / "keep.txt").read_text(), "valuable")

    def test_symlink_workspace_is_never_followed(self):
        (self.state / "workspace").symlink_to(self.source, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symlink workspace"):
            self.prepare()
        self.assertTrue((self.source / "original.txt").exists())

    def test_source_symlink_cannot_escape_checkout(self):
        outside = self.root / "outside.txt"
        outside.write_text("private")
        (self.source / "escape").symlink_to(outside)
        with self.assertRaisesRegex(RuntimeError, "escapes"):
            profile.source_inventory(self.source)

    def test_mutation_arguments_rejected_but_comparison_allowed(self):
        for arguments in (["seed-parity-baseline"], ["--target", "promote-validation-baseline"],
                          ["binding-tests", "--seed-runtime-identity-baseline=true"]):
            with self.assertRaisesRegex(RuntimeError, "Baseline mutation"):
                profile.validate_arguments(arguments)
        profile.validate_arguments(["binding-tests", "--skip-surface"])
        with self.assertRaisesRegex(RuntimeError, "escape"):
            profile.validate_arguments(["test", "--root=/another/checkout"])

    def test_environment_selects_child_tools_and_clears_foreign_overrides(self):
        old = {"PATH": "/usr/bin:/bin", "DOTNET_ROOT": "/wrong", "DOTNET_HOST_PATH": "/wrong/dotnet",
               "GIT_DIR": "/wrong/git", "SDKROOT": "/wrong/sdk", "MSBuildSDKsPath": "/wrong/msbuild",
               "SWIFT_INTERFACE_PARSER_PATH": "/wrong/parser"}
        new = profile.environment({"dotnetRoot": str(self.root / "sdk"),
                                   "developerDirectory": str(self.root / "xcode")}, self.state, old)
        self.assertEqual(old["DOTNET_ROOT"], "/wrong")
        self.assertEqual(new["DOTNET_HOST_PATH"], str(self.root / "sdk/dotnet"))
        self.assertEqual(new["TMPDIR"], str(profile.temporary_root(self.state)) + "/")
        self.assertFalse(Path(new["TMPDIR"]).is_relative_to(self.source))
        self.assertFalse(Path(new["TMPDIR"]).is_relative_to(self.state))
        self.assertNotEqual(profile.temporary_root(self.state), profile.temporary_root(self.state / "other-profile"))
        self.assertTrue(new["PATH"].startswith(str(self.root / "sdk") + os.pathsep))
        for key in ("GIT_DIR", "SDKROOT", "MSBuildSDKsPath", "SWIFT_INTERFACE_PARSER_PATH"):
            self.assertNotIn(key, new)

    def test_package_cache_cannot_discover_owner_source_or_another_profile(self):
        selected = {"dotnetRoot": str(self.root / "sdk"),
                    "developerDirectory": str(self.root / "xcode")}
        # Real profiles nest state under the checkout. Installed package executables
        # must not inherit that ancestry, or source discovery crosses the boundary.
        states = [self.source / "artifacts/toolchains" / name for name in ("old", "new")]
        caches = [Path(profile.environment(selected, state, {})["NUGET_PACKAGES"])
                  for state in states]
        self.assertNotEqual(caches[0], caches[1])
        for state, cache in zip(states, caches):
            packaged_generator = cache / "swiftbindings.sdk/0.20.1/tools/net10.0/any"
            self.assertNotIn(self.source, packaged_generator.parents)
            self.assertNotIn(state, packaged_generator.parents)
            self.assertEqual(cache.parent, profile.temporary_root(state))

    def test_net11_profile_selects_framework_without_global_runtime_rollforward(self):
        selected = {"dotnetRoot": str(self.root / "sdk"),
                    "developerDirectory": str(self.root / "xcode"),
                    "managedFramework": "net11.0", "allowPrerelease": True,
                    "sdkVersion": "11.0.100-rc.2.26504.105",
                    "workloadVersion": "11.0.100-rc.2.26509.2"}
        env = profile.environment(selected, self.state, {
            "SWIFT_BINDINGS_MANAGED_FRAMEWORK": "net10.0",
            "DOTNET_ROLL_FORWARD_TO_PRERELEASE": "1", "DOTNET_ROLL_FORWARD": "Major"})
        self.assertEqual(env["SWIFT_BINDINGS_MANAGED_FRAMEWORK"], "net11.0")
        self.assertNotIn("DOTNET_ROLL_FORWARD_TO_PRERELEASE", env)
        self.assertNotIn("DOTNET_ROLL_FORWARD", env)
        self.assertEqual(profile.global_json(selected)["sdk"], {
            "version": selected["sdkVersion"], "rollForward": "disable",
            "allowPrerelease": True, "workloadVersion": selected["workloadVersion"]})

    def test_legacy_profile_keeps_net10_and_rejects_unknown_framework(self):
        selected = {"dotnetRoot": str(self.root / "sdk"),
                    "developerDirectory": str(self.root / "xcode"),
                    "sdkVersion": "10.0.107", "workloadVersion": "10.0.107"}
        env = profile.environment(selected, self.state, {"SWIFT_BINDINGS_MANAGED_FRAMEWORK": "net11.0"})
        self.assertEqual(env["SWIFT_BINDINGS_MANAGED_FRAMEWORK"], "net10.0")
        self.assertFalse(profile.global_json(selected)["sdk"]["allowPrerelease"])
        selected["managedFramework"] = "net12.0"
        with self.assertRaisesRegex(RuntimeError, "Unsupported profile managed framework"):
            profile.environment(selected, self.state, {})

    def test_external_temporary_directory_requires_matching_ownership(self):
        scratch = self.root / "external-tmp"
        selected = {"sdkVersion": "10.0.402", "workloadVersion": "10.0.402.1"}
        profile.prepare_temporary_directory(scratch, self.state, selected)
        (scratch / "evidence.txt").write_text("retained")
        profile.prepare_temporary_directory(scratch, self.state, selected)
        with self.assertRaisesRegex(RuntimeError, "unowned"):
            profile.prepare_temporary_directory(scratch, self.state / "other", selected)
        self.assertEqual((scratch / "evidence.txt").read_text(), "retained")
        unowned = self.root / "unowned-tmp"
        unowned.mkdir()
        with self.assertRaisesRegex(RuntimeError, "unowned"):
            profile.prepare_temporary_directory(unowned, self.state, selected)
        link = self.root / "linked-tmp"
        link.symlink_to(scratch)
        with self.assertRaisesRegex(RuntimeError, "symlink"):
            profile.prepare_temporary_directory(link, self.state, selected)

    def test_external_temporary_directory_pins_sdk_and_updates_pin(self):
        scratch = self.root / "external-tmp"
        selected = {"sdkVersion": "10.0.402", "workloadVersion": "10.0.402.1"}
        profile.prepare_temporary_directory(scratch, self.state, selected)
        self.assertEqual(json.loads((scratch / "global.json").read_text()), profile.global_json(selected))
        self.assertFalse((scratch / "Directory.Build.props").exists())
        selected = {"sdkVersion": "10.0.107", "workloadVersion": "10.0.107"}
        profile.prepare_temporary_directory(scratch, self.state, selected)
        self.assertEqual(json.loads((scratch / "global.json").read_text()), profile.global_json(selected))
        (scratch / "global.json").unlink()
        (scratch / "global.json").symlink_to(self.source / "global.json")
        with self.assertRaisesRegex(RuntimeError, "symlink temporary SDK pin"):
            profile.prepare_temporary_directory(scratch, self.state, selected)
        self.assertEqual(json.loads((self.source / "global.json").read_text())["sdk"]["version"], "10.0.100")

    def test_unrelated_source_change_preserves_only_parser_build_cache(self):
        workspace = self.prepare()
        cache = self.seed_parser_cache(workspace)
        (cache / "release").symlink_to("swift-syntax.o")
        staged = workspace / "src/Swift.Bindings.Sdk/tools/swift-interface-parser/SwiftInterfaceParser"
        staged.parent.mkdir(parents=True)
        staged.write_text("staged executable")
        (workspace / "bin").mkdir()
        (workspace / "bin/managed.dll").write_text("managed output")
        (self.source / "original.txt").write_text("unrelated change")
        self.prepare()
        self.assertEqual((cache / "swift-syntax.o").read_text(), "compiled dependency")
        self.assertTrue((cache / "release").is_symlink())
        self.assertFalse(staged.exists())
        self.assertFalse((workspace / "bin").exists())
        self.assertEqual(list(self.state.glob("parser-cache-*")), [])

    def test_parser_input_changes_discard_cache(self):
        for name in ("tools/SwiftInterfaceParser/Sources/Parser.swift",
                     "tools/SwiftInterfaceParser/Package.swift", "build/Build.SwiftInterfaceParser.cs"):
            with self.subTest(name=name):
                workspace = self.prepare()
                cache = self.seed_parser_cache(workspace)
                (self.source / name).write_text((self.source / name).read_text() + " change")
                self.prepare()
                self.assertFalse(cache.exists())

    def test_toolchain_change_discards_parser_cache(self):
        workspace = self.prepare()
        cache = self.seed_parser_cache(workspace)
        identity, inventory = self.identity()
        identity["toolchain"] = "b" * 64
        profile.prepare_workspace(self.source, self.state, identity, inventory, [])
        self.assertFalse(cache.exists())

    def test_unverified_source_receipts_cannot_authorize_parser_cache(self):
        for invalid in ("missing", "identity", "files", "malformed", "symlink", "incomplete"):
            with self.subTest(invalid=invalid):
                workspace = self.prepare()
                cache = self.seed_parser_cache(workspace)
                receipt_path = self.state / "source.json"
                receipt = json.loads(receipt_path.read_text())
                if invalid == "missing":
                    receipt_path.unlink()
                elif invalid == "identity":
                    receipt["identity"]["owner"] = "/unowned"
                    profile.write_json(receipt_path, receipt)
                elif invalid == "files":
                    receipt["files"] = []
                    profile.write_json(receipt_path, receipt)
                elif invalid == "malformed":
                    receipt_path.write_text("not json")
                elif invalid == "symlink":
                    outside = self.root / "outside-receipt.json"
                    outside.write_text(receipt_path.read_text())
                    receipt_path.unlink()
                    receipt_path.symlink_to(outside)
                else:
                    receipt["identity"]["incomplete"] = True
                    profile.write_json(workspace / profile.MARKER, receipt["identity"])
                    profile.write_json(receipt_path, receipt)
                (self.source / "original.txt").write_text(invalid)
                self.prepare()
                self.assertFalse(cache.exists())
                if receipt_path.is_symlink():
                    receipt_path.unlink()

    def test_symlink_parser_cache_is_not_followed(self):
        workspace = self.prepare()
        outside = self.root / "outside-cache"
        outside.mkdir()
        (outside / "valuable").write_text("keep")
        cache = workspace / "tools/SwiftInterfaceParser/.build"
        cache.symlink_to(outside, target_is_directory=True)
        (self.source / "original.txt").write_text("unrelated change")
        self.prepare()
        self.assertFalse(cache.exists())
        self.assertEqual((outside / "valuable").read_text(), "keep")

    def test_unowned_workspace_receipt_and_abandoned_holders_are_not_trusted(self):
        workspace = self.prepare()
        cache = self.seed_parser_cache(workspace)
        previous = json.loads((workspace / profile.MARKER).read_text())
        previous["owner"] = "/unowned"
        profile.write_json(workspace / profile.MARKER, previous)
        receipt = json.loads((self.state / "source.json").read_text())
        receipt["identity"] = previous
        profile.write_json(self.state / "source.json", receipt)
        (self.source / "original.txt").write_text("unrelated change")
        with self.assertRaisesRegex(RuntimeError, "unowned"):
            self.prepare()
        self.assertTrue((cache / "swift-syntax.o").exists())
        # A stale holder is never consulted or removed, even when its name looks valid.
        holder = self.state / "parser-cache-abandoned"
        holder.mkdir()
        (holder / ".build").mkdir()
        (holder / ".build/valuable").write_text("keep")
        previous["owner"] = str(self.source)
        profile.write_json(workspace / profile.MARKER, previous)
        self.prepare()
        self.assertFalse(cache.exists())
        self.assertEqual((holder / ".build/valuable").read_text(), "keep")

    def test_failed_refresh_cannot_recover_cache_from_holder(self):
        workspace = self.prepare()
        self.seed_parser_cache(workspace)
        (self.source / "original.txt").write_text("unrelated change")
        identity, inventory = self.identity()
        with patch.object(profile.subprocess, "run", side_effect=RuntimeError("interrupted clone")):
            with self.assertRaisesRegex(RuntimeError, "interrupted clone"):
                profile.prepare_workspace(self.source, self.state, identity, inventory, [])
        self.prepare()
        self.assertFalse((workspace / "tools/SwiftInterfaceParser/.build").exists())
        self.assertEqual(list(self.state.glob("parser-cache-*")), [])

    def test_profile_lock_prevents_concurrent_mutation(self):
        with profile.profile_lock(self.state):
            with self.assertRaisesRegex(RuntimeError, "Another invocation"):
                with profile.profile_lock(self.state):
                    self.fail("Acquired duplicate lock")


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
