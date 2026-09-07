#!/usr/bin/env python3
"""Fixed-input W10-01 probe using a freshly captured natural refusal log.

The generator is not rerun here. The actual corpus `main()` consumes the
retained SWIFTBIND111 subprocess outcome; discovery and managed compilation are
injected to isolate aggregate classification.
"""

import contextlib
import hashlib
import importlib.util
import io
import json
import pathlib
import sys
import tempfile


MAIN_REPO = pathlib.Path(__file__).resolve().parents[5]
CORPUS_SOURCE = pathlib.Path(
    "/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py")
NATURAL_DIR = MAIN_REPO / "src/docs/audit-2026-09/execution/PV/nested/generated-r1"
NATURAL_LOG = NATURAL_DIR / "generation.log"
NATURAL_RECEIPT = NATURAL_DIR / "receipt.json"
RESULT_PATH = pathlib.Path(__file__).with_name(
    "natural-refusal-classifier-result.json")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    natural_receipt = json.loads(NATURAL_RECEIPT.read_text())
    natural_output = NATURAL_LOG.read_text()
    if natural_receipt.get("exit") != 1 or "SWIFTBIND111" not in natural_output:
        raise SystemExit("retained natural refusal inputs do not match the probe contract")

    spec = importlib.util.spec_from_file_location("p1_run_library", CORPUS_SOURCE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    calls = []

    with tempfile.TemporaryDirectory(prefix="p1-natural-refusal-") as temp:
        root = pathlib.Path(temp)
        (root / "candidates").mkdir()
        (root / "candidates/candidates.json").write_text(json.dumps({
            "candidates": [{
                "name": "ContractNested",
                "git_url": "https://example.invalid/fixed-input",
                "products": ["ContractNested"],
            }]
        }))
        xcfw_root = root / "xcframeworks/ContractNested"
        (xcfw_root / "ContractNested.xcframework").mkdir(parents=True)
        (xcfw_root / module.RECEIPT_FILENAME).write_text(json.dumps({
            "schema_version": 1,
            "run_id": "p1-fixed-input",
            "status": "success",
            "expected_primary_products": ["ContractNested"],
            "package": {"resolved_version": "fixed-input"},
            "modules": {"produced": ["ContractNested"], "missing_required": []},
            "failures": [],
        }))
        product_dir = root / "output/ContractNested/ContractNested"
        product_dir.mkdir(parents=True)
        (product_dir / "ContractNested.Swift.iOS.csproj").write_text("<Project/>")

        def fake_run(cmd, log_path, timeout, cwd=None):
            calls.append(cmd)
            log = pathlib.Path(log_path)
            log.parent.mkdir(parents=True, exist_ok=True)
            if "--emit-input-graph" in cmd and "--no-verify-csharp" in cmd:
                sidecar = pathlib.Path(cmd[cmd.index("--emit-input-graph") + 1])
                sidecar.parent.mkdir(parents=True, exist_ok=True)
                sidecar.write_text(json.dumps({
                    "primaryModule": "ContractNested",
                    "importDependencies": {"ContractNested": []},
                }))
                log.write_text("fixed-input discovery control\n")
                return 0, 0, ""
            if "--emit-input-graph" in cmd:
                log.write_text(natural_output)
                return natural_receipt["exit"], 0, "natural SWIFTBIND111 refusal"
            if "build" in cmd:
                log.write_text("fixed-input managed compile control succeeded\n")
                return 0, 0, ""
            raise AssertionError(f"unexpected command: {cmd}")

        module.SWEEP = str(root)
        module.run = fake_run
        saved_argv = sys.argv
        sys.argv = [str(CORPUS_SOURCE), "ContractNested", "--skip-convert"]
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                try:
                    module.main()
                except SystemExit as exc:
                    exit_code = exc.code
        finally:
            sys.argv = saved_argv

        classified = json.loads(
            (root / "output/ContractNested/result.json").read_text())

    product = classified["products"][0]
    expected = (
        exit_code == 3
        and classified["status"] == "generate_failed"
        and product["generate"]["exit"] == 1
        and product["managed_compiles"] is True
        and product["publication_authorized"] is False
    )
    result = {
        "proof": (
            "actual run_library.main classification; fresh retained natural "
            "SWIFTBIND111 generator output; discovery and managed compile injected"
        ),
        "pass": expected,
        "corpus_source": str(CORPUS_SOURCE),
        "corpus_source_sha256": sha256(CORPUS_SOURCE),
        "natural_generator_receipt": str(NATURAL_RECEIPT.relative_to(MAIN_REPO)),
        "natural_generator_receipt_sha256": sha256(NATURAL_RECEIPT),
        "natural_generation_log": str(NATURAL_LOG.relative_to(MAIN_REPO)),
        "natural_generation_log_sha256": sha256(NATURAL_LOG),
        "exit": exit_code,
        "classified_result": classified,
        "injected_calls": calls,
    }
    RESULT_PATH.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"pass": expected, "result": str(RESULT_PATH)}))
    raise SystemExit(0 if expected else 1)


if __name__ == "__main__":
    main()
