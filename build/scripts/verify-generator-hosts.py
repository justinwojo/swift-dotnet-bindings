#!/usr/bin/env python3
"""Exercise one built generator on isolated 10-only, 11-only and mixed runtimes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def run(command, env, cwd):
    return subprocess.run([str(x) for x in command], env=env, cwd=cwd,
                          text=True, capture_output=True, timeout=120)


def verify(generator, legacy, candidate, evidence):
    generator, legacy, candidate, evidence = [Path(p).resolve() for p in (generator, legacy, candidate, evidence)]
    config = generator.with_suffix('.runtimeconfig.json')
    original = json.loads(config.read_text())
    assert original['runtimeOptions']['tfm'] == 'net10.0', 'Generator API floor changed'
    assert original['runtimeOptions']['rollForward'] == 'Major', 'Generator must own major roll-forward'
    evidence.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    for key in list(env):
        if key.startswith(('DOTNET_ROOT', 'DOTNET_ROLL_FORWARD', 'COREHOST_TRACE', 'DOTNET_HOST_TRACE')):
            env.pop(key)
    env['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    versions = {}
    for label, host, major in [('net10', legacy, '10.'), ('net11', candidate, '11.')]:
        result = run([host, '--list-runtimes'], env, evidence)
        assert result.returncode == 0, result.stderr
        cores = [line.split()[1] for line in result.stdout.splitlines() if line.startswith('Microsoft.NETCore.App ')]
        assert cores and all(v.startswith(major) for v in cores), f'{label} is not isolated: {result.stdout}'
        versions[label] = cores
    results = []
    with tempfile.TemporaryDirectory(prefix='generator-hosts-', dir=evidence) as temporary:
        scratch = Path(temporary)
        interface = scratch / 'RealityKit.swiftmodule' / 'arm64.swiftinterface'
        interface.parent.mkdir()
        interface.write_text('import Swift\npublic import RealityFoundation\n')
        mixed = scratch / 'mixed-dotnet'
        (mixed / 'host').mkdir(parents=True)
        shutil.copy2(candidate, mixed / 'dotnet')
        (mixed / 'host' / 'fxr').symlink_to(candidate.parent / 'host' / 'fxr', target_is_directory=True)
        shared = mixed / 'shared' / 'Microsoft.NETCore.App'
        shared.mkdir(parents=True)
        for label, host in [('net10', legacy), ('net11', candidate)]:
            for version in versions[label]:
                (shared / version).symlink_to(host.parent / 'shared' / 'Microsoft.NETCore.App' / version,
                                             target_is_directory=True)
        operations = [('--help',), ('--detect-apple-cross-module-deps', str(interface), '--apple-version', '26.2.1')]
        for label, host, major in [('net10', legacy, '10.'), ('net11', candidate, '11.'), ('mixed', mixed / 'dotnet', '10.')]:
            for index, arguments in enumerate(operations):
                trace = evidence / f'{label}-{index}.hosttrace.txt'
                process_env = dict(env, DOTNET_ROOT=str(host.parent), DOTNET_ROLL_FORWARD_TO_PRERELEASE='1',
                                   COREHOST_TRACE='1', COREHOST_TRACEFILE=str(trace),
                                   DOTNET_HOST_TRACE='1', DOTNET_HOST_TRACEFILE=str(trace))
                result = run([host, 'exec', generator, *arguments], process_env, scratch)
                (evidence / f'{label}-{index}.output.txt').write_text(result.stdout + result.stderr)
                assert result.returncode == 0, f'{label}: {result.stdout}\n{result.stderr}'
                if index == 0:
                    assert '--xcframework' in result.stdout, result.stdout
                else:
                    assert 'RealityFoundation|SwiftBindings.Apple.RealityFoundation|[26.2.1,26.3.0)' in result.stdout, result.stdout
                trace_text = trace.read_text()
                # Host trace proves the selected runtime; successful CLI output alone cannot.
                chosen = [line for line in trace_text.splitlines() if 'Chose FX version' in line and 'Microsoft.NETCore.App' in line]
                assert chosen and any('/' + major in line for line in chosen), f'{label}: {chosen}'
                results.append({'host': label, 'operation': arguments[0], 'selected_runtime': chosen})
        negative = scratch / 'no-roll-forward.runtimeconfig.json'
        negative_config = json.loads(config.read_text())
        negative_config['runtimeOptions'].pop('rollForward')
        negative.write_text(json.dumps(negative_config))
        failure = run([candidate, 'exec', '--runtimeconfig', negative, generator, '--help'],
                      dict(env, DOTNET_ROOT=str(candidate.parent), DOTNET_ROLL_FORWARD_TO_PRERELEASE='1'), scratch)
        (evidence / 'negative-control.txt').write_text(failure.stdout + failure.stderr)
        assert failure.returncode != 0 and '10.0.0' in failure.stderr, 'Negative control unexpectedly succeeded'
    receipt = {'generator': str(generator), 'generator_sha256': hashlib.sha256(generator.read_bytes()).hexdigest(),
               'runtimeconfig': original, 'runtimes': versions, 'prerelease_roll_forward': 'generator process only',
               'results': results, 'without_major_roll_forward': 'rejected on 11-only host'}
    (evidence / 'generator-hosts.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print('PASS: generator help and dependency parsing on 10-only, 11-only and mixed hosts; negative control rejected.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('generator', 'legacy-dotnet', 'candidate-dotnet', 'evidence'):
        parser.add_argument('--' + argument, required=True)
    args = parser.parse_args()
    verify(args.generator, args.legacy_dotnet, args.candidate_dotnet, args.evidence)
