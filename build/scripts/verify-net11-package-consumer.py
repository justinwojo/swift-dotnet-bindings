#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Restore and compile real .NET 11 Apple consumers of pinned .NET 10 nupkgs.

Run via nuke verify-net11-package-consumer. This does not regenerate, repack,
link, install or execute any native asset, and does not qualify a runtime lane.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape
import zipfile

PLATFORMS = ('ios', 'tvos', 'macos', 'maccatalyst')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def stage_packages(cache, feed, specifications):
    """Only the original bytes are admissible; same-version test stubs fail."""
    evidence = []
    feed.mkdir(parents=True)
    for spec in specifications:
        name = f"{spec['id'].lower()}.{spec['version']}.nupkg"
        source = cache / spec['id'].lower() / spec['version'] / name
        require(source.is_file(), f'Missing retained package: {source}; do not regenerate it with .NET 11/Xcode 27')
        data = source.read_bytes()
        require(sha256(data) == spec['sha256'], f'Pinned package hash mismatch: {source}')
        with zipfile.ZipFile(source) as archive:
            libraries = sorted(n for n in archive.namelist() if n.startswith('lib/') and n.endswith('.dll'))
            require(libraries and all(n.startswith('lib/net10.0') for n in libraries),
                    f'Package must contain only original net10 managed assets: {source}')
            native = {n: sha256(archive.read(n)) for n in archive.namelist()
                      if '.framework/' in n and not n.endswith('/')}
            require(native, f'No packaged native framework assets: {source}')
        (feed / name).write_bytes(data)
        evidence.append(dict(spec, source=str(source), staged=str(feed / name),
                             libAssets=libraries, nativeAssetHashes=native))
    return evidence


def check_assets(assets, platform_name, specifications, package_folder):
    """Inspect actual NuGet restore output, including restored-file byte identity."""
    expected_ids = {'SwiftBindings.Runtime', 'SwiftBindings.Apple'}
    if platform_name == 'ios':
        expected_ids.add('MixedPackFixture.SbMixedPack')
    results = []
    targets = [(name, entries) for name, entries in assets.get('targets', {}).items()
               if name.startswith(f'net11.0-{platform_name}')]
    require(targets, f'No actual net11.0-{platform_name} target in project.assets.json')
    for name, entries in targets:
        selected = {}
        for spec in specifications:
            if spec['id'] not in expected_ids:
                continue
            key = f"{spec['id']}/{spec['version']}"
            require(key in entries, f'{name}: missing package {key}')
            entry = entries[key]
            expected = f"lib/net10.0-{platform_name}26.0/{spec['assembly']}"
            for kind in ('compile', 'runtime'):
                require(expected in entry.get(kind, {}), f'{name}: {key} {kind} must select {expected}: {entry.get(kind)}')
                require(all(not p.startswith('lib/net10.0/') for p in entry[kind]),
                        f'{name}: neutral Runtime fallback in {kind}')
            library = assets.get('libraries', {}).get(key, {})
            require(library.get('type') == 'package', f'{key}: must be a restored package')
            restored = package_folder / library['path']
            archive_path = Path(spec['staged'])
            with zipfile.ZipFile(archive_path) as archive:
                for path in entry['compile']:
                    if path.endswith('.dll'):
                        require((restored / path).is_file(), f'Missing restored compile asset: {restored / path}')
                        require(sha256((restored / path).read_bytes()) == sha256(archive.read(path)),
                                f'Restored assembly differs from pinned nupkg: {path}')
                if spec['id'] == 'MixedPackFixture.SbMixedPack':
                    companion = f'lib/net10.0-{platform_name}26.0/SbMixedPack.ObjC.iOS.dll'
                    require(companion in entry['compile'], 'Embedded ObjC companion not selected for compilation')
                # Check native bytes in the restored extraction, not just the staged archive.
                for path, digest in spec['nativeAssetHashes'].items():
                    require((restored / path).is_file() and sha256((restored / path).read_bytes()) == digest,
                            f'Restored native asset differs from pinned nupkg: {path}')
            require(entry.get('build'), f'{key}: package buildTransitive targets were not selected')
            selected[key] = {kind: sorted(entry.get(kind, {})) for kind in ('compile', 'runtime', 'build')}
        results.append({'target': name, 'selected': selected})
    return results


