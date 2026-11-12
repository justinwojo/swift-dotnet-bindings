#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Classify Mono managed-to-native wrappers for Swift-register victims.

A `CallConvSwift` P/Invoke that passes an untyped `SwiftSelf` loads self into x20 immediately
before the native branch (and a `SwiftError` primes x21). Mono's wrapper register allocator does
not exclude those registers for its own values, so a value the wrapper parked there earlier (the
LMF address, the GC-safe-region cookie, or anything else) is overwritten by the argument setup.
x20 is callee-saved, so after the call it still holds self, and any post-call use of the parked
value then operates on self instead.

Two independent classifications are reported per wrapper:

* ``x20`` (the general one): locate the native branch, the x20 write that loads self for it and
  the definition of x20 before that write; then walk the wrapper's control-flow graph from the
  instruction after the branch and report every read of x20 reachable before x20 is redefined.
  A reachable read is a victim; the parked definition says which value it was (``lmf`` when the
  park is followed by Mono's LMF push through that register, ``cookie`` when it follows the
  GC-safe-region entry, ``other`` otherwise). ``x21`` gets the same treatment for SwiftError
  wrappers, except that its post-call reads include the legitimate error writeback: with no park
  it is safe, and a read of an ``other`` park is ``review`` rather than a victim.
* ``verdict`` (the original cookie-only check, kept for receipts that predate the general one):
  whether the cookie's register is written between the GC-safe-region entry and the native call.

Input is either ``llvm-objdump --disassemble --no-show-raw-insn`` output (AOT images) or lldb
``disassemble -n <wrapper>`` transcripts (a live process, e.g. JIT-compiled wrappers). Unknown
instruction forms, and post-call paths that cannot be followed to their end, never produce a safe
answer: they make the wrapper ``unparsed``.

Mono AOT-compiles the managed-to-native wrappers into the executable on the iOS/tvOS simulator and
Mac Catalyst as well as on device, so every lane except the interpreter one can be read statically.
Disassembling only the carrier wrappers keeps the input small::

    xcrun nm -U <app executable> | awk '{print $NF}' | grep '^wrapper_managed_to_native_' \\
        | grep -E 'SwiftSelf|SwiftError' | paste -sd, - > symbols.txt
    xcrun llvm-objdump --disassemble --no-show-raw-insn \\
        --disassemble-symbols="$(cat symbols.txt)" <app executable> > wrappers.asm
    python3 build/scripts/classify-mono-aot-cookie.py wrappers.asm --out swiftself.json
    python3 build/scripts/classify-mono-aot-cookie.py wrappers.asm --carrier SwiftError --out error.json
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
from collections import Counter
from pathlib import Path

VERSION = "m2n-x20-v3"
SYMBOL = re.compile(r"^([0-9a-fA-F]+) <([^>]+)>:\s*$")
INSTRUCTION = re.compile(r"^\s*([0-9a-fA-F]+):\s+(.*?)\s*$")
LLDB_HEADER = re.compile(r"^\S+`(wrapper_managed_to_native_\S+):\s*$")
LLDB_INSTRUCTION = re.compile(r"^(?:->\s*)?\S*\[0x([0-9a-fA-F]+)\]\s+<\+\d+>:\s+(.*?)\s*$")
BRANCH_LINK = re.compile(r"^bl(?:r)?(?:\s|$)")
DIRECT_BRANCH_LINK = re.compile(r"^bl\s")
DESTINATION = re.compile(r"^([a-z][a-z0-9.]*)\s+([wx])(\d+|zr|sp)\b")
PAIR_DESTINATION = re.compile(
    r"^(ldp|ldnp|ldpsw)\s+([wx])(\d+|zr|sp),\s*([wx])(\d+|zr|sp)(?:,|\s|$)")
