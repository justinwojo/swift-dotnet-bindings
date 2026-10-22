# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('consumer', Path(__file__).with_name('verify-net11-package-consumer.py'))
consumer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(consumer)


class AssetEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cache = self.root / 'old-cache'
        self.packages = self.root / 'restored'
        self.specifications = []
        for package_id, assembly in [('SwiftBindings.Runtime', 'Swift.Runtime.dll'),
                                     ('SwiftBindings.Apple', 'SwiftBindings.Apple.dll'),
                                     ('MixedPackFixture.SbMixedPack', 'SbMixedPack.dll')]:
            item = {'id': package_id, 'version': '1.0.0', 'assembly': assembly}
            archive = self.cache / package_id.lower() / '1.0.0' / f'{package_id.lower()}.1.0.0.nupkg'
            archive.parent.mkdir(parents=True)
            with zipfile.ZipFile(archive, 'w') as package:
                for platform in consumer.PLATFORMS:
                    package.writestr(f'lib/net10.0-{platform}26.0/{assembly}', b'old-platform-dll')
                if package_id == 'MixedPackFixture.SbMixedPack':
                    package.writestr('lib/net10.0-ios26.0/SbMixedPack.ObjC.iOS.dll', b'objc-companion')
                package.writestr('native/Original.framework/Original', b'old-native-bytes')
            item['sha256'] = consumer.sha256(archive.read_bytes())
            self.specifications.append(item)
        self.staged = consumer.stage_packages(self.cache, self.root / 'feed', self.specifications)
        self.assets = {'targets': {'net11.0-ios27.0': {}}, 'libraries': {}}
        for package in self.staged:
            key = package['id'] + '/1.0.0'
            relative = package['id'].lower() + '/1.0.0'
            selected = {f"lib/net10.0-ios26.0/{package['assembly']}": {}}
            if package['id'].startswith('Mixed'):
                selected['lib/net10.0-ios26.0/SbMixedPack.ObjC.iOS.dll'] = {}
            self.assets['targets']['net11.0-ios27.0'][key] = {
                'compile': copy.deepcopy(selected), 'runtime': copy.deepcopy(selected),
                'build': {'buildTransitive/original.targets': {}}}
            self.assets['libraries'][key] = {'type': 'package', 'path': relative}
            with zipfile.ZipFile(package['staged']) as archive:
                archive.extractall(self.packages / relative)

    def check(self):
        return consumer.check_assets(self.assets, 'ios', self.staged, self.packages)

    def test_real_platform_asset_groups_and_original_bytes_pass(self):
        result = self.check()
        self.assertEqual(3, len(result[0]['selected']))

    def test_neutral_compile_fallback_fails(self):
        entry = self.assets['targets']['net11.0-ios27.0']['SwiftBindings.Runtime/1.0.0']
        entry['compile'] = {'lib/net10.0/Swift.Runtime.dll': {}}
        with self.assertRaisesRegex(ValueError, 'must select lib/net10.0-ios'):
            self.check()

    def test_neutral_runtime_fallback_fails(self):
        entry = self.assets['targets']['net11.0-ios27.0']['SwiftBindings.Runtime/1.0.0']
        entry['runtime'] = {'lib/net10.0/Swift.Runtime.dll': {}}
        with self.assertRaisesRegex(ValueError, 'must select lib/net10.0-ios'):
            self.check()

    def test_missing_apple_package_fails(self):
        del self.assets['targets']['net11.0-ios27.0']['SwiftBindings.Apple/1.0.0']
        with self.assertRaisesRegex(ValueError, 'missing package SwiftBindings.Apple'):
            self.check()

    def test_missing_objc_companion_fails(self):
        entry = self.assets['targets']['net11.0-ios27.0']['MixedPackFixture.SbMixedPack/1.0.0']
        del entry['compile']['lib/net10.0-ios26.0/SbMixedPack.ObjC.iOS.dll']
        with self.assertRaisesRegex(ValueError, 'ObjC companion'):
            self.check()

    def test_package_targets_missing_fails(self):
        del self.assets['targets']['net11.0-ios27.0']['SwiftBindings.Runtime/1.0.0']['build']
        with self.assertRaisesRegex(ValueError, 'buildTransitive'):
            self.check()

    def test_restored_native_rebuilt_with_candidate_fails(self):
        path = self.packages / 'swiftbindings.runtime/1.0.0/native/Original.framework/Original'
        path.write_bytes(b'regenerated-under-new-xcode')
        with self.assertRaisesRegex(ValueError, 'Restored native asset differs'):
            self.check()

    def test_restored_assembly_replaced_with_stub_fails(self):
        path = self.packages / 'swiftbindings.runtime/1.0.0/lib/net10.0-ios26.0/Swift.Runtime.dll'
        path.write_bytes(b'Stub.dll')
        with self.assertRaisesRegex(ValueError, 'Restored assembly differs'):
            self.check()

    def test_repacked_same_version_fails_hash_pin(self):
        archive = self.cache / 'swiftbindings.runtime/1.0.0/swiftbindings.runtime.1.0.0.nupkg'
        with zipfile.ZipFile(archive, 'a') as package:
            package.writestr('lib/net11.0/Swift.Runtime.dll', b'new-dll')
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            consumer.stage_packages(self.cache, self.root / 'other-feed', self.specifications)

    def test_missing_retained_package_is_explicit(self):
        with self.assertRaisesRegex(ValueError, 'Missing retained package'):
            consumer.stage_packages(self.root / 'missing-cache', self.root / 'other-feed', self.specifications)

    def test_net10_restore_or_framework_reducer_is_not_net11_restore(self):
        self.assets['targets'] = {'net10.0-ios26.0': {}}
        with self.assertRaisesRegex(ValueError, 'No actual net11'):
            self.check()

    def test_missing_workload_is_not_reported_as_package_incompatibility(self):
        self.assertEqual('missing-apple-workload', consumer.diagnose('error NETSDK1147: install ios'))
        self.assertEqual('xcode-workload-version-mismatch', consumer.diagnose('This workload requires Xcode 27.0'))

    def test_compiler_receipts_preserve_both_assemblies_and_native_references(self):
        project = self.root / 'consumer/Consumer.csproj'
        project.parent.mkdir()
        evidence = self.root / 'receipts'
        evidence.mkdir()
        for relative, content in [('obj/Release/net11.0-ios27.0/Consumer.dll', b'consumer'),
                                  ('identity/obj/Release/net11.0-ios27.0/Identity.dll', b'identity'),
                                  ('native-references.txt', b'original/native/framework')]:
            path = project.parent / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        hashes = consumer.preserve_compiler_outputs(project, evidence, 'net11.0-ios27.0')
        self.assertEqual(consumer.sha256(b'consumer'), hashes['Consumer.dll'])
        self.assertEqual(consumer.sha256(b'identity'), hashes['Identity.dll'])
        self.assertEqual(b'consumer', (evidence / 'compiled/Consumer.dll').read_bytes())
        self.assertEqual(b'identity', (evidence / 'compiled/Identity.dll').read_bytes())
        self.assertEqual(b'original/native/framework', (evidence / 'native-references.txt').read_bytes())

    def test_projects_keep_old_packages_and_no_generator_import(self):
        fixtures = Path(__file__).resolve().parents[1] / 'fixtures/net11-package-consumer'
        for platform in consumer.PLATFORMS:
            project = consumer.write_projects(self.root / platform, fixtures, platform)
            xml = project.read_text()
            self.assertIn(f'net11.0-{platform}27.0', xml)
            self.assertNotIn('SwiftBindings.Sdk', xml)
            self.assertNotIn('ProjectReference Include="../../src/', xml)
            self.assertEqual(platform == 'ios', 'MixedPackFixture.SbMixedPack' in xml)


if __name__ == '__main__':
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