def write_projects(directory, fixtures, platform_name):
    directory.mkdir(parents=True)
    tfm = f'net11.0-{platform_name}27.0'
    identity = directory / 'identity'
    identity.mkdir()
    shutil.copy2(fixtures / 'Identity.cs', identity / 'Identity.cs')
    common = f'''<PropertyGroup><TargetFramework>{tfm}</TargetFramework>
      <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
      <NoWarn>$(NoWarn);CA1416;CA1422</NoWarn><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
      <SupportedOSPlatformVersion>26.0</SupportedOSPlatformVersion>
      <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
      <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
      </PropertyGroup>'''
    (identity / 'Identity.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">{common}
      <ItemGroup><Compile Include="Identity.cs" />
      <PackageReference Include="SwiftBindings.Apple" Version="26.2.0-mixedpack" /></ItemGroup></Project>''')
    shutil.copy2(fixtures / 'Consumer.cs', directory / 'Consumer.cs')
    if platform_name == 'ios':
        shutil.copy2(fixtures / 'MixedBinding.cs', directory / 'MixedBinding.cs')
        dependency = '<PackageReference Include="MixedPackFixture.SbMixedPack" Version="0.0.0-mixedpack" />'
        extra_compile = '<Compile Include="MixedBinding.cs" />'
        native_dependency = ';_ResolveSbMixedPackNativeReferences'
    else:
        dependency = '<PackageReference Include="SwiftBindings.Apple" Version="26.2.0-mixedpack" />'
        extra_compile = native_dependency = ''
    # Use real package imports and normal ResolveReferences/Compile targets. Only
    # the managed compiler is requested; Apple app bundling/linking is separate.
    (directory / 'Consumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">{common}
      <ItemGroup><Compile Include="Consumer.cs" />{extra_compile}{dependency}
      <ProjectReference Include="identity/Identity.csproj" /></ItemGroup>
      <Target Name="VerifyConsumerCompile" DependsOnTargets="ResolveReferences;Compile{native_dependency}">
        <WriteLinesToFile File="$(MSBuildProjectDirectory)/native-references.txt"
          Lines="@(NativeReference->'%(FullPath)')" Overwrite="true" />
      </Target></Project>''')
    return directory / 'Consumer.csproj'


def preserve_compiler_outputs(project, evidence, tfm):
    binaries = evidence / 'compiled'
    binaries.mkdir()
    result = {}
    for name, source in (
        ('Consumer.dll', project.parent / 'obj/Release' / tfm / 'Consumer.dll'),
        ('Identity.dll', project.parent / 'identity/obj/Release' / tfm / 'Identity.dll')):
        require(source.is_file(), f'Compiler did not produce {source}')
        shutil.copy2(source, binaries / name)
        result[name] = sha256(source.read_bytes())
    shutil.copy2(project.parent / 'native-references.txt', evidence / 'native-references.txt')
    return result


def diagnose(log):
    if 'NETSDK1147' in log:
        return 'missing-apple-workload'
    if 'requires Xcode' in log or 'requires the Xcode' in log:
        return 'xcode-workload-version-mismatch'
    return 'restore-or-compile-failed'


