#!/usr/bin/env python3
"""Wave 0 M0-F: seed 00-file-coverage-ledger.md (read-only inventory; writes only the ledger)."""
from __future__ import annotations

import os
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

ROOT = Path("/Users/wojo/Dev/swift-bindings")
OUT = ROOT / "src/docs/deep-audit-2026-07/00-file-coverage-ledger.md"
SKIP_DIR_NAMES = {"bin", "obj", ".build", "node_modules", ".git", "__pycache__"}


@dataclass(frozen=True)
class Area:
    key: str
    title: str
    roots: tuple[str, ...]
    # (suffixes, or empty = any) and optional name-prefix filters
    globs: tuple[str, ...]  # file suffixes like .cs, .swift; empty means special
    special: str = ""  # "sdk" | "build" | "rules" | "apple"


AREAS: list[Area] = [
    Area(
        "gen_src",
        "src/Swift.Bindings/src (generator source, *.cs)",
        ("src/Swift.Bindings/src",),
        (".cs",),
    ),
    Area(
        "gen_tests",
        "src/Swift.Bindings/tests (unit tests, *.cs)",
        ("src/Swift.Bindings/tests",),
        (".cs",),
    ),
    Area(
        "rt_src",
        "src/Swift.Runtime/src (runtime library, *.cs)",
        ("src/Swift.Runtime/src",),
        (".cs",),
    ),
    Area(
        "rt_tests",
        "src/Swift.Runtime/tests (runtime tests, *.cs)",
        ("src/Swift.Runtime/tests",),
        (".cs",),
    ),
    Area(
        "rt_swift",
        "src/Swift.Runtime/swift (native runtime: *.swift, *.c, *.sh)",
        ("src/Swift.Runtime/swift",),
        (".swift", ".c", ".sh"),
    ),
    Area(
        "sdk",
        "src/Swift.Bindings.Sdk (*.props, *.targets, *.cs, scripts)",
        ("src/Swift.Bindings.Sdk",),
        (),
        special="sdk",
    ),
    Area(
        "apple",
        "src/Swift.Bindings.Apple (*.cs, *.swift, *.targets — not bin/obj)",
        ("src/Swift.Bindings.Apple",),
        (),
        special="apple",
    ),
    Area(
        "analyzers",
        "src/Swift.Analyzers (*.cs)",
        ("src/Swift.Analyzers",),
        (".cs",),
    ),
    Area(
        "analyzer_tests",
        "src/Swift.Analyzers.Tests (*.cs)",
        ("src/Swift.Analyzers.Tests",),
        (".cs",),
    ),
    Area(
        "test_discovery",
        "src/SwiftBindings.TestDiscovery (*.cs)",
        ("src/SwiftBindings.TestDiscovery",),
        (".cs",),
    ),
    Area(
        "templates",
        "src/Swift.Bindings.Templates (template content + project)",
        ("src/Swift.Bindings.Templates",),
        (),
        special="templates",
    ),
    Area(
        "build",
        "build/ (Nuke targets, scripts, Helpers, Models, Tools)",
        ("build",),
        (),
        special="build",
    ),
    Area(
        "bt_sources",
        "BindingTests/Sources (*.swift)",
        ("BindingTests/Sources",),
        (".swift",),
    ),
    Area(
        "bt_runtime",
        "BindingTests/RuntimeTestsApp (*.cs only, not bin/obj)",
        ("BindingTests/RuntimeTestsApp",),
        (".cs",),
    ),
    Area(
        "sip",
        "tools/SwiftInterfaceParser/Sources (*.swift)",
        ("tools/SwiftInterfaceParser/Sources",),
        (".swift",),
    ),
    Area(
        "rules",
        ".claude/rules (*.md)",
        (".claude/rules",),
        (".md",),
    ),
]


