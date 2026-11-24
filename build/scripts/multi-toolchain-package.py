#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""The multi-toolchain package gate: one binding package, a copy per supported toolchain.

Run through `nuke binding-tests --multi-toolchain-package`, which packs the throwaway
SwiftBindings Runtime, Sdk and Apple feed this script restores from. One entry point drives
the three pairings: each pairing's environment (DOTNET_ROOT, DEVELOPER_DIR, SDK pin) is the
one the toolchain launcher (run-toolchain.py) builds for its profile, validated the way the
launcher validates it, so the pins live only in build/toolchains/<profile>.json.

1. Contributions: each pairing packs two Apple-framework fixtures with its own explicit
   target frameworks. ProximityReader (iOS, Mac Catalyst) has API only the 27.2 SDK
   declares; WeatherKit (macOS, tvOS) covers the other two platforms. It also builds a
   first-party Swift library (multi-toolchain-fixtures/CaseOrder.swift) with its own Xcode and
   packs its binding: the Xcode 27 build inserts enum cases ahead of existing ones, so the
   generated case tags of every copy must not depend on case order.
2. Merge with SwiftBindingsMergePackages; assert the layout, each copy's bytes against its
   contribution, the nuspec groups, SWIFTBIND133 and the Windows path budget.
3. Consumers, from a private NuGet cache per pairing: iOS simulator Debug and Release,
   NativeAOT ios-arm64 publish on the .NET 10 pairings, macOS, tvOS simulator and Mac
   Catalyst apps, and a versionless class library consumed through a ProjectReference.
4. Controls that must go red, each with its own code.
5. Every app runs (simulators, Mac Catalyst and macOS on this host).

Receipts land in --receipts (summary.txt, summary.json, logs/).
"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from xml.sax.saxutils import escape
import zipfile

# (profile, .NET framework, copy platform version). The oldest .NET 10 copy is labelled 26.0:
# a versionless net10.0-<platform> library gets platform version 26.0 on every .NET 10 toolchain.
PAIRINGS = [("net10-xcode26", "net10.0", "26.0"),
            ("net10-xcode27", "net10.0", "27.2"),
            ("net11-xcode27", "net11.0", "27.2")]
FIXTURES = {"ProximityReader": ("ios", "maccatalyst"), "WeatherKit": ("macos", "tvos"), "CaseOrder": ("ios",)}
# Fixtures bound as a library (<SwiftFramework>) from a framework each pairing builds from this Swift source.
LIBRARY_FIXTURES = {"CaseOrder": "multi-toolchain-fixtures/CaseOrder.swift"}
PACKAGE_PREFIX = "MtpFixture."
VERSION = "27.2.0"
# What an app on each platform runs on, and the RID it builds for.
RIDS = {"ios": "iossimulator-arm64", "tvos": "tvossimulator-arm64", "maccatalyst": "maccatalyst-arm64",
        "macos": "osx-arm64"}
SDK_NAMES = {"ios": "iphonesimulator", "tvos": "appletvsimulator", "maccatalyst": "macosx", "macos": "macosx"}
# The simulator runtime each pairing's apps run on: the newest runtime of its Xcode's major.
RUNTIME_MAJOR = {"net10-xcode26": "26", "net10-xcode27": "27", "net11-xcode27": "27"}
# NuGet's restore root on Windows, as the merge budgets it (multi-toolchain-packages.md).
WINDOWS_BUDGET_BASE = 259 - (9 + 40 + 17 + 1)