INDIRECT_BRANCH = re.compile(r"^blr\s+x\d+\b")
COOKIE_REGISTER = re.compile(r"^(?:mov\s+x(\d+),\s*x0|orr\s+x(\d+),\s*xzr,\s*x0)\b")
COOKIE_SPILL = re.compile(r"^str\s+x0,\s*\[(?:x29|sp)(?:,\s*#[^]]+)?\]")
REGISTER = re.compile(r"\b([wx])(\d+)\b")
TARGET = re.compile(r"\b0x([0-9a-fA-F]+)\b")
ENTER = "mono_threads_enter_gc_safe_region_unbalanced"
EXIT = "mono_threads_exit_gc_safe_region_unbalanced"
NATIVE_CALL = "plt__icall_native"
INDIRECT_WRAPPER = "_Module_wrapper_native_indirect_"
UNTYPED_SWIFT_SELF = re.compile(r"System_Runtime_InteropServices_Swift_SwiftSelf(?!_1_)")
SWIFT_ERROR = "System_Runtime_InteropServices_Swift_SwiftError"

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
    "umull", "uxtb", "uxth", "sxtb", "sxth", "sxtw", "msub", "ubfx", "sbfx",
    "ubfiz", "sbfiz", "ror", "orn", "eon", "csinc", "csinv", "csneg", "cls", "clz",
    "fmov", "fcvtzs", "fcvtzu", "umov", "smov",
}
# Single-destination forms that also read their destination.
READ_MODIFY_DESTINATION = {"movk", "bfi", "bfxil", "bfm"}
NO_DESTINATION = {
    "b", "bl", "blr", "br", "cbnz", "cbz", "cmn", "cmp", "nop", "prfm", "ret",
    "tbz", "tbnz", "tst", "ccmp", "ccmn", "dmb", "dsb", "isb", "brk", "hint",
}
CONDITIONAL_BRANCH = {"cbz", "cbnz", "tbz", "tbnz"}
# Ordinary stores: every register operand is a source, and only pre/post-indexing writes the base.
STORE = {"str", "strb", "strh", "stur", "sturb", "sturh", "stp", "stnp", "stlr", "stlrb", "stlrh"}
STORE_EXCLUSIVE = {"stxr", "stlxr", "stxrb", "stlxrb", "stxrh", "stlxrh", "stxp", "stlxp"}


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


def lldb_wrappers_from_lines(lines):
    """Parse lldb ``disassemble -n`` transcripts. lldb may print one function twice; addresses
    dedupe it, and the ``; comment`` (callee symbol or ``<+off>``) is kept as an annotation."""
    functions = {}
    order = []
    current = None
    for line in lines:
        header = LLDB_HEADER.match(line.rstrip("\n"))
        if header:
            current = header.group(1)
            if current not in functions:
                functions[current] = {}
                order.append(current)
            continue
        if current is None:
            continue
        match = LLDB_INSTRUCTION.match(line.rstrip("\n"))
        if not match:
            if line.startswith("(lldb)"):
                current = None
            continue
        body, _, comment = match.group(2).partition(";")
        text = re.sub(r"\s+", " ", body.strip())
        comment = comment.strip()
        if comment and not comment.startswith("<+") and not comment.startswith("="):
            text = f"{text} <{comment}>"
        functions[current].setdefault(int(match.group(1), 16), text)
    for name in order:
        yield name, [(f"{address:x}", text) for address, text in sorted(functions[name].items())]


def detect_format(path: Path) -> str:
    with path.open(errors="replace") as stream:
        for _, line in zip(range(4000), stream):
            if LLDB_HEADER.match(line.rstrip("\n")) or line.startswith("(lldb)"):
                return "lldb"
            if SYMBOL.match(line):
                return "objdump"
    return "objdump"


def wrappers(path: Path, fmt: str = "auto"):
    fmt = detect_format(path) if fmt == "auto" else fmt
    with path.open(errors="replace") as stream:
        if fmt == "lldb":
            yield from lldb_wrappers_from_lines(stream)
        else:
            yield from wrappers_from_lines(stream)


def register_write_state(instruction: str, register: int) -> bool | None:
    """Return True/False for a proved write/non-write, or None when the form is unknown."""
    pair = PAIR_DESTINATION.match(instruction)
    if pair:
        destinations = (pair.group(3), pair.group(5))
        return any(name.isdigit() and int(name) == register for name in destinations)

    mnemonic = instruction.split(None, 1)[0] if instruction else ""
    if mnemonic in STORE_EXCLUSIVE:
        destination = DESTINATION.match(instruction)
        return bool(destination and destination.group(3).isdigit()
                    and int(destination.group(3)) == register)
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


def operands(instruction: str):
    """Split an instruction's operands on top-level commas, dropping any ``<annotation>``."""
    body = re.sub(r"\s*<[^>]*>\s*$", "", instruction)
    parts = body.split(None, 1)
    if len(parts) < 2:
        return parts[0] if parts else "", []
    result, depth, current = [], 0, ""
    for char in parts[1]:
        if char in "[{":
            depth += 1
        elif char in "]}":
            depth -= 1
        if char == "," and depth == 0:
            result.append(current.strip())
            current = ""
        else:
            current += char
    if current.strip():
        result.append(current.strip())
    return parts[0], result