def purpose_for(rel: str) -> str:
    """One-line purpose guessed from path/name."""
    name = Path(rel).name
    stem = Path(rel).stem
    parts = rel.replace("\\", "/").split("/")
    low = rel.lower()

    # Rules
    if rel.startswith(".claude/rules/"):
        return {
            "constraints.md": "Load-bearing trap constraints for generator/runtime",
            "emitter.md": "Emitter architecture and projection rules",
            "parser-marshaler.md": "Parser/marshaler patterns and gates",
            "bindingtests.md": "BindingTests nuke targets and test attributes",
            "swiftui-bridge.md": "SwiftUI bridge detection and emission",
            "csharp-files.md": "Copyright header conventions for C#/Swift",
        }.get(name, "Scoped agent rule")

    # Build / nuke
    if name.startswith("Build.") and name.endswith(".cs"):
        return f"Nuke target partial: {stem[6:]}"
    if "/Helpers/" in rel:
        return f"Build helper: {stem}"
    if "/Models/" in rel:
        return f"Build model/DTO: {stem}"
    if "/Tools/" in rel:
        return f"Build tool wrapper: {stem}"
    if "/scripts/" in rel:
        return f"Build/CI script: {name}"
    if name == "Build.cs":
        return "Nuke Build entry and shared properties"
    if name == "_build.csproj":
        return "Nuke build project"

    # Generator layers
    if "/Emitter/" in rel or "/StringEmitter/" in rel:
        if "Handler/" in rel:
            return f"Emitter type/method handler: {stem}"
        if "ThunkEmitter" in rel:
            return f"Native thunk emission: {stem}"
        if stem.startswith("ProtocolProxy"):
            return f"Protocol proxy emitter part: {stem}"
        if stem.startswith("Closure"):
            return f"Closure marshalling emitter: {stem}"
        if stem.startswith("SwiftUI"):
            return f"SwiftUI bridge emitter: {stem}"
        if "Wrapper" in stem:
            return f"Swift @_cdecl wrapper emitter: {stem}"
        return f"Code emitter: {stem}"
    if "/Marshaler/" in rel:
        if "/Projection/" in rel:
            return f"Type projection: {stem}"
        return f"Marshaler/handler: {stem}"
    if "/Parser/" in rel:
        return f"ABI/interface parser: {stem}"
    if "/Demangler/" in rel:
        return f"Swift demangler: {stem}"
    if "/TypeDatabase/" in rel:
        return f"Type database: {stem}"
    if "/Configuration/" in rel:
        return f"Generator configuration/tooling: {stem}"
    if "/Model/" in rel:
        return f"IR model: {stem}"
    if "/Reporting/" in rel:
        return f"Binding/skip report: {stem}"
    if "/ObjC/" in rel:
        return f"ObjC pipeline: {stem}"
    if "/AppleTypesManifest/" in rel:
        return f"Apple types manifest tooling: {stem}"
    if name == "BindingsGeneratorCommand.cs":
        return "Main generator CLI orchestration command"
    if name == "Program.cs" and "Swift.Bindings" in rel:
        return "Generator CLI entry point"
    if name == "CliOptions.cs":
        return "Generator CLI option definitions"

    # Runtime
    if "/Swift.Runtime/" in rel:
        if "/Runtime/" in rel:
            return f"Runtime core: {stem}"
        if "/SwiftUI/" in rel:
            return f"SwiftUI runtime stub: {stem}"
        if name.endswith("Database.xml"):
            return f"Type database XML: {stem}"
        if name.endswith(".swift"):
            return f"Native runtime Swift: {stem}"
        if name.endswith(".c"):
            return f"Native runtime C: {stem}"
        if name.endswith(".sh"):
            return f"Native runtime build script: {stem}"
        if "/tests/" in rel:
            return f"Runtime unit test: {stem}"
        return f"Runtime type/API: {stem}"

    # SDK / Apple / templates
    if "Swift.Bindings.Sdk" in rel:
        if name == "Sdk.targets":
            return "MSBuild SDK targets (generate/compile/pack)"
        if name == "Sdk.props":
            return "MSBuild SDK props"
        if name.endswith(".sh"):
            return f"SDK script: {name}"
        return f"SDK artifact: {name}"
    if "Swift.Bindings.Apple" in rel:
        if name.endswith(".swift"):
            return f"Apple supplement Swift shim: {stem}"
        if name.endswith(".targets"):
            return "Apple package MSBuild targets"
        return f"Apple supplement managed type: {stem}"
    if "Swift.Bindings.Templates" in rel:
        return f"dotnet new template content: {name}"
    if "Swift.Analyzers" in rel:
        return f"Roslyn analyzer/test: {stem}"
    if "TestDiscovery" in rel:
        return f"Test discovery source generator: {stem}"

    # BindingTests
    if "BindingTests/Sources" in rel:
        domain = parts[3] if len(parts) > 3 else "fixture"
        return f"Swift BindingTests fixture ({domain}): {stem}"
    if "BindingTests/RuntimeTestsApp" in rel:
        domain = parts[2] if len(parts) > 2 else "tests"
        if domain == "Infrastructure":
            return f"Runtime test harness: {stem}"
        if name == "Program.cs":
            return "RuntimeTestsApp entry / test runner"
        return f"Runtime test ({domain}): {stem}"

    # SwiftInterfaceParser
    if "SwiftInterfaceParser" in rel:
        return f"SwiftSyntax interface fact walker: {stem}"

    return f"TBD — {stem}"