def load_launcher(root):
    spec = importlib.util.spec_from_file_location("run_toolchain", root / "build/scripts/run-toolchain.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def package_id(framework):
    return PACKAGE_PREFIX + framework


def tfm(net, platform, version):
    return f"{net}-{platform}{version}"


def windows_budget(pid, version):
    return WINDOWS_BUDGET_BASE - len(f"{pid}.{version}")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def read_entries(nupkg):
    with zipfile.ZipFile(nupkg) as z:
        return {i.filename: z.read(i.filename) for i in z.infolist() if not i.filename.endswith("/")}


def copy_tfms(names):
    """The copies a package carries, per layout root: {'lib': {...}, 'buildTransitive': {...}, 'native': {...}}."""
    roots = {"lib": set(), "buildTransitive": set(), "native": set()}
    for name in names:
        parts = name.split("/")
        if len(parts) > 2 and parts[0] in roots:
            roots[parts[0]].add(parts[1])
    return roots


def copy_digest(entries, copy):
    """Every entry of one copy (lib/, buildTransitive/, native/ under the copy's TFM) with its sha256."""
    return {name: sha256(data) for name, data in entries.items()
            if name.split("/")[0] in ("lib", "buildTransitive", "native") and name.split("/")[1:2] == [copy]}


def nuspec_groups(entries):
    """{targetFramework: sorted dependencies}, each dependency its sorted (attribute, value) pairs."""
    nuspec = next(data for name, data in entries.items() if name.endswith(".nuspec") and "/" not in name).decode()
    groups = {}
    for match in re.finditer(r'<group targetFramework="([^"]+)"\s*(?:/>|>(.*?)</group>)', nuspec, re.S):
        if match.group(1) in groups:
            raise ValueError(f"the nuspec has two dependency groups for {match.group(1)}")
        deps = [tuple(sorted(re.findall(r'(\w+)="([^"]*)"', d))) for d in re.findall(r"<dependency\b([^>]*)/?>", match.group(2) or "")]
        groups[match.group(1)] = sorted(deps)
    return groups


def describe_groups(groups, tfms):
    """One receipt line of nuspec_groups(): each copy's dependencies as 'id version'."""
    return "; ".join(f"{t}: " + ", ".join(f"{dict(d).get('id')} {dict(d).get('version')}" for d in groups.get(t, [])) for t in tfms)


def name_keyed_case_id(name):
    """The CaseTag value the generator gives a case of a non-frozen enum (EnumDecl.NameKeyedCaseId)."""
    value = 2166136261
    for b in name.encode():
        value = ((value ^ b) * 16777619) & 0xFFFFFFFF
    return value & 0x7FFFFFFF


def variant(source, out_dir, version, drops=(), copies=(), patches=()):
    """A doctored copy of a package under a new version (controls only).

    copies: (other_nupkg or None, src_prefix, dst_prefix): every entry under dst_prefix is replaced
    by the entries under src_prefix of other_nupkg (or of the package itself).
    patches: (prefix, old_bytes, new_bytes): every occurrence of old_bytes in each entry under prefix
    is replaced; at least one entry must hold it.
    """
    entries = read_entries(source)
    for prefix, old, new in patches:
        hit = [n for n in entries if n.startswith(prefix) and old in entries[n]]
        if not hit:
            raise ValueError(f"variant: no entry under {prefix} of {source} holds the bytes to patch")
        for name in hit:
            entries[name] = entries[name].replace(old, new)
    for other, src, dst in copies:
        pool = read_entries(other) if other else dict(entries)
        for name in [n for n in entries if n.startswith(dst)]:
            del entries[name]
        for name in [n for n in pool if n.startswith(src)]:
            entries[dst + name[len(src):]] = pool[name]
    for prefix in drops:
        dropped = [n for n in entries if n.startswith(prefix)]
        if not dropped:
            raise ValueError(f"variant: nothing under {prefix} in {source}")
        for name in dropped:
            del entries[name]
    entries.pop(".signature.p7s", None)
    pid = None
    for name in list(entries):
        if name.endswith(".nuspec") and "/" not in name:
            text = entries[name].decode()
            pid = re.search(r"<id>([^<]+)</id>", text).group(1)
            entries[name] = re.sub(r"<version>[^<]+</version>", f"<version>{version}</version>", text, count=1).encode()
    out = Path(out_dir) / f"{pid}.{version}.nupkg"
    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries.items():
            z.writestr(name, data)
    return out


def wrapper_identities(binary, module):
    """Every wrapper identity string a Mach-O image holds for module (one per slice)."""
    data = Path(binary).read_bytes()
    marker = f"schema=1;module={module};".encode()
    found, start = [], 0
    while (at := data.find(marker, start)) >= 0:
        end = data.index(b"\0", at)
        found.append(data[at:end].decode())
        start = end
    return found


def identity_field(identity, key):
    for field in identity.split(";"):
        name, _, value = field.partition("=")
        if name == key:
            return value
    return None


def assets_paths(assets_file, pid):
    """{target: (compile entries, build entries)} of package pid in a project.assets.json."""
    data = json.loads(Path(assets_file).read_text())
    result = {}
    for target, libraries in data["targets"].items():
        for key, value in libraries.items():
            if key.lower().startswith(pid.lower() + "/"):
                result[target] = (sorted(value.get("compile", {})), sorted(value.get("build", {})))
    return result


def longest_entry(names):
    return max(names, key=len)


class Gate:
    def __init__(self, options):
        self.o = options
        self.root = Path(options.root).resolve()
        # Not resolved: a link planted at the scratch path must be refused, never followed and emptied.
        self.work = Path(os.path.abspath(options.work))
        self.receipts = Path(options.receipts).resolve()
        self.logs = self.receipts / "logs"
        self.feed = Path(options.feed).resolve()
        self.launcher = load_launcher(self.root)
        self.failures = []
        self.lines = []
        self.record = {"sdkVersion": options.sdk_version, "appleVersion": options.apple_version, "steps": []}
        self.envs, self.receipts_by_profile, self.sdk_versions = {}, {}, {}
        self.created_devices = []
        self.runs = []
        self.started = time.time()

    # ---- infrastructure ----------------------------------------------------------------------

    def say(self, line):
        print(line, flush=True)
        self.lines.append(line)

    def step(self, name, ok, detail=""):
        self.record["steps"].append({"step": name, "ok": ok, "detail": detail})
        self.say(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}")
        if not ok:
            self.failures.append(f"{name}: {detail}")
        return ok

    def prepare(self):
        if self.work.is_symlink() or (self.work.exists() and self.work.stat().st_uid != os.getuid()):
            raise RuntimeError(f"{self.work} is a link or belongs to another user; the gate deletes only a scratch root of its own")
        if self.work.exists():
            shutil.rmtree(self.work)
        self.work.mkdir(parents=True, mode=0o700)
        if self.receipts.exists():
            shutil.rmtree(self.receipts)
        self.logs.mkdir(parents=True)
        for profile, _, _ in PAIRINGS:
            spec = json.loads((self.root / "build/toolchains" / f"{profile}.json").read_text())
            state = self.work / "state" / profile
            for name in ("cli-home", "http-cache", "control"):
                (state / name).mkdir(parents=True, exist_ok=True)
            inherited = {k: v for k, v in os.environ.items()
                         if not k.upper().startswith(("MSBUILD", "NUKE_", "DOTNET_", "NUGET_")) and k != "TMPDIR"}
            env = self.launcher.environment(spec, state, inherited)
            home = self.work / profile
            (home / "tmp").mkdir(parents=True)
            env.update({"TMPDIR": str(home / "tmp") + "/", "NUGET_PACKAGES": str(home / "nuget"),
                        "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
            # Every scratch project of the pairing sits under its own SDK pin.
            self.launcher.write_json(home / "global.json", self.launcher.global_json(spec))
            (home / "NuGet.config").write_text(self.nuget_config())
            # A fresh CLI home records the installed workload packs on its first workload command and
            # says so on stderr; take that into the pairing's log instead of the gate's error output.
            subprocess.run([str(Path(env["DOTNET_ROOT"]) / "dotnet"), "workload", "--info"], cwd=state / "control", env=env,
                           stdout=subprocess.DEVNULL, stderr=(self.logs / f"toolchain-{profile}.log").open("w"))
            receipt = self.launcher.toolchain_receipt(spec, state, env)
            self.envs[profile] = env
            self.receipts_by_profile[profile] = {"sdk": receipt["sdk"], "xcode": receipt["xcode"].splitlines()[0],
                                                 "nativeSdks": {k: v["version"] for k, v in receipt["nativeSdks"].items()}}
            self.sdk_versions[profile] = {k: v["version"] for k, v in receipt["nativeSdks"].items()}
            self.say(f"pairing {profile}: .NET SDK {receipt['sdk']}, {receipt['xcode'].splitlines()[0]}")
        self.record["toolchains"] = self.receipts_by_profile
        (self.work / "contributions").mkdir()
        (self.work / "merged").mkdir()
        (self.work / "controls").mkdir()

    def nuget_config(self):
        sources = [("sdk", self.feed, "SwiftBindings.*"), ("merged", self.work / "merged", PACKAGE_PREFIX + "*"),
                   ("controls", self.work / "controls", PACKAGE_PREFIX + "*")]
        adds = "".join(f'    <add key="{k}" value="{escape(str(p))}" />\n' for k, p, _ in sources)
        maps = "".join(f'    <packageSource key="{k}"><package pattern="{escape(pat)}" /></packageSource>\n'
                       for k, _, pat in sources if pat)
        return ("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n"
                f"{adds}    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />\n  </packageSources>\n"
                f"  <packageSourceMapping>\n{maps}"
                "    <packageSource key=\"nuget.org\"><package pattern=\"*\" /></packageSource>\n"
                "  </packageSourceMapping>\n</configuration>\n")

    def dotnet(self, profile, args, cwd, log, timeout=3600):
        env = self.envs[profile]
        path = self.logs / log
        with path.open("w") as out:
            out.write(f"$ (cd {cwd} && dotnet {' '.join(args)})   [{profile}]\n")
            out.flush()
            try:
                code = subprocess.call([env["DOTNET_HOST_PATH"], *args], cwd=cwd, env=env, stdout=out,
                                       stderr=subprocess.STDOUT, timeout=timeout)
            except subprocess.TimeoutExpired:
                code = -1
                out.write("\nTIMED OUT\n")
        return code, path.read_text(errors="replace")

    def evict(self, profile, pid):
        cached = Path(self.envs[profile]["NUGET_PACKAGES"]) / pid.lower()
        if cached.exists():
            shutil.rmtree(cached)

    # ---- 1. contributions ----------------------------------------------------------------------

    def binding_dir(self, profile, framework):
        return self.work / profile / "bind" / framework

    def write_binding(self, profile, framework):
        directory = self.binding_dir(profile, framework)
        directory.mkdir(parents=True, exist_ok=True)
        item = (f'<SwiftFramework Include="{escape(str(self.library_xcframework(profile, framework)))}" />' if framework in LIBRARY_FIXTURES
                else f'<SwiftAppleFrameworkTarget Include="{framework}" />')
        (directory / f"{package_id(framework)}.csproj").write_text(f"""<Project Sdk="SwiftBindings.Sdk/{self.o.sdk_version}">
  <PropertyGroup>
    <TargetFramework />
    <TargetFrameworks>$(MtpTargetFrameworks)</TargetFrameworks>
    <PackageId>{package_id(framework)}</PackageId>
    <Version>{VERSION}</Version>
    <Authors>SwiftBindings gate</Authors>
    <Description>Multi-toolchain package gate fixture: a binding of {framework} (throwaway).</Description>
  </PropertyGroup>
  <ItemGroup>
    {item}
  </ItemGroup>
</Project>
""")
        return directory / f"{package_id(framework)}.csproj"

    def library_xcframework(self, profile, framework):
        return self.work / profile / "frameworks" / f"{framework}.xcframework"

    def build_library(self, profile, framework):
        """Builds a library fixture's device and simulator framework with the pairing's own Xcode, as its vendor would."""
        env = self.envs[profile]
        source = Path(__file__).resolve().parent / LIBRARY_FIXTURES[framework]
        root = self.work / profile / "frameworks"
        log = (self.logs / f"framework-{profile}-{framework}.log").open("w")
        slices = []
        for sdk, triple, suffix, platform in (("iphoneos", "arm64-apple-ios17.0", "arm64-apple-ios", "iPhoneOS"),
                                              ("iphonesimulator", "arm64-apple-ios17.0-simulator", "arm64-apple-ios-simulator", "iPhoneSimulator")):
            fw = root / sdk / f"{framework}.framework"
            modules = fw / "Modules" / f"{framework}.swiftmodule"
            modules.mkdir(parents=True)
            sdk_path = subprocess.check_output(["xcrun", "--sdk", sdk, "--show-sdk-path"], env=env, text=True).strip()
            steps = [
                ["xcrun", "swiftc", "-emit-library", "-emit-module", "-enable-library-evolution", "-emit-module-interface",
                 "-target", triple, "-sdk", sdk_path, "-module-name", framework, "-o", str(fw / framework),
                 "-emit-module-path", str(modules / f"{suffix}.swiftmodule"),
                 "-emit-module-interface-path", str(modules / f"{suffix}.swiftinterface"),
                 "-Xlinker", "-install_name", "-Xlinker", f"@rpath/{framework}.framework/{framework}", str(source)],
                ["xcrun", "swift-frontend", "-compile-module-from-interface", str(modules / f"{suffix}.swiftinterface"),
                 "-target", triple, "-module-name", framework, "-sdk", sdk_path,
                 "-emit-abi-descriptor-path", str(modules / f"{suffix}.abi.json"), "-o", str(root / sdk / f"{suffix}.swiftmodule")],
            ]
            for args in steps:
                log.write("$ " + " ".join(args) + "\n")
                log.flush()
                if subprocess.call(args, env=env, stdout=log, stderr=subprocess.STDOUT) != 0:
                    return False
            shutil.copy2(modules / f"{suffix}.swiftinterface", modules / f"{suffix}.private.swiftinterface")
            (fw / "Info.plist").write_text(FRAMEWORK_INFO_PLIST.format(name=framework, platform=platform))
            slices += ["-framework", str(fw)]
        args = ["xcrun", "xcodebuild", "-create-xcframework", *slices, "-output", str(self.library_xcframework(profile, framework))]
        log.write("$ " + " ".join(args) + "\n")
        log.flush()
        return subprocess.call(args, env=env, stdout=log, stderr=subprocess.STDOUT) == 0

    def pack(self, profile, framework, tfms, version, out, log):
        project = self.write_binding(profile, framework)
        for name in ("bin", "obj"):
            shutil.rmtree(project.parent / name, ignore_errors=True)
        return self.dotnet(profile, ["pack", str(project), "-c", "Release", f"-p:MtpTargetFrameworks=\"{';'.join(tfms)}\"",
                                     f"-p:Version={version}", "-o", str(out), "-nodeReuse:false"], project.parent, log)

    def contributions(self):
        for profile, net, label in PAIRINGS:
            for framework, platforms in FIXTURES.items():
                tfms = [tfm(net, p, label) for p in platforms]
                if framework in LIBRARY_FIXTURES and not self.library_xcframework(profile, framework).exists() \
                        and not self.step(f"{profile} builds the {framework} framework", self.build_library(profile, framework),
                                          f"see framework-{profile}-{framework}.log"):
                    continue
                out = self.work / "contributions" / profile
                code, text = self.pack(profile, framework, tfms, VERSION, out, f"pack-{profile}-{framework}.log")
                nupkg = out / f"{package_id(framework)}.{VERSION}.nupkg"
                ok = code == 0 and nupkg.exists()
                if ok:
                    roots = copy_tfms(read_entries(nupkg))
                    ok = all(roots[r] == set(tfms) for r in roots)
                    detail = f"{', '.join(tfms)}" if ok else f"layout {roots}, expected {tfms}"
                else:
                    detail = f"exit {code}; {first_errors(text)}"
                self.step(f"contribution {profile} {framework}", ok, detail)

    def contribution(self, profile, framework):
        return self.work / "contributions" / profile / f"{package_id(framework)}.{VERSION}.nupkg"

    # ---- 2. merge ------------------------------------------------------------------------------

    def merge(self, framework, parts, version, out, log, strict=False):
        """parts: [(nupkg, [tfms])]. Runs on .NET 10 + Xcode 26.3 from the binding's own project."""
        profile = PAIRINGS[0][0]
        project = self.write_binding(profile, framework)
        oldest = [tfm(PAIRINGS[0][1], p, PAIRINGS[0][2]) for p in FIXTURES[framework]]
        Path(out).mkdir(parents=True, exist_ok=True)
        args = ["msbuild", "-restore", str(project), "-t:SwiftBindingsMergePackages", "-nodeReuse:false",
                f"-p:MtpTargetFrameworks=\"{';'.join(oldest)}\"", f"-p:Version={version}",
                f"-p:SwiftBindingsMergeContributions=\"{';'.join(str(n) for n, _ in parts)}\"",
                f"-p:SwiftBindingsMergeTargetFrameworks=\"{';'.join(t for _, ts in parts for t in ts)}\"",
                f"-p:SwiftBindingsMergeOutput={out}"]
        if strict:
            args.append("-p:SwiftBindingsMergeRequireWindowsPathBudget=true")
        return self.dotnet(profile, args, project.parent, log)

    def matrix(self, framework):
        return [(self.contribution(p, framework), [tfm(net, pl, label) for pl in FIXTURES[framework]])
                for p, net, label in PAIRINGS]

    def merged(self, framework):
        return self.work / "merged" / f"{package_id(framework)}.{VERSION}.nupkg"

    def merges(self):
        for framework in FIXTURES:
            parts = self.matrix(framework)
            expected = [t for _, ts in parts for t in ts]
            code, text = self.merge(framework, parts, VERSION, self.work / "merged", f"merge-{framework}.log")
            nupkg = self.merged(framework)
            if not self.step(f"merge {framework}", code == 0 and nupkg.exists(), f"exit {code}; {first_errors(text)}"
                             if code else ", ".join(expected)):
                continue
            entries = read_entries(nupkg)
            roots = copy_tfms(entries)
            self.step(f"merge {framework} layout", all(roots[r] == set(expected) for r in roots),
                      f"lib {sorted(roots['lib'])}, buildTransitive {sorted(roots['buildTransitive'])}, native {sorted(roots['native'])}")
            stray = sorted({n.split("/")[0] for n in entries} - {"lib", "buildTransitive", "native", "_rels", "package",
                                                                  "[Content_Types].xml", f"{package_id(framework)}.nuspec",
                                                                  "README.md", "readme.md"})
            self.step(f"merge {framework} has nothing outside the copy layout", not stray, ", ".join(stray) or "none")
            for contribution, tfms in parts:
                source = read_entries(contribution)
                for t in tfms:
                    mine, theirs = copy_digest(entries, t), copy_digest(source, t)
                    differ = sorted(n for n in set(mine) | set(theirs) if mine.get(n) != theirs.get(n))
                    self.step(f"merge {framework} copy {t} is its contribution's bytes", not differ and bool(mine),
                              f"{len(mine)} entries" if not differ else f"differ: {', '.join(differ[:4])}")
            groups, source_groups = nuspec_groups(entries), {}
            for contribution, _ in parts:
                source_groups.update(nuspec_groups(read_entries(contribution)))
            self.step(f"merge {framework} nuspec groups", set(groups) == set(expected)
                      and all(groups[t] == source_groups.get(t) for t in expected),
                      describe_groups(groups, expected))
            names = list(entries)
            longest = longest_entry(names)
            budget = windows_budget(package_id(framework), VERSION)
            over = len(longest) > budget
            warned = re.search(r"warning SWIFTBIND140", text) is not None
            self.step(f"merge {framework} Windows path budget", warned == over,
                      f"longest entry {len(longest)} ({longest}) against a budget of {budget}: "
                      f"{'over, SWIFTBIND140 warned' if over and warned else 'within, no warning' if not over and not warned else 'SWIFTBIND140 ' + ('missing' if over else 'unexpected')}")
            self.record.setdefault("pathBudget", {})[framework] = {"longest": len(longest), "entry": longest, "budget": budget}
            # SWIFTBIND133 on the merged package, read again by the SDK's own task outside the merge.
            code, text = self.package_check(nupkg, f"check133-{framework}.log")
            self.step(f"merge {framework} SWIFTBIND133", code == 0, "the pack-time transition check accepts the merged package"
                      if code == 0 else first_errors(text))

    def package_check(self, nupkg, log):
        """Runs SWIFTBIND133 (SwiftTransitionPackageCheck, from the SDK package) on a copy of nupkg."""
        profile = PAIRINGS[0][0]
        cache = Path(self.envs[profile]["NUGET_PACKAGES"])
        tasks = cache / "swiftbindings.sdk" / self.o.sdk_version.lower() / "Sdk" / "tasks" / "net10.0" / "SwiftBindings.Tasks.dll"
        runtime = cache / "swiftbindings.runtime" / self.o.sdk_version.lower() / "lib"
        references = sorted(str(d) for d in runtime.iterdir()) if runtime.is_dir() else []
        scratch = self.work / profile / "check" / Path(log).stem
        if scratch.exists():
            shutil.rmtree(scratch)
        scratch.mkdir(parents=True)
        subject = scratch / Path(nupkg).name
        shutil.copy2(nupkg, subject)
        (scratch / "check.proj").write_text(f"""<Project>
  <UsingTask TaskName="SwiftBindings.Tasks.SwiftTransitionPackageCheck" AssemblyFile="{escape(str(tasks))}" />
  <Target Name="Check">
    <Error Condition="!Exists('{escape(str(tasks))}')" Text="The SDK's task assembly is missing: {escape(str(tasks))}" />
    <SwiftTransitionPackageCheck Packages="{escape(str(subject))}" ReferenceDirectories="{escape(';'.join(references))}"
                                 ScratchDirectory="{escape(str(scratch / 'scratch'))}" />
  </Target>
</Project>
""")
        return self.dotnet(profile, ["msbuild", str(scratch / "check.proj"), "-t:Check", "-nodeReuse:false"], scratch, log)

    # ---- 3. consumers ----------------------------------------------------------------------------

    def app_dir(self, profile, name):
        directory = self.work / profile / "apps" / name
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)
        return directory

    def write_app(self, directory, profile, platform, framework, version, expected_copy, probe27, extra_props="",
                  extra_items="", reference=None, assembly="MtpApp"):
        net = dict((p, n) for p, n, _ in PAIRINGS)[profile]
        bundle = f"com.swiftbindings.mtp.{assembly.lower()}"
        package = (f'<PackageReference Include="{package_id(framework)}" Version="[{version}]" />' if reference is None
                   else f'<ProjectReference Include="{escape(str(reference))}" />')
        (directory / f"{assembly}.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{net}-{platform}</TargetFramework>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>true</ImplicitUsings>
    <SupportedOSPlatformVersion>{ {"ios": "17.0", "tvos": "17.0", "maccatalyst": "17.0", "macos": "14.0"}[platform] }</SupportedOSPlatformVersion>
    <ApplicationId>{bundle}</ApplicationId>
    <ApplicationTitle>{assembly}</ApplicationTitle>
    <RuntimeIdentifier>{RIDS[platform]}</RuntimeIdentifier>
    <DefineConstants Condition="'{probe27}' == 'True'">$(DefineConstants);MTP_PROBE27</DefineConstants>
    {extra_props}
  </PropertyGroup>
  <ItemGroup>
    {package}
    {extra_items}
  </ItemGroup>
</Project>
""")
        (directory / "Main.cs").write_text(app_source(platform, framework, expected_copy, reference is not None))
        if platform != "macos":
            # An app built with the 27 SDK must adopt the scene lifecycle (MT7199 at build, a trap at launch).
            (directory / "Info.plist").write_text(SCENE_INFO_PLIST)
        return directory / f"{assembly}.csproj"

    def consumer(self, profile, platform, framework, configuration, expected_copy, probe27, label, publish_aot=False,
                 version=VERSION, run=True):
        directory = self.app_dir(profile, label)
        project = self.write_app(directory, profile, platform, framework, version, expected_copy, probe27)
        self.evict(profile, package_id(framework))
        if publish_aot:
            args = ["publish", str(project), "-c", "Release", "-r", "ios-arm64", "-p:RuntimeIdentifier=ios-arm64",
                    "-p:PublishAot=true", "-p:PublishAotUsingRuntimePack=true", "-p:EnableCodeSigning=false",
                    "-nodeReuse:false"]
        else:
            args = ["build", str(project), "-c", configuration, "-nodeReuse:false"]
        code, text = self.dotnet(profile, args, directory, f"app-{profile}-{label}.log")
        if not self.step(f"{profile} {label} builds", code == 0, f"exit {code}; {first_errors(text)}" if code else ""):
            return
        self.assert_selection(profile, label, directory, text, framework, expected_copy, platform,
                              check132=not publish_aot and platform != "macos", configuration="Release" if publish_aot else configuration,
                              rid="ios-arm64" if publish_aot else RIDS[platform])
        if run and not publish_aot:
            self.runs.append((profile, label, platform, self.app_bundle(directory, configuration, platform)))

    def app_bundle(self, directory, configuration, platform, rid=None):
        bundles = sorted((directory / "bin" / configuration).glob(f"*/{rid or RIDS[platform]}/*.app"))
        return bundles[0] if bundles else None

    def assert_selection(self, profile, label, directory, text, framework, expected_copy, platform, check132, configuration, rid):
        pid = package_id(framework)
        # A referenced project builds first and prints its own line; the app's is the last.
        lines = copy_lines(text, pid)
        line = lines[-1] if lines else None
        self.step(f"{profile} {label} copy line", line is not None and line[0] == expected_copy,
                  f"{line[0]} (built on {line[1]})" if line else "no 'SwiftBindings copy' line in the build log")
        assets = assets_paths(directory / "obj" / "project.assets.json", pid)
        bad = [f"{t}: {c} {b}" for t, (c, b) in assets.items()
               if not c or not all(x.startswith(f"lib/{expected_copy}/") for x in c)
               or not b or not all(x.startswith(f"buildTransitive/{expected_copy}/") for x in b)]
        self.step(f"{profile} {label} project.assets.json paths", bool(assets) and not bad,
                  "; ".join(bad) if bad else f"lib/{expected_copy}/, buildTransitive/{expected_copy}/ for {', '.join(sorted(assets))}")
        bundle = self.app_bundle(directory, configuration, platform, rid)
        self.assert_wrapper(profile, label, bundle, framework, expected_copy, platform)
        if check132:
            caches = sorted(directory.glob("obj/**/swiftbindings-transition-check.json"))
            verdicts = []
            for cache in caches:
                results = json.loads(cache.read_text()).get("Results", {})
                verdicts += [f"{v['Verdict']} ({v.get('State')})" for v in results.values() if v.get("Name") == pid]
            self.step(f"{profile} {label} SWIFTBIND132 check", bool(verdicts) and all(v.startswith("Verified") for v in verdicts),
                      ", ".join(verdicts) or f"no verdict for {pid} in {len(caches)} cache(s)")

    def assert_wrapper(self, profile, label, bundle, framework, expected_copy, platform):
        module = framework
        wrapper = f"{module}SwiftBindings"
        images = [p for p in (bundle.rglob(wrapper) if bundle else []) if p.is_file() and not p.is_symlink()]
        if not self.step(f"{profile} {label} embeds the wrapper", len(images) == 1,
                         f"{len(images)} image(s) named {wrapper} in {bundle}" if len(images) != 1 else str(images[0].relative_to(bundle))):
            return
        image = images[0]
        identities = wrapper_identities(image, module)
        copies = {identity_field(i, "copy") for i in identities}
        sdks = {identity_field(i, "sdk") for i in identities}
        build = subprocess.run(["vtool", "-show-build", str(image)], capture_output=True, text=True).stdout
        linked = set(re.findall(r"^\s*sdk (\S+)", build, re.M))
        # A Catalyst slice links the macOS SDK and records its iOS-equivalent SDK version.
        names = ("macosx", "iphonesimulator") if platform == "maccatalyst" else (SDK_NAMES[platform],)
        expected = {minor_version(self.sdk_versions[profile][n]) for n in names}
        self.step(f"{profile} {label} wrapper identity and SDK",
                  bool(identities) and copies == {expected_copy} and len(sdks) == 1 and bool(linked)
                  and all(s is not None and minor_version(s) in expected for s in sdks | linked),
                  f"identity copy={','.join(sorted(c or '?' for c in copies))} sdk={','.join(sorted(s or '?' for s in sdks))}; "
                  f"linked against SDK {','.join(sorted(linked)) or '?'} (pairing SDK {', '.join(sorted(expected))})")

    def library_flow(self, profile, net, label):
        """A versionless class library restores the package; the app that references it selects its own copy."""
        lib = self.app_dir(profile, "lib")
        (lib / "MtpLib.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{net}-ios</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="{package_id('ProximityReader')}" Version="[{VERSION}]" />
    <PackageReference Include="{package_id('CaseOrder')}" Version="[{VERSION}]" />
  </ItemGroup>
</Project>
""")
        (lib / "LibUse.cs").write_text(LIBRARY_SOURCE)
        app_copy = tfm(net, "ios", label)
        lib_copy = tfm(net, "ios", "26.0" if net == "net10.0" else label)
        app = self.app_dir(profile, "lib-app")
        project = self.write_app(app, profile, "ios", "ProximityReader", VERSION, app_copy, probe27=label == "27.2",
                                 reference=lib / "MtpLib.csproj", assembly="MtpLibApp")
        self.evict(profile, package_id("ProximityReader"))
        self.evict(profile, package_id("CaseOrder"))
        code, text = self.dotnet(profile, ["build", str(project), "-c", "Debug", "-nodeReuse:false"], app, f"app-{profile}-lib-app.log")
        if not self.step(f"{profile} library through a ProjectReference builds", code == 0, f"exit {code}; {first_errors(text)}" if code else ""):
            return
        lines = copy_lines(text, package_id("ProximityReader"))
        self.step(f"{profile} versionless library's own build selects the {lib_copy} copy",
                  len(lines) == 2 and lines[0][0] == lib_copy, "; ".join(f"{c} (built on {b})" for c, b in lines) or "no copy line")
        assets = assets_paths(lib / "obj" / "project.assets.json", package_id("ProximityReader"))
        bad = [f"{t}: {c}" for t, (c, _) in assets.items() if not c or not all(x.startswith(f"lib/{lib_copy}/") for x in c)]
        self.step(f"{profile} versionless library restores the {lib_copy} copy", bool(assets) and not bad,
                  "; ".join(bad) or f"{', '.join(sorted(assets))}")
        self.assert_selection(profile, "lib-app", app, text, "ProximityReader", app_copy, "ios", check132=True,
                              configuration="Debug", rid=RIDS["ios"])
        # The library compiled its CaseOrder calls, case tags included, against the oldest copy of its
        # .NET version; the app runs its own copy, whose Swift library inserted cases on Xcode 27.
        lines = copy_lines(text, package_id("CaseOrder"))
        self.step(f"{profile} versionless library's CaseOrder copy is {lib_copy}, the app's {app_copy}",
                  len(lines) == 2 and lines[0][0] == lib_copy and lines[1][0] == app_copy,
                  "; ".join(f"{c} (built on {b})" for c, b in lines) or "no copy line")
        self.assert_selection(profile, "lib-app CaseOrder", app, text, "CaseOrder", app_copy, "ios", check132=True,
                              configuration="Debug", rid=RIDS["ios"])
        self.runs.append((profile, "lib-app", "ios", self.app_bundle(app, "Debug", "ios")))

    def consumers(self):
        for profile, net, label in PAIRINGS:
            probe27 = label == "27.2"
            ios = tfm(net, "ios", label)
            for configuration in ("Debug", "Release"):
                self.consumer(profile, "ios", "ProximityReader", configuration, ios, probe27, f"ios-{configuration.lower()}")
            if net == "net10.0":
                self.consumer(profile, "ios", "ProximityReader", "Release", ios, probe27, "ios-nativeaot", publish_aot=True)
            self.consumer(profile, "maccatalyst", "ProximityReader", "Debug", tfm(net, "maccatalyst", label), probe27, "maccatalyst")
            self.consumer(profile, "macos", "WeatherKit", "Debug", tfm(net, "macos", label), False, "macos")
            self.consumer(profile, "tvos", "WeatherKit", "Debug", tfm(net, "tvos", label), False, "tvos")
            self.library_flow(profile, net, label)

    # ---- 4. controls ---------------------------------------------------------------------------

    def control(self, name, code, text, expect):
        """A control passes the gate only when its run completed and failed (not a timeout, -1) with
        the expected diagnostic: every pattern on one line, so two diagnostics cannot make up one."""
        line = next((l for l in text.splitlines() if all(re.search(m, l) for m in expect)), None)
        self.step(f"control {name} is red", code > 0 and line is not None,
                  f"exit {code}; " + (line.strip()[:400] if line else f"no line matches all of {', '.join(expect)}"))

    def control_app(self, profile, label, version, extra_props="", extra_items="", expected_copy=None):
        directory = self.app_dir(profile, f"control-{label}")
        project = self.write_app(directory, profile, "ios", "ProximityReader", version, expected_copy or "", probe27=False,
                                 extra_props=extra_props, extra_items=extra_items)
        self.evict(profile, package_id("ProximityReader"))
        return self.dotnet(profile, ["build", str(project), "-c", "Debug", "-nodeReuse:false"], directory, f"control-{label}.log")

    def controls(self):
        pr = "ProximityReader"
        pid = package_id(pr)
        merged = self.merged(pr)
        controls = self.work / "controls"
        x26, x27, n11 = (p for p, _, _ in PAIRINGS)
        parts = dict((p, (n, t)) for p, (n, t) in zip((x26, x27, n11), self.matrix(pr)))

        # NU1202: a package without the oldest .NET 10 copy cannot be restored on .NET 10 + Xcode 26.3.
        code, text = self.merge(pr, [parts[x27], parts[n11]], VERSION, self.work / "partial-a", "control-merge-nega.log")
        if self.step("control NU1202 setup: merge without the 26.0 copy", code == 0, first_errors(text) if code else ""):
            variant(self.work / "partial-a" / merged.name, controls, f"{VERSION}-nega")
            code, text = self.control_app(x26, "nu1202", f"{VERSION}-nega")
            self.control("NU1202 (oldest .NET 10 copy missing)", code, text, [rf"error NU1202: Package {re.escape(pid)} {VERSION}-nega is not compatible"])

        # SWIFTBIND136: buildTransitive/<27.2>/ dropped, so NuGet takes the 26.0 targets beside the 27.2 lib.
        # SWIFTBIND132 is suppressed, as it would otherwise refuse the 26.0 natives first.
        variant(merged, controls, f"{VERSION}-negb", drops=["buildTransitive/net10.0-ios27.2/"])
        code, text = self.control_app(x27, "swiftbind136", f"{VERSION}-negb",
                                      extra_items=f'<SwiftBindingsTransitionSuppress Include="{pid}" />')
        self.control("SWIFTBIND136 (buildTransitive copy dropped, 132 suppressed)", code, text,
                     [r"error SWIFTBIND136: .*lib/net10\.0-ios27\.2/', but NuGet chose the package's net10\.0-ios26\.0 consumer targets"])

        # SWIFTBIND132 and SWIFTBIND133: the 26.0 copy's wrapper in the 27.2 copy's native/ (NEG-C).
        negc = variant(merged, controls, f"{VERSION}-negc", copies=[(None, "native/net10.0-ios26.0/", "native/net10.0-ios27.2/")])
        code, text = self.control_app(x27, "swiftbind132", f"{VERSION}-negc")
        self.control("SWIFTBIND132 (wrapper swapped between copies)", code, text,
                     [r"error SWIFTBIND132: .*is not the wrapper ProximityReader was generated with: it was generated as copy net10\.0-ios26\.0"])
        code, text = self.package_check(negc, "control-swiftbind133.log")
        self.control("SWIFTBIND133 (wrapper swapped between copies)", code, text,
                     [r"error SWIFTBIND133", r"lib/net10\.0-ios27\.2/\S+ would be refused", r"generated as copy net10\.0-ios26\.0"])

        # SWIFTBIND137: Xcode 27.2 pack of an Apple framework under the versionless (26.0) label.
        out = self.work / "mislabel"
        code, text = self.pack(x27, pr, ["net10.0-ios"], f"{VERSION}-mislabel", out, "control-swiftbind137.log")
        self.control("SWIFTBIND137 (mislabelled contribution)", code if not list(out.glob("*.nupkg")) else 0, text,
                     [r"error SWIFTBIND137: .*labelled ios 26\.0, but its API surface and wrapper were generated from the ios 27\.2 SDK"])

        # SWIFTBIND139: no .NET 11 copy -> the .NET 11 app uses the .NET 10 copy with a message; an error on request.
        code, text = self.merge(pr, [parts[x26], parts[x27]], VERSION, self.work / "partial-d", "control-merge-negd.log")
        if self.step("control SWIFTBIND139 setup: merge without the .NET 11 copy", code == 0, first_errors(text) if code else ""):
            variant(self.work / "partial-d" / merged.name, controls, f"{VERSION}-negd")
            code, text = self.control_app(n11, "swiftbind139-message", f"{VERSION}-negd")
            self.step("SWIFTBIND139 message without the exact-copy opt-in", code == 0 and re.search(
                r"message SWIFTBIND139: .*uses the package's net10\.0-ios27\.2 copy", text) is not None,
                f"exit {code}; {'message present' if 'SWIFTBIND139' in text else 'no SWIFTBIND139 message'}")
            code, text = self.control_app(n11, "swiftbind139", f"{VERSION}-negd",
                                          extra_props="<SwiftBindingsRequireExactCopy>true</SwiftBindingsRequireExactCopy>")
            self.control("SWIFTBIND139 (SwiftBindingsRequireExactCopy=true)", code, text,
                         [r"error SWIFTBIND139: .*built for \.NET 10\.0 rather than this build's \.NET 11\.0"])

        # SWIFTBIND140 in strict mode: the real package is past the Windows restore budget.
        out = self.work / "strict"
        code, text = self.merge(pr, self.matrix(pr), VERSION, out, "control-swiftbind140.log", strict=True)
        self.control("SWIFTBIND140 (SwiftBindingsMergeRequireWindowsPathBudget=true)", code if not list(out.glob("*.nupkg")) else 0,
                     text, [r"error SWIFTBIND140"])

        # SWIFTBIND138: the merge's refusals (M1-M7).
        c26, c27 = parts[x26][0], parts[x27][0]
        doctored = self.work / "doctored"
        rev = variant(c27, doctored / "m3", f"{VERSION}-other")
        nobt = variant(c27, doctored / "m4", VERSION, drops=["buildTransitive/net10.0-ios27.2/"])
        swap26 = variant(c26, doctored / "m5a", VERSION, copies=[(c27, "lib/net10.0-ios27.2/", "lib/net10.0-ios26.0/")])
        swap27 = variant(c27, doctored / "m5b", VERSION, copies=[(c26, "lib/net10.0-ios26.0/", "lib/net10.0-ios27.2/")])
        nswap26 = variant(c26, doctored / "m6a", VERSION, copies=[(c27, "native/net10.0-ios27.2/", "native/net10.0-ios26.0/")])
        nswap27 = variant(c27, doctored / "m6b", VERSION, copies=[(c26, "native/net10.0-ios26.0/", "native/net10.0-ios27.2/")])
        cat26, cat27 = parts[x26][1], parts[x27][1]
        cases = [
            ("M1 a copy carried by two contributions", [parts[x26], parts[x27], parts[n11], parts[n11]],
             [r"error SWIFTBIND138: net11\.0-ios27\.2 is carried by both"]),
            ("M2 a matrix TFM no contribution carries", [(c26, cat26), (c27, cat27 + ["net11.0-ios27.2"])],
             [r"error SWIFTBIND138: No contribution carries the expected copy net11\.0-ios27\.2"]),
            ("M3 a contribution of another version", [(c26, cat26), (rev, cat27)], [r"error SWIFTBIND138: .*is \S+ 27\.2\.0-other, but .*every contribution must be the same package id and version"]),
            ("M4 lib/ and buildTransitive/ copies differ", [(c26, cat26), (nobt, cat27)],
             [r"error SWIFTBIND138: .*NuGet selects lib/ and buildTransitive/ separately"]),
            ("M5 a newer copy lacks an older copy's API", [(swap26, cat26), (swap27, cat27)],
             [r"error SWIFTBIND138: .*the copy net10\.0-ios27\.2 lacks \d+ public API member\(s\) of the older copy net10\.0-ios26\.0"]),
            ("M6 a newer copy's natives lack an older copy's exports", [(nswap26, cat26), (nswap27, cat27)],
             [r"error SWIFTBIND138: native/net10\.0-ios27\.2/\S+ \(\w+\) lacks \d+ export\(s\) of the older copy net10\.0-ios26\.0"]),
        ]
        for name, case_parts, expect in cases:
            out = self.work / "merge-controls" / name.split()[0]
            code, text = self.merge(pr, case_parts, VERSION, out, f"control-{name.split()[0].lower()}.log")
            self.control(f"SWIFTBIND138 {name}", code if not list(out.glob("*.nupkg")) else 0, text, expect)

        # M7: the CaseOrder 27.2 copy with one CaseTag constant renumbered, as a binding whose case
        # values followed the SDK's case order would be. API and exports are unchanged, so only the
        # compiled-in value check can refuse it.
        (co26, cot26), (co27, cot27), _ = self.matrix("CaseOrder")
        solid = name_keyed_case_id("solid")
        renumbered = variant(co27, doctored / "m7", VERSION,
                             patches=[("lib/net10.0-ios27.2/", solid.to_bytes(4, "little"), (solid ^ 0x40000000).to_bytes(4, "little"))])
        out = self.work / "merge-controls" / "M7"
        code, text = self.merge("CaseOrder", [(co26, cot26), (renumbered, cot27)], VERSION, out, "control-m7.log")
        self.control("SWIFTBIND138 M7 a newer copy changes an older copy's compiled-in value", code if not list(out.glob("*.nupkg")) else 0, text,
                     [rf"error SWIFTBIND138: .*the copy net10\.0-ios27\.2 changes \d+ compiled-in value\(s\) of the older copy net10\.0-ios26\.0 "
                      rf"\(.*CaseOrder\.Paint\+CaseTag::Solid is {solid} in net10\.0-ios26\.0 and {solid ^ 0x40000000} in net10\.0-ios27\.2"])

    # ---- 5. runs -------------------------------------------------------------------------------

    def simulator(self, platform, major):
        kind = "iOS" if platform == "ios" else "tvOS"
        listing = json.loads(subprocess.check_output(["xcrun", "simctl", "list", "-j", "runtimes"]))
        runtimes = sorted((r for r in listing["runtimes"] if r.get("isAvailable") and r["platform"] == kind
                           and r["version"].split(".")[0] == major), key=lambda r: [int(x) for x in r["version"].split(".")])
        if not runtimes:
            raise RuntimeError(f"No available {kind} {major}.x simulator runtime")
        runtime = runtimes[-1]
        device_type = next(t["identifier"] for t in reversed(runtime["supportedDeviceTypes"])
                           if (t["productFamily"] == "iPhone" if kind == "iOS" else t["productFamily"] == "Apple TV"))
        name = f"swiftbindings-mtp-{kind}-{runtime['version']}"
        udid = subprocess.check_output(["xcrun", "simctl", "create", name, device_type, runtime["identifier"]], text=True).strip()
        self.created_devices.append(udid)
        return udid, f"{kind} {runtime['version']}"

    def run_all(self):
        try:
            self.run_each()
        finally:
            for udid in self.created_devices:
                subprocess.run(["xcrun", "simctl", "shutdown", udid], capture_output=True)
                subprocess.run(["xcrun", "simctl", "delete", udid], capture_output=True)

    def run_each(self):
        devices = {}
        for profile, label, platform, bundle in self.runs:
            if bundle is None:
                self.step(f"{profile} {label} runs", False, "no app bundle")
                continue
            log = self.logs / f"run-{profile}-{label}.log"
            if platform in ("macos", "maccatalyst"):
                executable = bundle / "Contents" / "MacOS" / bundle.stem
                try:
                    proc = subprocess.run([str(executable)], capture_output=True, text=True, timeout=180)
                    output, code = proc.stdout + proc.stderr, proc.returncode
                except subprocess.TimeoutExpired as e:
                    output, code = (e.stdout or b"").decode(errors="replace") if isinstance(e.stdout, bytes) else (e.stdout or ""), -1
                where = "this Mac"
            else:
                key = (platform, RUNTIME_MAJOR[profile])
                if key not in devices:
                    devices[key] = self.simulator(*key)
                udid, where = devices[key]
                output, code = simulator_run(udid, bundle)
            log.write_text(output)
            passed = "MTP RESULT PASS" in output and "MTP RESULT FAIL" not in output and "MTP FAIL" not in output
            verdict = list(dict.fromkeys(l.split("MTP ", 1)[1] for l in output.splitlines() if "MTP FAIL" in l or "MTP RESULT" in l))
            self.step(f"{profile} {label} runs", passed and code == 0,
                      f"on {where}: exit {code}; " + ("; ".join(verdict) or "no result line"))

    # ---- driver --------------------------------------------------------------------------------

    def finish(self):
        minutes = (time.time() - self.started) / 60
        self.record["minutes"] = round(minutes, 1)
        self.record["failures"] = list(self.failures)
        verdict = "PASSED" if not self.failures else f"FAILED ({len(self.failures)})"
        self.say(f"multi-toolchain-package {verdict} in {minutes:.1f} min")
        self.receipts.mkdir(parents=True, exist_ok=True)
        (self.receipts / "summary.txt").write_text("\n".join(self.lines) + "\n")
        (self.receipts / "summary.json").write_text(json.dumps(self.record, indent=2) + "\n")
        return not self.failures


def simulator_run(udid, bundle):
    bundle_id = subprocess.check_output(["/usr/libexec/PlistBuddy", "-c", "Print :CFBundleIdentifier", str(bundle / "Info.plist")],
                                        text=True).strip()
    subprocess.run(["xcrun", "simctl", "boot", udid], capture_output=True)
    subprocess.run(["xcrun", "simctl", "bootstatus", udid, "-b"], capture_output=True, check=True)
    subprocess.run(["xcrun", "simctl", "uninstall", udid, bundle_id], capture_output=True)
    install = subprocess.run(["xcrun", "simctl", "install", udid, str(bundle)], capture_output=True, text=True)
    if install.returncode != 0:
        return f"install failed: {install.stderr}", install.returncode
    try:
        proc = subprocess.run(["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", udid, bundle_id],
                              capture_output=True, text=True, timeout=240)
        output, code = (proc.stdout + proc.stderr).replace("\r", ""), proc.returncode
    except subprocess.TimeoutExpired as e:
        raw = e.stdout or ""
        output, code = (raw.decode(errors="replace") if isinstance(raw, bytes) else raw).replace("\r", "") + "\nTIMED OUT", -1
        subprocess.run(["xcrun", "simctl", "terminate", udid, bundle_id], capture_output=True)
    container = subprocess.run(["xcrun", "simctl", "get_app_container", udid, bundle_id, "data"], capture_output=True, text=True)
    # MyDocuments is <container>/Documents on iOS and <container>/Library/Caches/Documents on tvOS.
    data = Path(container.stdout.strip()) if container.returncode == 0 else None
    report = next((r for r in (data / "Documents" / RESULT_FILE, data / "Library/Caches/Documents" / RESULT_FILE) if r.is_file()), None) if data else None
    output += f"\n--- {RESULT_FILE} ---\n" + (report.read_text() if report else "(none)\n")
    subprocess.run(["xcrun", "simctl", "uninstall", udid, bundle_id], capture_output=True)
    return output, code


def copy_lines(text, package):
    """Every 'SwiftBindings copy' line of a package in a build log, in order: (copy, toolchain)."""
    return re.findall(rf"SwiftBindings copy: {re.escape(package)} \S+ -> (\S+) \(built on ([^)]*)\)", text)


def minor_version(version):
    """27.2.1 -> 27.2, 27 -> 27.0: SDK versions compared as the SDK names them."""
    parts = (version.split(".") + ["0"])[:2]
    return ".".join(parts)


def first_errors(text, count=3):
    lines = [l.strip() for l in text.splitlines() if re.search(r"\berror\b|\bwarning SWIFTBIND", l)]
    unique = list(dict.fromkeys(lines))
    return " | ".join(l[:400] for l in unique[:count]) or "no error line"


RESULT_FILE = "mtp-result.txt"

# The versionless library's code. Each comparison with a CaseTag member is compiled against the oldest
# copy, so the constant it holds is that copy's; the values it reads come from the copy the app runs.
LIBRARY_SOURCE = """namespace MtpLib;
public static class LibUse
{
    public static bool GivenName() => ProximityReader.MobileDriversLicenseDataRequest.Element.GivenName
        .Equals(ProximityReader.MobileDriversLicenseDataRequest.Element.GivenName);

    public static System.Collections.Generic.List<(string, object, object)> CaseOrderChecks()
    {
        var palette = new global::CaseOrder.Palette();
        var late = palette.GetLate();
        var solid = palette.Solid(7);
        var lateClear = palette.GetLateClear();
        return new()
        {
            ("Swift's Shade.late reads as CaseTag.Late", late.Tag == global::CaseOrder.Shade.CaseTag.Late, true),
            ("Shade.Late reads back in Swift", palette.Name(global::CaseOrder.Shade.Late), "late"),
            ("Shade.Late's tag", global::CaseOrder.Shade.Late.Tag == global::CaseOrder.Shade.CaseTag.Late, true),
            ("Swift's Paint.solid reads as CaseTag.Solid", solid.Tag == global::CaseOrder.Paint.CaseTag.Solid, true),
            ("Swift's Paint.solid(7) payload", solid.TryGetSolid(out var payload) ? (long)payload : -1L, 7L),
            ("Swift's Paint.lateClear reads as CaseTag.LateClear", lateClear.Tag == global::CaseOrder.Paint.CaseTag.LateClear, true),
            ("Paint.LateClear reads back in Swift", palette.Describe(global::CaseOrder.Paint.LateClear), "lateClear"),
            ("Paint.Clear reads back in Swift", palette.Describe(global::CaseOrder.Paint.Clear), "clear"),
        };
    }
}
"""

FRAMEWORK_INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>{name}</string>
  <key>CFBundleIdentifier</key><string>com.swiftbindings.mtp.{name}</string>
  <key>CFBundleName</key><string>{name}</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleSupportedPlatforms</key><array><string>{platform}</string></array>
  <key>MinimumOSVersion</key><string>17.0</string>
</dict>
</plist>
"""

SCENE_INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>UIApplicationSceneManifest</key>
  <dict>
    <key>UIApplicationSupportsMultipleScenes</key>
    <false/>
    <key>UISceneConfigurations</key>
    <dict>
      <key>UIWindowSceneSessionRoleApplication</key>
      <array>
        <dict>
          <key>UISceneConfigurationName</key>
          <string>Default Configuration</string>
          <key>UISceneDelegateClassName</key>
          <string>SceneDelegate</string>
        </dict>
      </array>
    </dict>
  </dict>
</dict>
</plist>
"""


def app_source(platform, framework, expected_copy, through_library):
    """The probe each app runs: which copy it loaded, a Swift call through the copy's wrapper, and the 27-only member."""
    probe = {
        "ProximityReader": """
            var given = ProximityReader.MobileDriversLicenseDataRequest.Element.GivenName;
            Check("Element.GivenName round-trips", given.Equals(ProximityReader.MobileDriversLicenseDataRequest.Element.GivenName), true);
            var name = typeof(ProximityReader.MobileDriversLicenseDataRequest.Element).GetProperty("Name", BindingFlags.Public | BindingFlags.Static);
#if MTP_PROBE27
            // Compiles only against a copy generated from the 27.2 SDK.
            Check("27-only Element.Name present", name != null, true);
            if (OperatingSystem.IsIOSVersionAtLeast(27) || OperatingSystem.IsMacCatalystVersionAtLeast(27))
                Check("27-only Element.Name differs from GivenName", ProximityReader.MobileDriversLicenseDataRequest.Element.Name.Equals(given), false);
#else
            Check("27-only Element.Name absent", name == null, true);
#endif
""",
        "WeatherKit": """
            Check("WeatherService.Shared non-null", WeatherKit.WeatherService.Shared != null, true);
            Check("MoonPhase.Full symbol", WeatherKit.MoonPhaseExtensions.GetSymbolName(WeatherKit.MoonPhase.Full), "moonphase.full.moon");
            Check("WeatherCondition.AllCases non-empty", WeatherKit.WeatherCondition.AllCases.Count > 0, true);
""",
    }[framework]
    anchor = {"ProximityReader": "ProximityReader.MobileDriversLicenseDataRequest", "WeatherKit": "WeatherKit.WeatherService"}[framework]
    library = ("Check(\"library call\", MtpLib.LibUse.GivenName(), true);\n"
               "            foreach (var (what, got, want) in MtpLib.LibUse.CaseOrderChecks())\n"
               "                Check(\"library compiled against the oldest copy: \" + what, got, want);") if through_library else ""
    body = f"""
    public static int Run()
    {{
        int fails = 0;
        var report = new System.Text.StringBuilder();
        void Say(string line)
        {{
            Console.WriteLine(line);
            report.AppendLine(line);
        }}
        void Check(string what, object? got, object want)
        {{
            bool ok = Equals(got?.ToString(), want.ToString());
            if (!ok) fails++;
            Say($"MTP {{(ok ? "ok  " : "FAIL")}} {{what}}: got={{got}} want={{want}}");
        }}
        try
        {{
            Say($"MTP info runtime {{RuntimeInformation.FrameworkDescription}} on {{RuntimeInformation.OSDescription}}");
            var assembly = typeof({anchor}).Assembly;
            var identity = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SwiftBindings.WrapperIdentity")?.Value ?? "";
            var copy = identity.Split(';').Select(f => f.Split('=', 2)).FirstOrDefault(f => f[0] == "copy")?[1];
            Check("loaded copy", copy, "{expected_copy}");
            {probe}
            {library}
        }}
        catch (Exception e)
        {{
            fails++;
            Say($"MTP FAIL exception {{e}}");
        }}
        Say($"MTP RESULT {{(fails == 0 ? "PASS" : "FAIL")}} fails={{fails}}");
        Console.Out.Flush();
        // A simulator app also leaves its report in its data container, which the gate reads
        // whether or not the launch's console capture forwarded the app's output.
        if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) && !OperatingSystem.IsMacCatalyst())
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "{RESULT_FILE}"), report.ToString());
        return fails == 0 ? 0 : 3;
    }}
"""
    usings = "using System.Reflection;\nusing System.Runtime.InteropServices;\n"
    if platform == "macos":
        return usings + "static class Program\n{\n    static int Main() => Run();\n" + body + "}\n"
    # UIKit apps adopt the scene lifecycle (an app built with the 27 SDK that does not traps on
    # launch), run the probe once their scene has connected, then exit with its verdict.
    return usings + "using CoreFoundation;\nusing Foundation;\nusing UIKit;\n\n" + """public class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
""" + body + """}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    [System.Diagnostics.CodeAnalysis.DynamicDependency(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces, typeof(SceneDelegate))]
    public override UISceneConfiguration GetConfiguration(UIApplication application,
        UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role) { DelegateClass = new ObjCRuntime.Class(typeof(SceneDelegate)) };
}

[Register("SceneDelegate")]
public class SceneDelegate : UIWindowSceneDelegate
{
    static bool started;

    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;
        Window = new UIWindow(windowScene) { RootViewController = new UIViewController() };
        Window.MakeKeyAndVisible();
        if (started)
            return;
        started = true;
        DispatchQueue.MainQueue.DispatchAsync(() => Environment.Exit(Program.Run()));
    }
}
"""


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", required=True, help="the repository checkout")
    parser.add_argument("--feed", required=True, help="directory holding the throwaway SwiftBindings packages")
    parser.add_argument("--sdk-version", required=True, help="their SwiftBindings Runtime/Sdk version")
    parser.add_argument("--apple-version", required=True, help="their SwiftBindings.Apple version")
    parser.add_argument("--work", required=True, help="scratch root outside the checkout (deleted on a pass)")
    parser.add_argument("--receipts", required=True)
    options = parser.parse_args(argv)
    if sys.platform != "darwin":
        parser.error("the multi-toolchain package gate needs macOS")
    gate = Gate(options)
    try:
        gate.prepare()
        gate.contributions()
        if not gate.failures:
            gate.merges()
        if not gate.failures:
            gate.consumers()
        if all(gate.merged(f).exists() for f in FIXTURES):
            gate.controls()
        gate.run_all()
    except BaseException as error:
        # An abort is a failure of its own, so the receipt can never read PASSED for a partial run.
        gate.step("the gate ran to the end", False, f"{type(error).__name__}: {error}")
        raise
    finally:
        passed = gate.finish()
    if passed:
        shutil.rmtree(gate.work, ignore_errors=True)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