def verify(root, dotnet, cache, evidence):
    require(platform.system() == 'Darwin', 'Real Apple restore/compile requires macOS')
    fixtures = root / 'build/fixtures/net11-package-consumer'
    specifications = json.loads((fixtures / 'packages.json').read_text())['packages']
    scratch = Path(tempfile.mkdtemp(prefix='swift-net11-packages-')).resolve()
    report = {'schemaVersion': 1, 'status': 'incomplete', 'scratch': str(scratch), 'dotnet': str(dotnet),
              'scope': 'Real Apple restore, net10 package selection, managed API compilation, and original native asset integrity; no native link or runtime qualification',
              'platforms': [], 'toolchainProfile': os.environ.get('SWIFT_BINDINGS_TOOLCHAIN_PROFILE'),
              'toolchainFingerprint': os.environ.get('SWIFT_BINDINGS_TOOLCHAIN_FINGERPRINT')}
    report_path = evidence / 'evidence.json'
    try:
        report['packages'] = stage_packages(cache, scratch / 'feed', specifications)
        env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_HOST_PATH=str(dotnet),
                   DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                   DOTNET_CLI_TELEMETRY_OPTOUT='1', NUGET_PACKAGES=str(scratch / 'packages'),
                   PATH=str(dotnet.parent) + os.pathsep + os.environ.get('PATH', ''))
        for key in ('DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'MSBuildSDKsPath'):
            env.pop(key, None)
        sdk = subprocess.check_output([str(dotnet), '--version'], cwd=scratch, env=env, text=True, timeout=60).strip()
        require(sdk.startswith('11.'), f'Requires an isolated .NET 11 SDK, observed {sdk}')
        report['sdk'] = sdk
        report['developerDirectory'] = os.environ.get('DEVELOPER_DIR')
        for label, command in (('dotnet-info', [str(dotnet), '--info']),
                               ('workloads', [str(dotnet), 'workload', 'list']),
                               ('xcode', ['xcodebuild', '-version'])):
            identity = subprocess.run(command, cwd=scratch, env=env, text=True, capture_output=True, timeout=60)
            (evidence / (label + '.log')).write_text(identity.stdout + identity.stderr)
            require(identity.returncode == 0, f'Cannot capture {label} toolchain identity')
        write_json(scratch / 'global.json', {'sdk': {'version': sdk, 'rollForward': 'disable', 'allowPrerelease': True}})
        (scratch / 'NuGet.Config').write_text(f'''<configuration><packageSources><clear />
          <add key="frozen-net10" value="{escape(str(scratch / 'feed'))}" />
          <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources><packageSourceMapping><packageSource key="frozen-net10">
          <package pattern="SwiftBindings.*" /><package pattern="MixedPackFixture.*" />
          </packageSource><packageSource key="nuget.org"><package pattern="Microsoft.*" />
          <package pattern="System.*" /></packageSource></packageSourceMapping></configuration>''')
        for platform_name in PLATFORMS:
            leg = {'platform': platform_name, 'targetFramework': f'net11.0-{platform_name}27.0', 'status': 'incomplete'}
            report['platforms'].append(leg)
            leg_evidence = evidence / platform_name
            leg_evidence.mkdir()
            project = write_projects(scratch / platform_name, fixtures, platform_name)
            shutil.copytree(project.parent, leg_evidence / 'project')
            try:
                for phase, arguments in (
                    ('restore', ['restore', str(project), '--configfile', str(scratch / 'NuGet.Config'), '--packages', str(scratch / 'packages')]),
                    ('compile', ['build', str(project), '--no-restore', '-c', 'Release', '-t:VerifyConsumerCompile', '--nologo'])):
                    command = [str(dotnet), *arguments, '-v:minimal']
                    leg[phase + 'Command'] = command
                    result = subprocess.run(command, cwd=scratch, env=env, text=True, capture_output=True, timeout=240)
                    log = result.stdout + result.stderr
                    (leg_evidence / (phase + '.log')).write_text(log)
                    leg[phase + 'ExitCode'] = result.returncode
                    if phase == 'restore':
                        leg['assetsFileProduced'] = (project.parent / 'obj/project.assets.json').is_file()
                    if result.returncode != 0:
                        leg['diagnosis'] = diagnose(log)
                        raise ValueError(f'{platform_name} {phase} failed: {leg["diagnosis"]}; see {leg_evidence / (phase + ".log")}')
                    if phase == 'restore':
                        assets_path = project.parent / 'obj/project.assets.json'
                        shutil.copy2(assets_path, leg_evidence / 'project.assets.json')
                        leg['assetSelections'] = check_assets(json.loads(assets_path.read_text()), platform_name,
                                                              report['packages'], scratch / 'packages')
                leg['compiledAssemblies'] = preserve_compiler_outputs(project, leg_evidence, leg['targetFramework'])
                leg['compiledAssemblySha256'] = leg['compiledAssemblies']['Consumer.dll']
                references = (project.parent / 'native-references.txt').read_text().splitlines()
                expected_names = ['SwiftBindingsRuntime.xcframework', 'SBApple.xcframework']
                if platform_name == 'ios':
                    expected_names.append('SbMixedPackSwiftBindings.xcframework')
                for name in expected_names:
                    require(any(Path(p).name == name and Path(p).is_dir() for p in references),
                            f'{platform_name}: missing package NativeReference {name}: {references}')
                leg['nativeReferences'] = references
                leg['status'] = 'passed'
                print(f'PASS: {platform_name}: actual restore selects net10 platform packages; public APIs compile')
            except (ValueError, subprocess.TimeoutExpired) as error:
                leg['status'] = 'failed'
                leg['error'] = str(error)
                print(f'FAIL: {error}')
            write_json(report_path, report)
        require(all(p['status'] == 'passed' for p in report['platforms']), 'One or more Apple package-consumer legs failed')
        report['status'] = 'passed'
    except Exception as error:
        report['status'] = 'failed'
        report['error'] = str(error)
        raise
    finally:
        report['scratchRetained'] = True
        if report['status'] == 'passed':
            try:
                shutil.rmtree(scratch)
                report['scratchRetained'] = False
            except OSError as error:
                report['cleanupWarning'] = str(error)
        write_json(report_path, report)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('root', 'dotnet', 'package-cache', 'evidence'):
        parser.add_argument('--' + option, required=True, type=Path)
    args = parser.parse_args()
    # A unique directory retains every run's evidence; a later failed run cannot
    # leave an earlier successful verdict at the same report path.
    destination = args.evidence.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    run_evidence = Path(tempfile.mkdtemp(prefix='run-', dir=destination))
    print(f'Evidence: {run_evidence}', flush=True)
    verify(args.root.resolve(), args.dotnet.resolve(), args.package_cache.resolve(), run_evidence)
