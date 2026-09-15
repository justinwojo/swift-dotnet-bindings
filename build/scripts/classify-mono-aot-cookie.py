#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Classify Mono full-AOT managed-to-native wrappers for GC-cookie clobbers.

Input is ``llvm-objdump --disassemble --no-show-raw-insn`` output. The classifier
selects wrapper symbols containing a requested carrier token (SwiftSelf by
default), locates the Mono GC-safe-region entry, identifies whether x0 is parked
in a callee-saved register or spilled, and checks every destination register up
to the native PLT call. JSON output includes the exact instruction window.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
from collections import Counter
from pathlib import Path

VERSION = "owner03-x20-v2"
SYMBOL = re.compile(r"^([0-9a-fA-F]+) <([^>]+)>:\s*$")
INSTRUCTION = re.compile(r"^\s*([0-9a-fA-F]+):\s+(.*?)\s*$")
BRANCH_LINK = re.compile(r"^bl(?:r)?(?:\s|$)")
DESTINATION = re.compile(r"^([a-z][a-z0-9.]*)\s+([wx])(\d+|zr|sp)\b")
PAIR_DESTINATION = re.compile(
    r"^(ldp|ldnp|ldpsw)\s+([wx])(\d+|zr|sp),\s*([wx])(\d+|zr|sp)(?:,|\s|$)")
INDIRECT_BRANCH = re.compile(r"^blr\s+x\d+\b")
COOKIE_REGISTER = re.compile(r"^(?:mov\s+x(\d+),\s*x0|orr\s+x(\d+),\s*xzr,\s*x0)\b")
COOKIE_SPILL = re.compile(r"^str\s+x0,\s*\[(?:x29|sp)(?:,\s*#[^]]+)?\]")
ENTER = "mono_threads_enter_gc_safe_region_unbalanced"
EXIT = "mono_threads_exit_gc_safe_region_unbalanced"
NATIVE_CALL = "plt__icall_native"
INDIRECT_WRAPPER = "_Module_wrapper_native_indirect_"

# Instructions whose first general-purpose register operand is their sole destination. Unknown
# forms are deliberately not guessed: if the first operand is not the cookie, another operand
# could still be a destination, so the containing wrapper becomes unparsed rather than safe.
SINGLE_DESTINATION = {
    "adc", "adcs", "add", "adds", "adr", "adrp", "and", "ands", "asr", "bic",
    "bics", "cinc", "cinv", "cneg", "csel", "cset", "csetm", "eor", "extr",
    "ldar", "ldarb", "ldarh", "ldaxr", "ldaxrb", "ldaxrh", "ldr", "ldrb", "ldrh",
    "ldrsb", "ldrsh", "ldrsw", "ldur", "ldurb", "ldurh", "ldursb", "ldursh",
    "ldursw", "lsl", "lsr", "madd", "mov", "movk", "movn", "movz", "mrs", "mul",
    "mvn", "neg", "negs", "orr", "rbit", "rev", "rev16", "rev32", "sbfm", "sdiv",
    "smaddl", "smulh", "smull", "sub", "subs", "ubfm", "udiv", "umaddl", "umulh",
    "umull", "uxtb", "uxth", "sxtb", "sxth", "sxtw",
}
NO_DESTINATION = {
    "b", "bl", "blr", "br", "cbnz", "cbz", "cmn", "cmp", "nop", "prfm", "ret",
    "tbz", "tbnz", "tst",
}


def wrappers_from_lines(lines):
    current = None
    instructions = []
    for line in lines:
        match = SYMBOL.match(line)
        if match:
            if current is not None and "wrapper_managed_to_native_" in current:
                yield current, instructions
            current = match.group(2)
            instructions = []
            continue

        match = INSTRUCTION.match(line)
        if match:
            if current is not None:
                instructions.append((match.group(1), match.group(2)))
            continue

        # Treat any remaining malformed or truncated column-zero record as a hard function
        # boundary so its following instructions can never be appended to the preceding wrapper.
        # Apple llvm-objdump itself may print instruction addresses in column zero, which is why
        # the strict instruction parse above must run first.
        if line.strip() and not line[0].isspace():
            if current is not None and "wrapper_managed_to_native_" in current:
                yield current, instructions
            current = None
            instructions = []
            continue
    if current is not None and "wrapper_managed_to_native_" in current:
        yield current, instructions


def wrappers(path: Path):
    with path.open(errors="replace") as stream:
        yield from wrappers_from_lines(stream)


