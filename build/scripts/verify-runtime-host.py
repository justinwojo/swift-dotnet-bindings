#!/usr/bin/env python3
"""Build a real net11 host consuming the net10 Runtime and its production targets."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape


def verify(root, dotnet, evidence):
    root, dotnet, evidence = [Path(p).resolve() for p in (root, dotnet, evidence)]
    evidence.mkdir(parents=True, exist_ok=True)
    sdks = list((dotnet.parent / 'sdk').iterdir())
    assert len(sdks) == 1 and sdks[0].name.startswith('11.'), 'Use the isolated net11 SDK'
    runtime = root / 'src/Swift.Runtime/src/bin/Debug/net10.0/Swift.Runtime.dll'
    native = root / 'src/Swift.Runtime/native/SwiftBindingsRuntime.xcframework/macos-arm64_x86_64/SwiftBindingsRuntime.framework/SwiftBindingsRuntime'
    targets = root / 'src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets'
    assert runtime.is_file() and native.is_file() and targets.is_file()
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_MULTILEVEL_LOOKUP='0',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    for key in ('DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'MSBuildSDKsPath'):
        env.pop(key, None)
    results = []
    with tempfile.TemporaryDirectory(prefix='swift-net11-runtime-') as temporary:
        scratch = Path(temporary)
        (scratch / 'global.json').write_text(json.dumps({'sdk': {'version': sdks[0].name, 'rollForward': 'disable', 'allowPrerelease': True}}))
        (scratch / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        (scratch / 'Host.csproj').write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
            <IncludeSwiftBindingsRuntimeNative>false</IncludeSwiftBindingsRuntimeNative>
            <UseAppHost>false</UseAppHost><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
          </PropertyGroup>
          <ItemGroup><Reference Include="Swift.Runtime"><HintPath>{escape(str(runtime))}</HintPath></Reference></ItemGroup>
          <Import Project="{escape(str(targets))}" />
        </Project>""")
        (scratch / 'Program.cs').write_text("""
using System.Runtime.CompilerServices;
using System.Text.Json;
using Swift;
using Swift.Runtime;
if (Environment.Version.Major != 11) throw new Exception("Wrong runtime");
foreach (var engine in new[] { "Mono", "CoreCLR", "NativeAOT" })
    if (!AppContext.TryGetSwitch("Swift.Runtime.Engine." + engine, out bool enabled) || enabled != (engine == "CoreCLR"))
        throw new Exception("Engine config transport failed: " + engine);
if (SwiftRuntimeInfo.IsMonoRuntime || SwiftRuntimeInfo.IsNativeAotRuntime) throw new Exception("CoreCLR misclassified");
using var value = new SwiftString("Hello .NET 11 — 🌍");
if (value.ToString() != "Hello .NET 11 — 🌍") throw new Exception("Swift roundtrip failed");
Console.WriteLine(JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(),
    dynamicCode = RuntimeFeature.IsDynamicCodeSupported, mono = SwiftRuntimeInfo.IsMonoRuntime,
    nativeAot = SwiftRuntimeInfo.IsNativeAotRuntime, value = value.ToString() }));
""")
        # Exercise invalid final-app facts through the real candidate SDK as well as
        # the synthetic workload tests. No native compilation or restore is needed.
        invalid = []
        for name, prop in (('mono-aot-conflict', 'UseMonoRuntime=true'),
                           ('suppressed-native-compilation', 'NativeCompilationDuringPublish=false')):
            command = [str(dotnet), 'msbuild', str(scratch / 'Host.csproj'), '--nologo', '-v:quiet',
                       '-t:_SwiftBindingsInjectRuntimeFlavorSwitch', '-p:PublishAot=true',
                       '-p:_IsPublishing=true', '-p:' + prop]
            result = subprocess.run(command, env=env, cwd=scratch, text=True, capture_output=True, timeout=60)
            observed = result.stdout + result.stderr
            (evidence / f'{name}.txt').write_text(observed)
            assert result.returncode != 0 and 'SWIFTBIND126' in observed, observed
            invalid.append({'case': name, 'diagnostic': 'SWIFTBIND126', 'exitCode': result.returncode})
        for policy in ('Safe', 'Direct'):
            output = scratch / policy
            command = [str(dotnet), 'build', str(scratch / 'Host.csproj'), '-c', 'Release', '-o', str(output),
                       '-p:SwiftBindingsInteropMode=' + policy, '--nologo', '-v:quiet']
            build = subprocess.run(command, env=env, cwd=scratch, text=True, capture_output=True, timeout=120)
            (evidence / f'{policy}-build.txt').write_text(build.stdout + build.stderr)
            assert build.returncode == 0, build.stdout + build.stderr
            shutil.copy2(native, output / 'libSwiftBindingsRuntime.dylib')
            config_path = output / 'Host.runtimeconfig.json'
            config = json.loads(config_path.read_text())
            assert 'rollForward' not in config['runtimeOptions'], 'Generator roll-forward leaked to consumer'
            for dynamic_code in (True, False):
                config['runtimeOptions'].setdefault('configProperties', {})['System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported'] = dynamic_code
                config_path.write_text(json.dumps(config))
                result = subprocess.run([str(dotnet), 'exec', str(output / 'Host.dll')], env=env, cwd=output,
                                        text=True, capture_output=True, timeout=60)
                (evidence / f'{policy}-{dynamic_code}.txt').write_text(result.stdout + result.stderr)
                assert result.returncode == 0, result.stdout + result.stderr
                observed = json.loads(result.stdout.strip())
                assert observed['dynamicCode'] == dynamic_code, observed
                results.append({'policy': policy, 'dynamicCodeConfigured': dynamic_code, 'observed': observed,
                                'runtimeconfig': json.loads(config_path.read_text())})
    (evidence / 'runtime-host.json').write_text(json.dumps({'sdk': sdks[0].name, 'results': results, 'invalidConfigurations': invalid,
        'scope': 'desktop CoreCLR; does not qualify mobile interpreter or Apple packaging'}, indent=2) + '\n')
    print('PASS: net11 desktop CoreCLR consumes net10 Runtime, transports engine identity, and round-trips Swift strings under both policies/capability values.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('root', 'dotnet', 'evidence'):
        parser.add_argument('--' + argument, required=True)
    args = parser.parse_args()
    verify(args.root, args.dotnet, args.evidence)
