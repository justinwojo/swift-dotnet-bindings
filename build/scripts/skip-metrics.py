#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Aggregates binding-report.json files from validation output directories
# and produces a structured skip metrics report.
#
# Usage:
#   # After nuke validate:
#   python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --output skip-metrics.json
#
#   # For sim-validation:
#   python3 build/scripts/skip-metrics.py --input /Users/wojo/Dev/sim-validation/ --output skip-metrics.json
#
#   # Compare against baseline:
#   python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --baseline .validation-skip-baseline.json
#
#   # JSON-only output (for CI/scripting):
#   python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --json

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timezone


def find_binding_reports(input_dir):
    """Find all binding-report.json files in the input directory tree."""
    reports = []
    for root, dirs, files in os.walk(input_dir):
        for f in files:
            if f == "binding-report.json":
                reports.append(os.path.join(root, f))
    return sorted(reports)


def load_report(path):
    """Load a single binding-report.json file."""
    with open(path) as f:
        return json.load(f)


def get_git_sha():
    """Get the short git SHA of the current HEAD."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, timeout=5
        )
        return result.stdout.strip() if result.returncode == 0 else "unknown"
    except Exception:
        return "unknown"


def aggregate_reports(reports):
    """Aggregate multiple binding reports into a single metrics summary."""
    summary = {
        "total_libraries": 0,
        "total_types": 0,
        "emitted_types": 0,
        "skipped_types": 0,
        "total_members": 0,
        "emitted_members": 0,
        "skipped_members": 0,
        "synthesized_members": 0,
        "wrapped_items": 0,
        "skip_rate_pct": 0.0,
    }
    skip_reasons = {}
    per_library = {}
    emitted_by_kind = {}
    skipped_by_kind = {}

    for report_path in reports:
        data = load_report(report_path)
        module = data.get("ModuleName", os.path.basename(os.path.dirname(report_path)))

        summary["total_libraries"] += 1
        summary["total_types"] += data.get("TotalTypes", 0)
        summary["emitted_types"] += data.get("EmittedTypes", 0)
        summary["skipped_types"] += data.get("SkippedTypes", 0)
        summary["total_members"] += data.get("TotalMembers", 0)
        summary["emitted_members"] += data.get("EmittedMembers", 0)
        summary["skipped_members"] += data.get("SkippedMembers", 0)
        summary["synthesized_members"] += data.get("SynthesizedMembers", 0)
        summary["wrapped_items"] += len(data.get("WrappedItems", []))

        # Aggregate emitted/skipped by kind
        for kind, count in data.get("EmittedMembersByKind", {}).items():
            emitted_by_kind[kind] = emitted_by_kind.get(kind, 0) + count
        for kind, count in data.get("SkippedMembersByKind", {}).items():
            skipped_by_kind[kind] = skipped_by_kind.get(kind, 0) + count

        # Aggregate skip reasons
        lib_skip_reasons = {}
        for item in data.get("SkippedItems", []):
            reason = item.get("Reason", "Unknown")
            skip_reasons[reason] = skip_reasons.get(reason, 0) + 1
            lib_skip_reasons[reason] = lib_skip_reasons.get(reason, 0) + 1

        # Per-library entry
        lib_emitted = data.get("EmittedMembers", 0)
        lib_skipped = data.get("SkippedMembers", 0)
        lib_total = lib_emitted + lib_skipped
        per_library[module] = {
            "total_types": data.get("TotalTypes", 0),
            "emitted_types": data.get("EmittedTypes", 0),
            "emitted_members": lib_emitted,
            "skipped_members": lib_skipped,
            "synthesized_members": data.get("SynthesizedMembers", 0),
            "skip_rate_pct": round(lib_skipped / lib_total * 100, 1) if lib_total > 0 else 0.0,
            "top_skip_reasons": dict(sorted(lib_skip_reasons.items(), key=lambda x: -x[1])),
        }

    # Compute overall skip rate
    total = summary["emitted_members"] + summary["skipped_members"]
    summary["skip_rate_pct"] = round(summary["skipped_members"] / total * 100, 1) if total > 0 else 0.0

    # Sort skip reasons by count descending
    skip_reasons = dict(sorted(skip_reasons.items(), key=lambda x: -x[1]))

    # Sort per-library by skipped_members descending
    per_library = dict(sorted(per_library.items(), key=lambda x: -x[1]["skipped_members"]))

    return {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "git_sha": get_git_sha(),
        "summary": summary,
        "skip_reasons": skip_reasons,
        "emitted_by_kind": emitted_by_kind,
        "skipped_by_kind": skipped_by_kind,
        "per_library": per_library,
    }


def compare_baseline(metrics, baseline_path):
    """Compare current metrics against a baseline file. Returns list of warnings."""
    if not os.path.isfile(baseline_path):
        return ["No baseline file found at: " + baseline_path]

    with open(baseline_path) as f:
        baseline = json.load(f)

    warnings = []
    base_summary = baseline.get("summary", {})
    curr_summary = metrics["summary"]

    # Check skip count regression
    base_skipped = base_summary.get("skipped_members", 0)
    curr_skipped = curr_summary["skipped_members"]
    if curr_skipped > base_skipped:
        warnings.append(
            f"Skip count increased: {base_skipped} -> {curr_skipped} "
            f"(+{curr_skipped - base_skipped})"
        )
    elif curr_skipped < base_skipped:
        warnings.append(
            f"Skip count improved: {base_skipped} -> {curr_skipped} "
            f"(-{base_skipped - curr_skipped})"
        )

    # Check emitted count regression
    base_emitted = base_summary.get("emitted_members", 0)
    curr_emitted = curr_summary["emitted_members"]
    if curr_emitted < base_emitted:
        warnings.append(
            f"Emitted count decreased: {base_emitted} -> {curr_emitted} "
            f"(-{base_emitted - curr_emitted})"
        )

    # Check per-reason regressions
    base_reasons = baseline.get("skip_reasons", {})
    curr_reasons = metrics["skip_reasons"]
    for reason, curr_count in curr_reasons.items():
        base_count = base_reasons.get(reason, 0)
        if curr_count > base_count + 5:  # Allow small noise margin
            warnings.append(
                f"Skip reason '{reason}' increased: {base_count} -> {curr_count} "
                f"(+{curr_count - base_count})"
            )

    return warnings


def print_report(metrics, baseline_warnings=None):
    """Print a human-readable summary to stderr."""
    s = metrics["summary"]
    print(f"\n=== Skip Metrics Report ===", file=sys.stderr)
    print(f"Git SHA: {metrics['git_sha']}", file=sys.stderr)
    print(f"Libraries: {s['total_libraries']}", file=sys.stderr)
    print(f"Types: {s['emitted_types']}/{s['total_types']} emitted "
          f"({s['skipped_types']} skipped)", file=sys.stderr)
    print(f"Members: {s['emitted_members']}/{s['emitted_members'] + s['skipped_members']} emitted "
          f"({s['skipped_members']} skipped, {s['skip_rate_pct']}% skip rate)", file=sys.stderr)
    print(f"Synthesized: {s['synthesized_members']}", file=sys.stderr)
    print(f"Wrapped: {s['wrapped_items']}", file=sys.stderr)

    print(f"\n--- Skip Reasons ---", file=sys.stderr)
    for reason, count in metrics["skip_reasons"].items():
        print(f"  {count:5d}  {reason}", file=sys.stderr)

    print(f"\n--- Top Libraries by Skips ---", file=sys.stderr)
    for lib, data in list(metrics["per_library"].items())[:15]:
        print(f"  {lib}: {data['emitted_members']} emitted, "
              f"{data['skipped_members']} skipped ({data['skip_rate_pct']}%)", file=sys.stderr)

    if baseline_warnings:
        print(f"\n--- Baseline Comparison ---", file=sys.stderr)
        for w in baseline_warnings:
            print(f"  {'WARNING' if 'increased' in w or 'decreased' in w else 'INFO'}: {w}",
                  file=sys.stderr)

    print("", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(
        description="Aggregate binding-report.json files into skip metrics"
    )
    parser.add_argument("--input", required=True,
                        help="Directory containing binding-report.json files (searched recursively)")
    parser.add_argument("--output", default=None,
                        help="Output path for JSON metrics file")
    parser.add_argument("--baseline", default=None,
                        help="Path to baseline JSON for comparison")
    parser.add_argument("--json", action="store_true",
                        help="Output JSON to stdout (suppresses human-readable report)")
    args = parser.parse_args()

    if not os.path.isdir(args.input):
        print(f"Error: input directory not found: {args.input}", file=sys.stderr)
        sys.exit(1)

    reports = find_binding_reports(args.input)
    if not reports:
        print(f"Error: no binding-report.json files found in: {args.input}", file=sys.stderr)
        sys.exit(1)

    metrics = aggregate_reports(reports)

    # Baseline comparison
    baseline_warnings = None
    if args.baseline:
        baseline_warnings = compare_baseline(metrics, args.baseline)

    # Output
    if args.json:
        print(json.dumps(metrics, indent=2))
    else:
        print_report(metrics, baseline_warnings)
        if args.output:
            print(json.dumps(metrics, indent=2))

    # Write to file if requested
    if args.output:
        with open(args.output, "w") as f:
            json.dump(metrics, f, indent=2)
        if not args.json:
            print(f"Written to: {args.output}", file=sys.stderr)

    # Exit with warning code if baseline regressions found
    if baseline_warnings and any("increased" in w or "decreased" in w for w in baseline_warnings):
        sys.exit(2)


if __name__ == "__main__":
    main()