def register_write_state(instruction: str, register: int) -> bool | None:
    """Return True/False for a proved write/non-write, or None when the form is unknown."""
    pair = PAIR_DESTINATION.match(instruction)
    if pair:
        destinations = (pair.group(3), pair.group(5))
        return any(name.isdigit() and int(name) == register for name in destinations)

    mnemonic = instruction.split(None, 1)[0] if instruction else ""
    if mnemonic.startswith("st"):
        # A pre/post-indexed store writes its address base. Simple stores do not.
        writeback = re.search(r"\[x(\d+)[^]]*\]!(?:\s|$)|\[x(\d+)[^]]*\],\s*#", instruction)
        if writeback:
            base = writeback.group(1) or writeback.group(2)
            return int(base) == register
        return False
    if mnemonic in NO_DESTINATION or mnemonic.startswith("b."):
        return False

    destination = DESTINATION.match(instruction)
    if destination is None:
        return None
    mnemonic, name = destination.group(1), destination.group(3)
    if name not in ("zr", "sp") and int(name) == register:
        return True
    if mnemonic in SINGLE_DESTINATION:
        return False
    return None


def classify(symbol: str, instructions):
    enter = next((i for i, (_, text) in enumerate(instructions)
                  if BRANCH_LINK.match(text) and ENTER in text), None)
    if enter is None:
        return {"symbol": symbol, "verdict": "no_transition"}

    park = None
    for index in range(enter + 1, min(len(instructions), enter + 16)):
        text = instructions[index][1]
        register = COOKIE_REGISTER.match(text)
        if register:
            park = (index, "register", int(register.group(1) or register.group(2)))
            break
        if COOKIE_SPILL.match(text):
            park = (index, "spill", None)
            break
        if BRANCH_LINK.match(text):
            break
    if park is None:
        return {"symbol": symbol, "verdict": "unparsed", "note": "cookie park not found"}

    exit_call = next((i for i in range(park[0] + 1, len(instructions))
                      if BRANCH_LINK.match(instructions[i][1]) and EXIT in instructions[i][1]), None)
    if exit_call is None:
        return {"symbol": symbol, "verdict": "unparsed", "note": "GC exit transition not found"}

    direct_calls = [i for i in range(park[0] + 1, exit_call)
                    if BRANCH_LINK.match(instructions[i][1]) and NATIVE_CALL in instructions[i][1]]
    if direct_calls:
        call = direct_calls[0]
    elif INDIRECT_WRAPPER in symbol:
        # Mono's Module wrapper receives its native function pointer as an argument. Helper blr
        # calls may precede it, so the native boundary is the final blr before the GC exit.
        indirect_calls = [i for i in range(park[0] + 1, exit_call)
                          if INDIRECT_BRANCH.match(instructions[i][1])]
        call = indirect_calls[-1] if indirect_calls else None
    else:
        call = None
    if call is None:
        return {"symbol": symbol, "verdict": "unparsed", "note": "native call boundary not found"}

    window = [text for _, text in instructions[park[0] + 1:call]]

    if park[1] == "spill":
        return {
            "symbol": symbol,
            "verdict": "spilled",
            "cookie_park": instructions[park[0]][1],
            "window_insns": window,
            "callee": instructions[call][1],
            "exit_seen": exit_call is not None,
        }

    register = park[2]
    write_states = [register_write_state(text, register) for text in window]
    clobbered = any(state is True for state in write_states)
    if not clobbered and any(state is None for state in write_states):
        unknown = next(text for text, state in zip(window, write_states) if state is None)
        return {
            "symbol": symbol,
            "verdict": "unparsed",
            "cookie_reg": f"x{register}",
            "cookie_park": instructions[park[0]][1],
            "window_insns": window,
            "callee": instructions[call][1],
            "exit_seen": True,
            "note": f"unknown register-write form: {unknown}",
        }
    return {
        "symbol": symbol,
        "verdict": "clobber" if clobbered else "safe",
        "cookie_reg": f"x{register}",
        "cookie_park": instructions[park[0]][1],
        "window_insns": window,
        "callee": instructions[call][1],
        "exit_seen": exit_call is not None,
    }