def count_lines(path: Path) -> int:
    try:
        with path.open("rb") as f:
            data = f.read()
        if not data:
            return 0
        # Count newlines; last line without trailing NL still counts as a line
        n = data.count(b"\n")
        if not data.endswith(b"\n"):
            n += 1
        return n
    except OSError:
        return 0


def should_skip_dir(dirname: str) -> bool:
    return dirname in SKIP_DIR_NAMES or dirname.startswith(".")


def iter_files(area: Area) -> list[Path]:
    found: list[Path] = []
    for root_rel in area.roots:
        base = ROOT / root_rel
        if not base.exists():
            continue
        if base.is_file():
            found.append(base)
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            # prune
            dirnames[:] = [d for d in dirnames if not should_skip_dir(d)]
            # never walk BindingTests/output etc.
            rel_dir = Path(dirpath).relative_to(ROOT).as_posix()
            if "/output/" in f"/{rel_dir}/" or rel_dir.endswith("/output"):
                dirnames[:] = []
                continue
            for fn in filenames:
                p = Path(dirpath) / fn
                if accept_file(area, p):
                    found.append(p)
    return sorted(found, key=lambda p: p.as_posix().lower())


def accept_file(area: Area, path: Path) -> bool:
    rel = path.relative_to(ROOT).as_posix()
    name = path.name
    suf = path.suffix.lower()

    # Always exclude generated/build junk
    if any(part in SKIP_DIR_NAMES for part in path.parts):
        return False
    if name.endswith(".pyc") or name == ".DS_Store":
        return False

    if area.special == "sdk":
        # props, targets, cs, scripts under Sdk/ or tools scripts; exclude packed net10.0 any dlls
        if "/tools/net10.0/" in rel or "/tools/swift-interface-parser/" in rel:
            return False
        if "/bin/" in rel or "/obj/" in rel:
            return False
        if suf in {".props", ".targets", ".cs", ".sh"}:
            return True
        # apple-types-manifest json/md/sh under tools
        if "/tools/apple-types-manifest/" in rel and suf in {".sh", ".json", ".md"}:
            return True
        if name.endswith(".csproj"):
            return True
        return False

    if area.special == "apple":
        if "/bin/" in rel or "/obj/" in rel or "/native/" in rel:
            return False
        if suf in {".cs", ".swift", ".targets"}:
            return True
        if name.endswith(".csproj"):
            return True
        return False

    if area.special == "templates":
        if "/bin/" in rel or "/obj/" in rel:
            return False
        if suf in {".csproj"} or name.endswith(".csproj"):
            return True
        # template content files
        if "/content/" in rel:
            return True
        return False

    if area.special == "build":
        if "/bin/" in rel or "/obj/" in rel:
            return False
        # In-scope: build/*.cs, build/scripts, Helpers, Models, Tools
        # Plus closely related gate fixtures under build/
        if suf == ".cs":
            return True
        if "/scripts/" in rel and suf in {".py", ".sh"}:
            return True
        if suf in {".swift"} and (
            "/PackGate/" in rel or "/X64PackGate/" in rel or "/x64-thunk-gate/" in rel
        ):
            return True
        if name in {"_build.csproj"}:
            return True
        if "/x64-thunk-gate/Driver/" in rel and suf in {".cs", ".csproj"}:
            return True
        return False

    if area.globs:
        return suf in area.globs
    return False


