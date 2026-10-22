#!/usr/bin/env python3
"""Real .NET 11 desktop CoreCLR/package/native canary. Invoke through nuke coreclr-canary."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import zipfile
from xml.sax.saxutils import escape


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(command, cwd, env, log):
    result = subprocess.run([str(part) for part in command], cwd=cwd, env=env,
                            text=True, capture_output=True, timeout=600)
    log.write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f'Command failed ({result.returncode}); see {log}\n' + (result.stdout + result.stderr)[-6000:])
    return result.stdout


def verify(args):
    if platform.system() != 'Darwin':
        raise RuntimeError('The CoreCLR canary requires macOS; no Apple tooling was invoked')
    root, dotnet, feed, evidence, developer = [Path(value).resolve() for value in
        (args.root, args.dotnet, args.feed, args.evidence, args.developer_dir)]
    if not dotnet.is_file() or not developer.is_dir() or not feed.is_dir():
        raise RuntimeError('dotnet, feed, and Developer directory must exist')
    sdks = sorted((dotnet.parent / 'sdk').glob('11.*'))
    if len(sdks) != 1:
        raise RuntimeError('Use an isolated installation containing one .NET 11 SDK')
    package_candidates = [path for path in feed.rglob('*.nupkg')
        if path.name.lower() == f'swiftbindings.runtime.{args.runtime_version}.nupkg'.lower()]
    if len(package_candidates) != 1:
        raise RuntimeError('The feed must contain exactly one matching Runtime nupkg')
    package = package_candidates[0]
    evidence.mkdir(parents=True, exist_ok=True)
    result_path = evidence / 'results.json'
    result_path.unlink(missing_ok=True)
    receipt = {'scope': 'macOS desktop CoreCLR; no mobile runtime or Apple packaging qualification',
               'sdk': sdks[0].name, 'dotnet': str(dotnet), 'developerDir': str(developer),
               'package': str(package), 'packageSha256': sha256(package), 'results': [], 'status': 'RUNNING'}
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_MULTILEVEL_LOOKUP='0',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DEVELOPER_DIR=str(developer))
    for key in ('DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'MSBuildSDKsPath',
                'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE'):
        env.pop(key, None)
    # Keep consumer outside the repository so no app-authored trim roots or repo props leak in.
    try:
        with tempfile.TemporaryDirectory(prefix='swift-coreclr-canary-') as temporary:
            scratch = Path(temporary)
            source = root / 'BindingTests/CoreClrCanary'
            for name in ('CoreClrCanary.csproj', 'Program.cs', 'Canary.swift'):
                shutil.copy2(source / name, scratch / name)
            receipt['sources'] = {name: sha256(scratch / name) for name in ('CoreClrCanary.csproj', 'Program.cs', 'Canary.swift')}
            (scratch / 'global.json').write_text(json.dumps({'sdk': {'version': sdks[0].name, 'rollForward': 'disable', 'allowPrerelease': True}}))
            (scratch / 'NuGet.Config').write_text('<configuration><packageSources><clear />'
                f'<add key="canary" value="{escape(str(feed))}" />'
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
                '</packageSources></configuration>')
            env['NUGET_PACKAGES'] = str(scratch / 'packages')
            run([dotnet, '--info'], scratch, env, evidence / 'dotnet-info.txt')
            run(['xcodebuild', '-version'], scratch, env, evidence / 'xcode-version.txt')
            run(['xcrun', 'swiftc', '--version'], scratch, env, evidence / 'swift-version.txt')
            sdk = run(['xcrun', '--sdk', 'macosx', '--show-sdk-path'], scratch, env, evidence / 'sdk-path.txt').strip()
            architecture = {'arm64': 'arm64', 'x86_64': 'x86_64'}[platform.machine()]
            fixture = scratch / 'libCoreClrCanary.dylib'
            run(['xcrun', '--sdk', 'macosx', 'swiftc', '-sdk', sdk,
                 '-target', f'{architecture}-apple-macosx13.0', '-emit-library',
                 '-module-name', 'CoreClrCanary', scratch / 'Canary.swift', '-o', fixture],
                scratch, env, evidence / 'swift-build.txt')
            with zipfile.ZipFile(package) as archive:
                native_candidates = [name for name in archive.namelist()
                    if 'macos-' in name and name.endswith('/SwiftBindingsRuntime.framework/SwiftBindingsRuntime')]
                if len(native_candidates) != 1:
                    raise RuntimeError(f'Expected one macOS native framework binary in Runtime package: {native_candidates}')
                native = archive.read(native_candidates[0])
                receipt['nativePackageEntry'] = native_candidates[0]
                receipt['nativeSha256'] = hashlib.sha256(native).hexdigest()
            for configuration in ('Debug', 'Release'):
                for policy in ('Safe', 'Direct'):
                    for dynamic_code in (True, False):
                        case = f'{configuration}-{policy}-dynamic-{dynamic_code}'
                        output = scratch / 'outputs' / case
                        project_dir = scratch / 'cases' / case
                        project_dir.mkdir(parents=True)
                        for name in ('CoreClrCanary.csproj', 'Program.cs'):
                            shutil.copy2(scratch / name, project_dir / name)
                        command = [dotnet, 'publish' if configuration == 'Release' else 'build',
                            project_dir / 'CoreClrCanary.csproj', '-c', configuration, '-o', output,
                            f'-p:CanaryRuntimeVersion={args.runtime_version}', f'-p:SwiftBindingsInteropMode={policy}',
                            f'-p:DynamicCodeSupport={str(dynamic_code).lower()}', '--nologo', '-v:minimal']
                        if configuration == 'Release':
                            command += ['-r', 'osx-' + ('arm64' if architecture == 'arm64' else 'x64'),
                                        '--self-contained', 'true', '-p:UseAppHost=true',
                                        '-p:PublishTrimmed=true', '-p:TrimMode=link']
                        # ILLink substitutes capability constants, so each capability is a
                        # separate publish; mutating runtimeconfig after trimming is insufficient.
                        run(command, scratch, env, evidence / f'{case}-build.txt')
                        assets = json.loads((project_dir / 'obj/project.assets.json').read_text())
                        runtime_libraries = {key: value for target in assets['targets'].values() for key, value in target.items()
                            if key.lower().startswith('swiftbindings.runtime/')}
                        selected = {key: {group: sorted(value.get(group, {})) for group in ('compile', 'runtime')}
                                    for key, value in runtime_libraries.items()}
                        if not selected or any(not value['compile'] or not value['runtime'] for value in selected.values()):
                            raise RuntimeError('Runtime compile/runtime assets were not selected')
                        if any(not asset.startswith('lib/net10.0/') for value in selected.values()
                               for group in value.values() for asset in group):
                            raise RuntimeError(f'Canary must consume the existing neutral net10 Runtime assets: {selected}')
                        restored = scratch / 'packages/swiftbindings.runtime' / args.runtime_version.lower()
                        restored_package = restored / f'swiftbindings.runtime.{args.runtime_version.lower()}.nupkg'
                        if not restored_package.is_file() or sha256(restored_package) != receipt['packageSha256']:
                            raise RuntimeError('Restored Runtime package differs from the exact feed artifact under test')
                        shutil.copy2(fixture, output / fixture.name)
                        # Neutral desktop SDK has no NativeReference embedding. Extract the unchanged
                        # shipped framework binary for this scratch host only; never rewrite the nupkg.
                        (output / 'libSwiftBindingsRuntime.dylib').write_bytes(native)
                        config_path = output / 'CoreClrCanary.runtimeconfig.json'
                        config = json.loads(config_path.read_text())
                        if 'rollForward' in config['runtimeOptions']:
                            raise RuntimeError('Generator roll-forward leaked into the consumer')
                        configured = config['runtimeOptions'].get('configProperties', {}).get(
                            'System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported')
                        if configured != dynamic_code:
                            raise RuntimeError('SDK did not transport the publish-time capability setting')
                        run_env = dict(env, CANARY_DYNAMIC_CODE=str(dynamic_code).lower())
                        stdout = run([dotnet, 'exec', output / 'CoreClrCanary.dll'], output, run_env,
                            evidence / f'{case}.txt')
                        observed = json.loads(stdout.strip())
                        if observed['status'] != 'PASS' or observed['dynamicCode'] != dynamic_code:
                            raise RuntimeError('Canary did not attest the requested process capability')
                        receipt['results'].append({'configuration': configuration, 'trimmed': configuration == 'Release',
                            'policy': policy, 'dynamicCodeConfigured': dynamic_code, 'selectedAssets': selected,
                            'command': [str(part) for part in command],
                            'runtimeConfig': config, 'observed': observed})
                        result_path.write_text(json.dumps(receipt, indent=2) + '\n')
            receipt['status'] = 'PASS'
    except Exception as exception:
        receipt.update(status='FAIL', error=str(exception))
        raise
    finally:
        result_path.write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'PASS: {len(receipt["results"])} real .NET 11 desktop CoreCLR canary processes; evidence: {result_path}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('root', 'dotnet', 'feed', 'runtime-version', 'developer-dir', 'evidence'):
        parser.add_argument('--' + argument, required=True)
    verify(parser.parse_args())