def mentions(operand: str, register: int) -> bool:
    return any(int(number) == register for _, number in REGISTER.findall(operand))


def register_read_state(instruction: str, register: int) -> bool | None:
    """Return True/False for a proved read/non-read of ``register``, None for an unknown form."""
    mnemonic, ops = operands(instruction)
    if not any(mentions(op, register) for op in ops):
        return False
    if mnemonic in NO_DESTINATION or mnemonic.startswith("b."):
        return True  # cbz/tbz/cmp/blr/br: every register operand is a source.
    if mnemonic in STORE_EXCLUSIVE:
        return any(mentions(op, register) for op in ops[1:])
    if mnemonic.startswith("st"):
        return True  # Stored values and the address base are all sources.
    if PAIR_DESTINATION.match(instruction):
        return any(mentions(op, register) for op in ops[2:])
    if mnemonic in READ_MODIFY_DESTINATION:
        return True
    if mnemonic in SINGLE_DESTINATION or mnemonic.startswith("ld"):
        return any(mentions(op, register) for op in ops[1:])
    return None


def is_known_form(instruction: str) -> bool:
    """True when the mnemonic's register and control-flow effects are modelled above. Anything
    else (an authenticated branch or return, a system instruction) could redefine x20 or leave the
    body without naming it, so the walker treats it as unknown."""
    mnemonic = instruction.split(None, 1)[0] if instruction else ""
    return (mnemonic in SINGLE_DESTINATION or mnemonic in READ_MODIFY_DESTINATION
            or mnemonic in NO_DESTINATION or mnemonic in STORE_EXCLUSIVE or mnemonic in STORE
            or mnemonic.startswith("b.") or bool(PAIR_DESTINATION.match(instruction)))


def branch_successors(instruction: str, index: int, by_address: dict[int, int], count: int):
    """Return (successor indices, note). A note marks a path that leaves the analysable body,
    including one that runs past the last supplied instruction."""
    mnemonic, ops = operands(instruction)
    if mnemonic in ("ret", "brk"):
        return [], None
    if mnemonic == "br":
        return [], "indirect jump"
    past_end = f"path runs past the supplied instructions after: {instruction}"
    if mnemonic == "b" or mnemonic.startswith("b.") or mnemonic in CONDITIONAL_BRANCH:
        target = TARGET.search(ops[-1]) if ops else None
        successors = [] if mnemonic == "b" else [index + 1]
        note = None
        if target is None:
            note = f"unresolved branch target: {instruction}"
        elif int(target.group(1), 16) in by_address:
            successors.append(by_address[int(target.group(1), 16)])
        else:
            note = f"branch leaves the wrapper: {instruction}"
        if index + 1 in successors and index + 1 >= count:
            note = note or past_end
        return [s for s in successors if s < count], note
    return ([index + 1], None) if index + 1 < count else ([], past_end)


def post_call_reads(instructions, call: int, register: int):
    """Walk every path from the instruction after ``call`` until ``register`` is redefined.
    Returns (definite reads, unknown forms, notes) as instruction indices / strings; a note is a
    path that could not be followed to its end, so with no definite read it fails closed too."""
    by_address = {}
    for index, (address, _) in enumerate(instructions):
        by_address.setdefault(int(address, 16), index)
    reads, unknown, notes = [], [], []
    seen = set()
    work = [call + 1] if call + 1 < len(instructions) else []
    if not work:
        notes.append("path runs past the supplied instructions after the native call")
    while work:
        index = work.pop()
        if index in seen:
            continue
        seen.add(index)
        text = instructions[index][1]
        read = register_read_state(text, register) if is_known_form(text) else None
        if read is True:
            reads.append(index)
            continue
        if read is None:
            unknown.append(index)
            continue
        if register_write_state(text, register) is True:
            continue
        successors, note = branch_successors(text, index, by_address, len(instructions))
        if note:
            notes.append(note)
        work.extend(successors)
    return sorted(reads), sorted(unknown), notes