def self_test() -> None:
    def insns(*texts):
        return [(f"{index:04x}", text) for index, text in enumerate(texts)]

    enter = "bl 0x100 <_mono_threads_enter_gc_safe_region_unbalanced>"
    exit_ = "bl 0x200 <_mono_threads_exit_gc_safe_region_unbalanced>"
    native = "bl 0x300 <plt__icall_native_test>"
    symbol = "wrapper_managed_to_native_Test_SwiftSelf"

    assert register_write_state("mov x20, x4", 20) is True
    assert register_write_state("mov w20, w4", 20) is True
    assert register_write_state("ldp x19, x20, [sp]", 20) is True
    assert register_write_state("ldp x20, xzr, [sp]", 20) is True
    assert register_write_state("ldp xzr, x20, [sp]", 20) is True
    assert register_write_state("ldpsw x19, x20, [sp]", 20) is True
    assert register_write_state("ldnp x19, x20, [sp]", 20) is True
    assert register_write_state("str x20, [sp]", 20) is False
    assert register_write_state("cmp x20, x4", 20) is False
    assert register_write_state("mystery x19, x20", 20) is None

    safe = classify(symbol, insns(enter, "mov x20, x0", "ldr x4, [x29]", native, exit_))
    assert safe["verdict"] == "safe"
    clobber = classify(symbol, insns(enter, "mov x20, x0", "mov x20, x4", native, exit_))
    assert clobber["verdict"] == "clobber"
    pair_clobber = classify(symbol, insns(
        enter, "mov x20, x0", "ldnp x19, x20, [sp]", native, exit_))
    assert pair_clobber["verdict"] == "clobber"
    spill = classify(symbol, insns(enter, "str x0, [x29, #0x20]", "ldr x4, [x29]", native, exit_))
    assert spill["verdict"] == "spilled"
    assert classify(symbol, insns("mov x20, x0", native))["verdict"] == "no_transition"
    assert classify(symbol, insns(enter, "mov x20, x0", "ldr x4, [x29]", native))["verdict"] == "unparsed"
    unknown = classify(symbol, insns(
        enter, "mov x20, x0", "mystery x19, x20", native, exit_))
    assert unknown["verdict"] == "unparsed"

    # A helper indirect call does not truncate the write window before the real native PLT call.
    helper = classify(symbol, insns(
        enter, "mov x20, x0", "blr\tx17", "mov x20, x4", native, exit_))
    assert helper["verdict"] == "clobber" and NATIVE_CALL in helper["callee"]

    # Mono's genuine Module indirect wrapper has no PLT boundary; its final blr before exit is the
    # native call and remains classifiable.
    module_symbol = (
        "wrapper_managed_to_native__Module_wrapper_native_indirect_void_"
        "modoptCallConvSwift_SwiftSelf")
    module = classify(module_symbol, insns(
        enter, "mov x23, x0", "blr\tx17", "ldr x2, [x29, #0x10]", "blr\tx2", exit_))
    assert module["verdict"] == "safe" and module["callee"] == "blr\tx2"

    # Streaming yields a completed wrapper at a malformed boundary, drops the malformed/truncated
    # symbol body, and still yields the final valid wrapper at EOF.
    streamed = list(wrappers_from_lines(io.StringIO(
        "0000 <wrapper_managed_to_native_First_SwiftSelf>:\n"
        "0000: mov x20, x0\n"
        "0001 <wrapper_managed_to_native_Truncated_SwiftSelf\n"
        "  0001: mov x20, x4\n"
        "not-hex <wrapper_managed_to_native_Malformed_SwiftSelf>:\n"
        "  0002: mov x20, x4\n"
        "0003 <wrapper_managed_to_native_Final_SwiftSelf>:\n"
        "  0003: mov x23, x0\n")))
    assert [item[0] for item in streamed] == [
        "wrapper_managed_to_native_First_SwiftSelf",
        "wrapper_managed_to_native_Final_SwiftSelf",
    ]
    assert streamed[-1][1] == [("0003", "mov x23, x0")]

    print(f"{VERSION}: streaming and fail-closed classification controls passed")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("assembly", nargs="?", type=Path)
    parser.add_argument("--carrier", default="SwiftSelf")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--version", action="version", version=VERSION)
    args = parser.parse_args()
    if args.self_test:
        self_test()
        if args.assembly is None:
            return
    if args.assembly is None:
        parser.error("assembly is required unless --self-test is used")

    selected = []
    total = 0
    for symbol, instructions in wrappers(args.assembly):
        total += 1
        if args.carrier in symbol:
            selected.append(classify(symbol, instructions))

    payload = {
        "classifier_version": VERSION,
        "assembly": str(args.assembly),
        "assembly_sha256": sha256(args.assembly),
        "carrier": args.carrier,
        "managed_to_native_wrappers": total,
        "selected_wrappers": len(selected),
        "verdicts": dict(sorted(Counter(item["verdict"] for item in selected).items())),
        "results": selected,
    }
    print(json.dumps({key: value for key, value in payload.items() if key != "results"}, indent=2))
    for item in selected:
        if item["verdict"] == "clobber":
            print(f"CLOBBER {item['cookie_reg']} {item['symbol']}")
    if args.out:
        args.out.write_text(json.dumps(payload, indent=2) + "\n")


if __name__ == "__main__":
    main()