def md_escape(s: str) -> str:
    return s.replace("|", "\\|")


def main() -> None:
    area_rows: dict[str, list[tuple[str, int, str]]] = {}
    all_rows: list[tuple[str, int, str, str]] = []  # area, loc, path, purpose

    for area in AREAS:
        rows: list[tuple[str, int, str]] = []
        for p in iter_files(area):
            rel = p.relative_to(ROOT).as_posix()
            loc = count_lines(p)
            purpose = purpose_for(rel)
            rows.append((rel, loc, purpose))
            all_rows.append((area.key, loc, rel, purpose))
        area_rows[area.key] = rows

    total_files = len(all_rows)
    total_loc = sum(r[1] for r in all_rows)
    top30 = sorted(all_rows, key=lambda r: (-r[1], r[2]))[:30]

    # Priority tiers from size + known load-bearing names
    t0_names = {
        "EveryProtocolEmitter.cs",
        "ProtocolProxyEmitter.Receivers.cs",
        "ProtocolProxyEmitter.cs",
        "IHandler.cs",
        "BindingsGeneratorCommand.cs",
        "SwiftABIParser.cs",
        "Build.BindingTests.cs",
        "ClosureEmitter.cs",
        "MethodHandler.cs",
        "ModuleEmissionContext.cs",
        "TypeProjectionFactory.cs",
        "NameProvider.cs",
        "MarshallingHelpers.cs",
        "MemberValidationPipeline.cs",
        "Sdk.targets",
        "SwiftBindingsRuntime.swift",
        "TypeDatabaseExtensions.cs",
        "EnumHandler.cs",
        "WitnessDispatchEmitter.cs",
        "ProtocolHandler.cs",
        "ModuleEmitter.cs",
        "SwiftWrapperCompiler.cs",
        "Build.Validation.cs",
        "Build.Pack.cs",
        "ValueWitnessTable.cs",
        "TypeMetadata.cs",
        "Arc.cs",
        "ExistentialContainer.cs",
        "constraints.md",
    }

    lines: list[str] = []
    lines.append("# File Coverage Ledger — Deep Audit 2026-07")
    lines.append("")
    lines.append("**Wave**: 0 (seed)  ")
    lines.append("**Agent**: M0-F  ")
    lines.append("**Status**: All in-scope files seeded as `inventory` — nothing `reviewed-deep` yet.  ")
    lines.append("**Scope**: Source surfaces only; excludes `bin/`, `obj/`, `.build/`, and generated `BindingTests/output/`.  ")
    lines.append("**LOC method**: physical line count (bytes split on `\\n`; final non-terminated line counted).  ")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## SUMMARY")
    lines.append("")
    lines.append(f"| Metric | Value |")
    lines.append(f"|--------|------:|")
    lines.append(f"| **Total files** | **{total_files}** |")
    lines.append(f"| **Total LOC** | **{total_loc:,}** |")
    lines.append(f"| Status for all rows | `inventory` |")
    lines.append(f"| Deep-reviewed | 0 |")
    lines.append("")
    lines.append("### Per-area rollup")
    lines.append("")
    lines.append("| Area | Files | LOC |")
    lines.append("|------|------:|----:|")
    for area in AREAS:
        rows = area_rows[area.key]
        loc = sum(r[1] for r in rows)
        lines.append(f"| {area.title} | {len(rows)} | {loc:,} |")
    lines.append(f"| **TOTAL** | **{total_files}** | **{total_loc:,}** |")
    lines.append("")
    lines.append("### Top 30 largest files (whole surface)")
    lines.append("")
    lines.append("| Rank | LOC | Path | Area key |")
    lines.append("|-----:|----:|------|----------|")
    for i, (akey, loc, path, _) in enumerate(top30, 1):
        lines.append(f"| {i} | {loc:,} | `{path}` | {akey} |")
    lines.append("")
    lines.append("### Suggested deep-review priority tiers")
    lines.append("")
    lines.append("#### T0 — Mega / crash-class load-bearing (Wave 1+ first)")
    lines.append("")
    lines.append(
        "Files that are simultaneously large and on the ABI/marshalling/proxy/wrapper critical path. "
        "Deep review should be **branch-level**, not whole-file skims."
    )
    lines.append("")
    t0 = [r for r in all_rows if Path(r[2]).name in t0_names or r[1] >= 2500]
    t0 = sorted(t0, key=lambda r: (-r[1], r[2]))
    lines.append("| LOC | Path | Why |")
    lines.append("|----:|------|-----|")
    for _, loc, path, purpose in t0[:40]:
        lines.append(f"| {loc:,} | `{path}` | {md_escape(purpose)} |")
    lines.append("")
    lines.append("#### T1 — Load-bearing (emitters, marshaler, runtime ownership, gates, SDK)")
    lines.append("")
    lines.append(
        "Everything under generator Emitter/Marshaler/Parser/TypeDatabase, runtime `Swift/Runtime/`, "
        "Nuke pack/binding-tests/validate targets, and `Sdk.targets`/`Sdk.props` that is **not** already T0. "
        "Also `.claude/rules/constraints.md` (trap list verification in Wave 10)."
    )
    lines.append("")
    lines.append("| Bucket | Paths |")
    lines.append("|--------|-------|")
    lines.append("| Generator core | `src/Swift.Bindings/src/{Emitter,Marshaler,Parser,TypeDatabase,Configuration,ObjC,Reporting}/**` |")
    lines.append("| Runtime ownership | `src/Swift.Runtime/src/Swift/Runtime/**`, `src/Swift.Runtime/swift/**` |")
    lines.append("| Build gates | `build/Build.{BindingTests,Validation,Pack,PackGate,ReleaseGates}*.cs` |")
    lines.append("| SDK packaging | `src/Swift.Bindings.Sdk/Sdk/**` |")
    lines.append("| Trap rules | `.claude/rules/*.md` |")
    lines.append("")
    lines.append("#### T2 — Rest of inventory")
    lines.append("")
    lines.append(
        "Unit tests, BindingTests fixtures/runtime tests, demangler corpus, Apple supplement shims, "
        "templates, analyzers, SwiftInterfaceParser walkers, small helpers/models. Map coverage still "
        "required; deep review is sample-driven or finding-driven rather than exhaustive line reads."
    )
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## AREA LEDGERS")
    lines.append("")
    lines.append("Every row: `status=inventory`. Update later waves to `mapped` / `sampled` / `reviewed-deep` / `n/a`.")
    lines.append("")

    for area in AREAS:
        rows = area_rows[area.key]
        loc_sum = sum(r[1] for r in rows)
        lines.append(f"## {area.title}")
        lines.append("")
        lines.append(f"**Files**: {len(rows)}  ")
        lines.append(f"**LOC**: {loc_sum:,}  ")
        lines.append("")
        lines.append("| Path | LOC | Status | Purpose |")
        lines.append("|------|----:|--------|---------|")
        for path, loc, purpose in rows:
            lines.append(
                f"| `{path}` | {loc:,} | inventory | {md_escape(purpose)} |"
            )
        lines.append("")

    lines.append("---")
    lines.append("")
    lines.append("## Regeneration")
    lines.append("")
    lines.append("```bash")
    lines.append(
        "python3 src/docs/deep-audit-2026-07/_seed_file_coverage_ledger.py"
    )
    lines.append("```")
    lines.append("")
    lines.append(
        "Wave 0 seed only writes this ledger. Later waves must **update statuses in place**, not re-seed blindly."
    )
    lines.append("")

    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {OUT}")
    print(f"Total files={total_files} LOC={total_loc}")
    print("Top 10:")
    for i, (_, loc, path, _) in enumerate(top30[:10], 1):
        print(f"  {i:2d}. {loc:6d}  {path}")


if __name__ == "__main__":
    main()