def native_call_index(symbol: str, instructions):
    direct = [i for i, (_, text) in enumerate(instructions)
              if BRANCH_LINK.match(text) and NATIVE_CALL in text]
    if direct:
        return direct[0]
    if INDIRECT_WRAPPER in symbol:
        exit_call = next((i for i, (_, text) in enumerate(instructions)
                          if BRANCH_LINK.match(text) and EXIT in text), len(instructions))
        indirect = [i for i in range(exit_call) if INDIRECT_BRANCH.match(instructions[i][1])]
        return indirect[-1] if indirect else None
    enter = next((i for i, (_, text) in enumerate(instructions)
                  if BRANCH_LINK.match(text) and ENTER in text), None)
    if enter is None:
        return None
    exit_call = next((i for i in range(enter + 1, len(instructions))
                      if BRANCH_LINK.match(instructions[i][1]) and EXIT in instructions[i][1]), None)
    return None if exit_call is None else linked_native_call(instructions, enter, exit_call)


def linked_native_call(instructions, after: int, exit_call: int):
    """A native library linked into the executable (the BCL's AppleCrypto shims, for one) is
    called by a direct ``bl`` to its own address, not through an icall PLT stub. Accept that only
    when it is the one branch-link between ``after`` and the GC-safe-region exit that is not a
    Mono runtime icall; any other shape stays unresolved."""
    branch_links = [i for i in range(after + 1, exit_call)
                    if BRANCH_LINK.match(instructions[i][1]) and "jit_icall" not in instructions[i][1]]
    if len(branch_links) != 1 or not DIRECT_BRANCH_LINK.match(instructions[branch_links[0]][1]):
        return None
    return branch_links[0]


def is_lmf_push(instructions, park: int, register: int) -> bool:
    """Mono's LMF push: ``mov xR, x0`` (the LMF address) then ``ldr xA, [xR]`` (previous LMF)
    and ``str xB, [xR]`` (this frame's LMF) within the next few instructions."""
    loads = stores = False
    pattern = re.compile(rf"\[x{register}\]")
    for _, text in instructions[park + 1:park + 8]:
        if pattern.search(text):
            if text.startswith("ldr"):
                loads = True
            elif text.startswith("str") and loads:
                stores = True
                break
        if BRANCH_LINK.match(text):
            break
    return loads and stores


def park_origin(instructions, park: int, register: int) -> str:
    """Name the value a ``mov xR, x0`` park holds: the result of the nearest preceding call,
    provided nothing in between (e.g. a swifterror writeback) redefined x0."""
    if not COOKIE_REGISTER.match(instructions[park][1]):
        return "other"
    for index in range(park - 1, -1, -1):
        text = instructions[index][1]
        if BRANCH_LINK.match(text):
            if ENTER in text:
                return "cookie"
            if "mono_get_lmf_addr" in text or is_lmf_push(instructions, park, register):
                return "lmf"
            return "other"
        if register_write_state(text, 0) is not False:
            return "other"
    return "other"


def is_call_result_park(instructions, last_call: int, setup: int) -> bool:
    """True when ``setup`` is ``mov xR, x0`` holding the preceding helper call's result, i.e. x0
    was not redefined since that call (a swifterror writeback may sit in between)."""
    if last_call < 0 or not COOKIE_REGISTER.match(instructions[setup][1]):
        return False
    return all(register_write_state(text, 0) is False
               for _, text in instructions[last_call + 1:setup])


def previous_write(instructions, before: int, register: int, stop: int = 0):
    """Index of the last write to ``register`` in [stop, before), or None. None in the result
    tuple's second slot means an unknown form was crossed, which fails the analysis closed."""
    for index in range(before - 1, stop - 1, -1):
        if not is_known_form(instructions[index][1]):
            return index, None
        state = register_write_state(instructions[index][1], register)
        if state is True:
            return index, True
        if state is None and any(mentions(op, register)
                                 for op in operands(instructions[index][1])[1]):
            return index, None
    return None, True


def classify_register(symbol: str, instructions, call: int, register: int):
    """General post-call liveness classification of one Swift-reserved register."""
    last_call = max((i for i in range(call) if BRANCH_LINK.match(instructions[i][1])), default=-1)
    setup, known = previous_write(instructions, call, register, stop=last_call + 1)
    if not known:
        return {"verdict": "unparsed", "note": f"unknown form: {instructions[setup][1]}"}
    if setup is None or is_call_result_park(instructions, last_call, setup):
        # The call does not load this register (a park of the preceding helper call's result is
        # not argument setup), so whatever the wrapper parked there survives the call.
        return {"verdict": "not_loaded"}
    park, known = previous_write(instructions, setup, register)
    if not known:
        return {"verdict": "unparsed", "note": f"unknown form: {instructions[park][1]}",
                "setup": instructions[setup][1]}
    origin = "entry" if park is None else park_origin(instructions, park, register)
    result = {
        "setup": instructions[setup][1],
        "park": None if park is None else instructions[park][1],
        "park_origin": origin,
    }
    reads, unknown, notes = post_call_reads(instructions, call, register)
    result["post_call_reads"] = [instructions[i][1] for i in reads]
    if notes:
        result["notes"] = sorted(set(notes))
    entry_x21 = register == 21 and origin == "entry"
    if reads and not entry_x21:
        # A post-call x21 read may be the writeback rather than the parked value, so an x21 park
        # of anything but the LMF or the cookie (values Mono always consumes) needs a human look.
        result["verdict"] = "review" if register == 21 and origin == "other" else "victim"
    elif unknown or notes:
        result["verdict"] = "unparsed"
        result["note"] = (f"unknown form after the call: {instructions[unknown[0]][1]}" if unknown
                          else f"incomplete path after the call: {sorted(set(notes))[0]}")
    else:
        # With no park, x21's post-call reads are the error writeback: nothing can be stale.
        result["verdict"] = "safe"
    return result


def classify_x20(symbol: str, instructions):
    call = native_call_index(symbol, instructions)
    if call is None:
        return {"x20": {"verdict": "unparsed", "note": "native call boundary not found"}}
    result = {"native_call": instructions[call][1], "x20": classify_register(symbol, instructions, call, 20)}
    if SWIFT_ERROR in symbol:
        result["x21"] = classify_register(symbol, instructions, call, 21)
    return result


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
        call = linked_native_call(instructions, park[0], exit_call)
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


def classify_all(symbol: str, instructions):
    result = classify(symbol, instructions)
    result.update(classify_x20(symbol, instructions))
    result["untyped_swiftself"] = bool(UNTYPED_SWIFT_SELF.search(symbol))
    result["swifterror"] = SWIFT_ERROR in symbol
    return result


def victim_label(item) -> str:
    return "+".join(f"{register}:{item[register]['park_origin']}" for register in ("x20", "x21")
                    if item.get(register, {}).get("verdict") == "victim")


def self_test() -> None:
    def insns(*texts):
        return [(f"{index * 4:04x}", text) for index, text in enumerate(texts)]

    enter = "bl 0x100 <_mono_threads_enter_gc_safe_region_unbalanced>"
    exit_ = "bl 0x200 <_mono_threads_exit_gc_safe_region_unbalanced>"
    native = "bl 0x300 <plt__icall_native_test>"
    symbol = "wrapper_managed_to_native_Test_System_Runtime_InteropServices_Swift_SwiftSelf"

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

    assert register_read_state("str x0, [x20]", 20) is True
    assert register_read_state("mov x0, x20", 20) is True
    assert register_read_state("ldr x1, [x20, #0x8]", 20) is True
    assert register_read_state("mov x20, x3", 20) is False
    assert register_read_state("ldp x19, x20, [x29, #0xd8]", 20) is False
    assert register_read_state("movk x20, #0x1, lsl #16", 20) is True
    assert register_read_state("cbz x20, 0x10", 20) is True
    assert register_read_state("ldr x0, [x29, #0x20]", 20) is False
    assert register_read_state("mystery x0, x20", 20) is None

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

    # Ground truth: Mac Catalyst SwiftBridgeProbe.combine (net10, Xcode 26.3), whose LMF pop
    # overwrote self's isa at register level. Abbreviated lldb transcript, real offsets; the
    # cookie is safe in x22, so the cookie-only verdict calls it safe while x20 is a victim.
    combine = (
        "RuntimeTestsApp.MacCatalyst`wrapper_managed_to_native_X_PInvoke_combine_00D469F9_long_long___"
        "System_Runtime_InteropServices_Swift_SwiftSelf:\n"
        + "".join(f"RuntimeTestsApp.MacCatalyst[0x{0x107407630 + offset:x}] <+{offset}>: {text}\n"
                  for offset, text in (
                      (0, "stp x29, x30, [sp, #-0x190]!"),
                      (16, "stp x19, x20, [x29, #0xd8]"),
                      (64, "str x3, [x29, #0x30]"),
                      (172, "ldr x0, [x16, #0x58]"),
                      (176, "blr x0"),
                      (180, "mov x20, x0"),
                      (184, "add x0, x29, #0xc0"),
                      (188, "ldr x1, [x20]"),
                      (192, "str x1, [x29, #0xc0]"),
                      (196, "str x0, [x20]"),
                      (240, "add x0, x29, #0xb8"),
                      (244, "bl 0x10d05c428 ; mono_threads_enter_gc_safe_region_unbalanced at mono-threads-coop.c:300"),
                      (248, "mov x22, x0"),
                      (292, "ldr x3, [x29, #0x80]"),
                      (296, "mov x20, x3"),
                      (300, "bl 0x107b39590 ; plt__icall_native_X_PInvoke_combine_00D469F9"),
                      (304, "mov x0, x22"),
                      (312, "bl 0x10d05c504 ; mono_threads_exit_gc_safe_region_unbalanced at mono-threads-coop.c:413"),
                      (376, "ldr w0, [x0]"),
                      (380, "cbz w0, 0x1074077f0 ; <+448>"),
                      (384, "bl 0x107b084a4 ; plt__jit_icall_mono_thread_interruption_checkpoint"),
                      (440, "bl 0x107a0325c ; plt__jit_icall_mono_arch_rethrow_exception"),
                      (448, "add x0, x29, #0x138"),
                      (500, "ldr x0, [x29, #0xc0]"),
                      (504, "str x0, [x20]"),
                      (576, "ldp x19, x20, [x29, #0xd8]"),
                      (588, "ldp x29, x30, [sp], #0x190"),
                      (592, "ret"),
                  ))
        + "(lldb) disassemble -n next\n")
    [(name, body)] = list(lldb_wrappers_from_lines(io.StringIO(combine)))
    item = classify_all(name, body)
    assert item["verdict"] == "safe" and item["cookie_reg"] == "x22", item
    assert item["x20"]["verdict"] == "victim" and item["x20"]["park_origin"] == "lmf", item
    assert item["x20"]["post_call_reads"] == ["str x0, [x20]"], item
    assert victim_label(item) == "x20:lmf"

    # Cookie victim (Catalyst IndirectSource.wrap shape): LMF in x19, cookie parked in x20.
    cookie = classify_all(symbol, insns(
        "blr x0", "mov x19, x0", "add x0, x29, #0x68", "ldr x1, [x19]", "str x1, [x29, #0x68]",
        "str x0, [x19]", enter, "mov x20, x0", "ldr x3, [x29, #0x50]", "mov x20, x3", native,
        "str x0, [x29, #0x110]", "mov x0, x20", exit_, "str x0, [x19]",
        "ldp x19, x20, [x29, #0x80]", "ret"))
    assert cookie["verdict"] == "clobber" and victim_label(cookie) == "x20:cookie", cookie

    # Safe control: LMF in x23, cookie in x24; x20 is only self and is restored, never read.
    safe_all = classify_all(symbol, insns(
        "blr x0", "mov x23, x0", "add x0, x29, #0x68", "ldr x1, [x23]", "str x1, [x29, #0x68]",
        "str x0, [x23]", enter, "mov x24, x0", "ldr x3, [x29, #0x50]", "mov x20, x3", native,
        "mov x0, x24", exit_, "str x0, [x23]", "ldp x19, x20, [x29, #0x80]", "ret"))
    assert safe_all["x20"]["verdict"] == "safe" and victim_label(safe_all) == "", safe_all

    # A read reachable only through a branch is still found; a redefinition first is not a read.
    branchy = classify_all(symbol, insns(
        "blr x0", "mov x20, x0", "ldr x1, [x20]", "str x0, [x20]", "mov x20, x3", native,
        "cbz w0, 0x24", "mov x20, x1", "ret", "str x0, [x20]", "ret"))
    assert branchy["x20"]["verdict"] == "victim", branchy
    redefined = classify_all(symbol, insns(
        "blr x0", "mov x20, x0", "ldr x1, [x20]", "str x0, [x20]", "mov x20, x3", native,
        "mov x20, x1", "str x0, [x20]", "ret"))
    assert redefined["x20"]["verdict"] == "safe", redefined

    # A call that never loads x20 (cdecl, or self passed elsewhere) cannot clobber it.
    untouched = classify_all(symbol, insns(
        "blr x0", "mov x20, x0", "ldr x1, [x20]", "str x0, [x20]", "mov x0, x3", native,
        "str x0, [x20]", "ret"))
    assert untouched["x20"]["verdict"] == "not_loaded", untouched

    # Unknown forms after the call fail closed, including unmodelled control flow that names no
    # x20 operand, and so does a path that leaves the supplied instructions.
    lmf_prefix = ("blr x0", "mov x20, x0", "ldr x1, [x20]", "str x0, [x20]", "mov x20, x3", native)
    for tail in (("mystery x0, x20", "ret"), ("braa x16, x17", "mov x20, x1", "ret"),
                 ("retaa",), ("b 0x9999",), ("ldr x0, [x29, #0xc0]",), (),
                 ("st64b x16, [x1]", "ret")):
        closed = classify_all(symbol, insns(*lmf_prefix, *tail))
        assert closed["x20"]["verdict"] == "unparsed", (tail, closed)
    # ...and so does an unmodelled form before the call, where it could hide the setup or park.
    signed = classify_all(symbol, insns("mov x20, x9", "pacia x20, x3", "mov x20, x4", native, "ret"))
    assert signed["x20"]["verdict"] == "unparsed", signed

    # x21: an LMF/cookie park overwritten by the swifterror zeroing is a victim; a plain zeroing
    # with no earlier park is safe even though the writeback then reads x21.
    error_symbol = symbol + "_System_Runtime_InteropServices_Swift_SwiftError_"
    x21_victim = classify_all(error_symbol, insns(
        enter, "mov x21, x0", "mov x20, x3", "mov x21, #0x0", native, "str x21, [x16]",
        "mov x0, x21", exit_, "ret"))
    assert x21_victim["x21"]["verdict"] == "victim" and victim_label(x21_victim) == "x21:cookie"
    x21_safe = classify_all(error_symbol, insns(
        enter, "mov x22, x0", "mov x20, x3", "mov x21, #0x0", native, "str x21, [x16]",
        "mov x0, x22", exit_, "ret"))
    assert x21_safe["x21"]["verdict"] == "safe" and victim_label(x21_safe) == ""
    x21_unknown = classify_all(error_symbol, insns(
        enter, "mov x22, x0", "mov x20, x3", "mov x21, #0x0", native, "mystery x21", "ret"))
    assert x21_unknown["x21"]["verdict"] == "unparsed", x21_unknown
    # ...a park the wrapper reloads before any post-call read is not consumed stale...
    x21_reloaded = classify_all(error_symbol, insns(
        enter, "mov x21, x0", "mov x20, x3", "mov x21, #0x0", native, "ldr x21, [x29, #0x10]",
        "mov x0, x21", exit_, "ret"))
    assert x21_reloaded["x21"]["verdict"] == "safe", x21_reloaded
    # ...and a wrapper with victims in both registers reports both.
    both = classify_all(error_symbol, insns(
        *lmf_prefix[:4], enter, "mov x21, x0", "mov x20, x3", "mov x21, #0x0", native,
        "str x21, [x16]", "mov x0, x21", exit_, "str x0, [x20]", "ret"))
    assert victim_label(both) == "x20:lmf+x21:cookie", both

    # Catalyst SwiftBridgeProbe.accumulate shape: a swifterror writeback sits between the GC
    # entry and the cookie park; the park still names the cookie.
    delayed = classify_all(error_symbol, insns(
        enter, "ldr x16, [x29, #0x30]", "str x21, [x16]", "mov x20, x0", "ldr x2, [x29, #0x50]",
        "mov x20, x2", "mov x21, #0x0", native, "ldr x16, [x29, #0x30]", "str x21, [x16]",
        "mov x0, x20", exit_, "ret"))
    assert victim_label(delayed) == "x20:cookie", delayed
    # ...but a park whose x0 was redefined after the call is not attributed to that call.
    unattributed = classify_all(symbol, insns(
        enter, "ldr x0, [x29, #0x30]", "mov x20, x0", "mov x20, x2", native, "mov x0, x20",
        exit_, "ret"))
    assert victim_label(unattributed) == "x20:other", unattributed
    # Catalyst AutoclosureBridgeHost.evaluate shape: SwiftError only, the cookie parked in x20
    # after the writeback and never overwritten — x20 is not an argument of this call.
    error_only = classify_all(
        "wrapper_managed_to_native_X_System_Runtime_InteropServices_Swift_SwiftError_", insns(
            "blr x0", "mov x19, x0", enter, "ldr x16, [x29, #0x30]", "str x21, [x16]",
            "mov x20, x0", "ldr x2, [x29, #0x20]", "mov x21, #0x0", native, "str x21, [x16]",
            "mov x0, x20", exit_, "ret"))
    assert error_only["x20"]["verdict"] == "not_loaded" and error_only["verdict"] == "safe", error_only

    # A native linked into the executable (BCL AppleCrypto shape) is a direct bl with no PLT name;
    # it is the boundary only when it is the sole non-icall branch-link inside the transition.
    linked_call = "bl 0x10d603cd4 <_unrelated_exported_symbol+0x10d5fe171>"
    checkpoint = "bl 0x400 <plt__jit_icall_mono_thread_interruption_checkpoint>"
    linked = classify_all(error_symbol, insns(
        "blr x0", "mov x19, x0", "ldr x1, [x19]", "str x0, [x19]", enter, "ldr x16, [x28, #0x20]",
        "str x21, [x16]", "mov x22, x0", checkpoint, "mov x21, #0x0", linked_call,
        "str x21, [x16]", "mov x0, x22", exit_, "str x0, [x19]", "ret"))
    assert linked["native_call"] == linked_call and linked["verdict"] == "safe", linked
    assert linked["x20"]["verdict"] == "not_loaded" and linked["x21"]["verdict"] == "safe", linked
    ambiguous = classify_all(error_symbol, insns(
        enter, "mov x22, x0", linked_call, "bl 0x500 <other>", "mov x0, x22", exit_, "ret"))
    assert ambiguous["x20"]["verdict"] == "unparsed" and ambiguous["verdict"] == "unparsed"
    helper_only = classify_all(symbol, insns(enter, "mov x22, x0", "blr x9", "mov x0, x22", exit_, "ret"))
    assert helper_only["x20"]["verdict"] == "unparsed" and helper_only["verdict"] == "unparsed"

    assert UNTYPED_SWIFT_SELF.search(symbol)
    assert not UNTYPED_SWIFT_SELF.search(
        "wrapper_managed_to_native_X_System_Runtime_InteropServices_Swift_SwiftSelf_1_X_Buffer")

    print(f"{VERSION}: streaming, cookie and x20/x21 victim classification controls passed")


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
    parser.add_argument("--format", choices=("auto", "objdump", "lldb"), default="auto")
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
    for symbol, instructions in wrappers(args.assembly, args.format):
        total += 1
        if args.carrier in symbol:
            selected.append(classify_all(symbol, instructions))

    untyped = [item for item in selected if item["untyped_swiftself"]]
    payload = {
        "classifier_version": VERSION,
        "assembly": str(args.assembly),
        "assembly_sha256": sha256(args.assembly),
        "carrier": args.carrier,
        "managed_to_native_wrappers": total,
        "selected_wrappers": len(selected),
        "verdicts": dict(sorted(Counter(item["verdict"] for item in selected).items())),
        "untyped_swiftself_wrappers": len(untyped),
        "x20_verdicts": dict(sorted(Counter(
            item["x20"]["verdict"] for item in untyped).items())),
        "victims": dict(sorted(Counter(
            victim_label(item) for item in selected if victim_label(item)).items())),
        "results": selected,
    }
    print(json.dumps({key: value for key, value in payload.items() if key != "results"}, indent=2))
    for item in selected:
        if item["verdict"] == "clobber":
            print(f"CLOBBER {item['cookie_reg']} {item['symbol']}")
    for item in selected:
        label = victim_label(item)
        if label:
            reads = {register: item[register]["post_call_reads"] for register in ("x20", "x21")
                     if item.get(register, {}).get("verdict") == "victim"}
            print(f"VICTIM {label} {item['symbol']} reads={reads}")
    for item in selected:
        for register in ("x20", "x21"):
            verdict = item.get(register, {}).get("verdict")
            if verdict in ("unparsed", "review"):
                print(f"{verdict.upper()} {register} {item['symbol']} "
                      f"{item[register].get('note', item[register].get('park', ''))}")
    if args.out:
        args.out.write_text(json.dumps(payload, indent=2) + "\n")


if __name__ == "__main__":
    main()
