# OWNER-03 Mono full-AOT `x20` cookie-clobber resolution

Date: 2026-09-15
Starting commit: `03f793ae3c30edcfd594fcd285adee1dae8f55a9`
Classifier: `build/scripts/classify-mono-aot-cookie.py`, version `owner03-x20-v2`

## Result

The binary was regenerated and classified before any source change. It contained 52 managed-to-native wrappers whose symbols carried `SwiftSelf`: 40 safe, 9 cookie clobbers, 2 cookie spills and 1 with no GC transition. Every clobber parked the Mono GC-safe-region cookie in `x20` and overwrote `x20` while loading `SwiftSelf` immediately before the native branch.

The eligible members were moved by shape onto existing principled `@_cdecl` routes:

- generic-parent property access uses generic static dispatch after reconstructing metadata through the real buffer-mode metadata-accessor ABI;
- a method-own generic returned as bare `T` is initialized from inside the opened generic body on the existential-opening strategies that provide its local generic type;
- an imported protocol named by the specialization registry is accepted as a proved protocol identity for method-level opening.

The post-fix binary contains 37 `SwiftSelf` managed-to-native wrappers: 35 safe, 1 spill and 1 with no transition. It contains no clobber classification. The nine pre-fix wrapper identities below are absent from the post-fix classifier receipt and their original mangled Swift entry points are absent from generated C#.

## Reproduction receipt

Pre-fix gate:

```text
nuke binding-tests --device --mono-aot
4067 pass, 0 fail, 32 skip, 0 crash
RuntimeTestsApp executable SHA-256: 4608568079cb54b44f56cedec9cbaa0ce913647423a4d382575a4b077d61c212
```

Classifier commands (Apple LLVM 17.0.0, Python 3.14.6):

```text
/usr/bin/xcrun llvm-objdump --disassemble --no-show-raw-insn \
  BindingTests/RuntimeTestsApp/bin/Debug/net10.0-ios/ios-arm64/RuntimeTestsApp.app/RuntimeTestsApp \
  > /private/tmp/owner03-x20-{prefx|postfx}-monoaot.asm
python3 build/scripts/classify-mono-aot-cookie.py --self-test
python3 build/scripts/classify-mono-aot-cookie.py \
  /private/tmp/owner03-x20-{prefx|postfx}-monoaot.asm \
  --out /private/tmp/owner03-x20-{prefx|postfx}-cookie-v2.json
```

| Artifact | SHA-256 | Classifier result |
| --- | --- | --- |
| Pre-fix executable | `4608568079cb54b44f56cedec9cbaa0ce913647423a4d382575a4b077d61c212` | — |
| Pre-fix disassembly | `5c3591c3eb635743dac472a4878dc6b154e3b224cac575d4c385dd0c3faa1592` | 14,573 managed wrappers; 52 selected; safe 40 / clobber 9 / spilled 2 / no-transition 1 |
| Pre-fix JSON receipt | `3a8c7b70927bd1a5d2f5ccddd4756c3e4637a5a6e14b8f09517bf1783967619f` | contains the exact instruction window for every selected wrapper |
| Post-fix executable | `a27bf5e6d165a4c9bd8cd5463f91a5fb8714693d1ac28c7255afcbd50a9fc9f9` | — |
| Post-fix disassembly | `16304e1238504a622eb2ef00af7e88008492dc14707d3033662be1f8e6cc03b6` | 14,576 managed wrappers; 37 selected; safe 35 / clobber 0 / spilled 1 / no-transition 1 |
| Post-fix JSON receipt | `c32b5c3126ffa5513262bf6a0d65523e55b7c1c105ad5f810c90c4bf4603d5e7` | all nine pre-fix wrapper identity hashes occur zero times |
| Classifier source | `131abfe107dbd9481dbdb86e8056f4f88186540a92b15be525289cfe7ce888c2` | `owner03-x20-v2`; full streaming and verdict self-test passed |

The JSON files are reproducible build receipts rather than checked-in binaries. The checked-in classifier streams the approximately 1.1 GiB disassembly and records its input hash, exact cookie park, all instructions through the native branch, callee and exit-transition presence. Direct wrappers require an explicit `plt__icall_native` boundary; only Mono's `_Module_wrapper_native_indirect_` shape may use the final `blr` before the GC-exit call. An unknown register-write form, missing native boundary or missing exit produces `unparsed`, never `safe`.

## Review repair pass

The coordinator-requested Medium/Low pass made two correctness repairs and pinned the nearby boundaries:

- bare own-generic returns are admitted only for `Existential` and `ParameterizedExistential` opening. `SuperclassCarrier` and `AssociatedTypeCarrier` keep their established direct route because their module-scope scaffolding cannot name the locally opened `T` needed for result initialization. Focused unit tests cover both declines and the exact supported existential Swift/C# ABI;
- classifier version `owner03-x20-v2` no longer mistakes an earlier helper `blr` for the native call, recognizes `ldp`/`ldnp`/`ldpsw` pair destinations including `xzr`, terminates on malformed/truncated symbol records, preserves Mono's legitimate Module indirect wrapper, and fails closed on unknown write forms or incomplete transitions. Its self-test covers streaming, EOF yield, native-boundary selection and every verdict;
- constructor dispatch explicitly declines the first over-bound case (four metadata slots, beyond the supported maximum of three), and a generic-struct property getter now has a direct two-metadata-plus-two-PWT packed-vector oracle;
- stale buffer-mode revisit language and the historical 85-member surface count are timestamped and separated from the current binary result.

No worthwhile local Low item from this pass was deferred. The repair narrows only an unsupported carrier-return admission and hardens evidence tooling; it does not change any of the nine supported post-fix ABIs, so the already-recorded physical-device gate was not rerun.

## Per-identity ABI proof

In the table, sequence `A`, `B` or `C` means the exact pre-native instruction window printed in the following section. All pre-fix declarations use `CallConvSwift`; all post-fix declarations use `CallConvCdecl`. `SIR` below expands exactly to `System.Runtime.InteropServices.Swift.SwiftIndirectResult`, `SS` to `System.Runtime.InteropServices.Swift.SwiftSelf`, and `IP` to `IntPtr`.

| Identity | Pre-fix managed wrapper / cookie proof | Exact pre-fix generated C# P/Invoke ABI | Exact post-fix generated Swift `@_cdecl` and C# P/Invoke ABI | Post-fix exposure proof |
| --- | --- | --- | --- | --- |
| `BufferModeDescribablePair.first` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeDescribablePair_PInvoke_PInvoke_first_Get_D829DA84_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib25BufferModeDescribablePairV5firstxvg`; `void PInvoke_first_Get_D829DA84(SIR swiftIndirectResult, IP KMetadata, IP VMetadata, IP KDescribablePWT, IP VDescribablePWT, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeDescribablePair_first") public func _sbw_get_first_4164C864(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _pwt0: UnsafeRawPointer, _ _pwt1: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_first_Get_4164C864(IP resultPtr, IP KMetadata, IP VMetadata, IP KDescribablePWT, IP VDescribablePWT, IP _self)` | old hash `D829DA84`: 0 post-classifier results; old mangled entry absent from generated C# |
| `BufferModeDescribablePair.second` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeDescribablePair_PInvoke_PInvoke_second_Get_BA50E347_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib25BufferModeDescribablePairV6secondq_vg`; `void PInvoke_second_Get_BA50E347(SIR swiftIndirectResult, IP KMetadata, IP VMetadata, IP KDescribablePWT, IP VDescribablePWT, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeDescribablePair_second") public func _sbw_get_second_7D620292(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _pwt0: UnsafeRawPointer, _ _pwt1: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_second_Get_7D620292(IP resultPtr, IP KMetadata, IP VMetadata, IP KDescribablePWT, IP VDescribablePWT, IP _self)` | old hash `BA50E347`: 0; old mangled entry absent |
| `BufferModeQuad.first` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeQuad_PInvoke_PInvoke_first_Get_A4723E05_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib14BufferModeQuadV5firstxvg`; `void PInvoke_first_Get_A4723E05(SIR swiftIndirectResult, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeQuad_first") public func _sbw_get_first_E9E9962B(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _metadata2: UnsafeRawPointer, _ _metadata3: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_first_Get_E9E9962B(IP resultPtr, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, IP _self)` | old hash `A4723E05`: 0; old mangled entry absent |
| `BufferModeQuad.second` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeQuad_PInvoke_PInvoke_second_Get_7DF139CE_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib14BufferModeQuadV6secondq_vg`; `void PInvoke_second_Get_7DF139CE(SIR swiftIndirectResult, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeQuad_second") public func _sbw_get_second_01227717(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _metadata2: UnsafeRawPointer, _ _metadata3: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_second_Get_01227717(IP resultPtr, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, IP _self)` | old hash `7DF139CE`: 0; old mangled entry absent |
| `BufferModeQuad.third` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeQuad_PInvoke_PInvoke_third_Get_61E71FB6_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib14BufferModeQuadV5thirdq0_vg`; `void PInvoke_third_Get_61E71FB6(SIR swiftIndirectResult, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeQuad_third") public func _sbw_get_third_B804A448(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _metadata2: UnsafeRawPointer, _ _metadata3: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_third_Get_B804A448(IP resultPtr, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, IP _self)` | old hash `61E71FB6`: 0; old mangled entry absent |
| `BufferModeQuad.fourth` | `wrapper_managed_to_native_SwiftBindingsTestLib_BufferModeQuad_PInvoke_PInvoke_fourth_Get_1B0700BB_System_Runtime_InteropServices_Swift_SwiftIndirectResult_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence A | Entry `$s20SwiftBindingsTestLib14BufferModeQuadV6fourthq1_vg`; `void PInvoke_fourth_Get_1B0700BB(SIR swiftIndirectResult, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, SS self)` | `@_cdecl("SBW_Get_SwiftBindingsTestLib_BufferModeQuad_fourth") public func _sbw_get_fourth_6786BCDF(_ resultPtr: UnsafeMutableRawPointer, _ _metadata0: UnsafeRawPointer, _ _metadata1: UnsafeRawPointer, _ _metadata2: UnsafeRawPointer, _ _metadata3: UnsafeRawPointer, _ self_: UnsafeRawPointer)`; `void PInvoke_fourth_Get_6786BCDF(IP resultPtr, IP AMetadata, IP BMetadata, IP CMetadata, IP DMetadata, IP _self)` | old hash `1B0700BB`: 0; old mangled entry absent |
| `CarrierBox.relayThrough` | `wrapper_managed_to_native_SwiftBindingsTestLib_CarrierBox_PInvoke_relayThrough_E17AE04F_System_Runtime_InteropServices_Swift_SwiftIndirectResult_int_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence C | Entry `$s20SwiftBindingsTestLib10CarrierBoxV12relayThrough9resultPtr4itemxs5Int32V_xtAA0E4ItemRzlF`; `void PInvoke_relayThrough_E17AE04F(SIR swiftIndirectResult, int resultPtr, IP itemPayload, IP TMetadata, IP TCarrierItemPWT, SS self)` | `@_cdecl("SBW_SwiftBindingsTestLib_CarrierBox_relayThrough_E17AE04F") public func _sbw_method_830D271B(_ resultPtr: UnsafeMutableRawPointer, _ __resultPtr2: Int32, _ itemPayload: UnsafeRawPointer, _ _metadata0: UnsafeRawPointer, _ self_: UnsafeRawPointer, _ _openRefused: UnsafeMutablePointer<UInt8>)`; `void PInvoke_relayThrough_830D271B(IP __resultPtr, int resultPtr, IP itemPayload, IP TMetadata, IP _self, out byte _openRefused)` | old hash `E17AE04F`: 0; old mangled entry absent |
| `DefaultedHasher.append` | `wrapper_managed_to_native_SwiftBindingsTestLib_DefaultedHasher_PInvoke_append_49B80544_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence B | Entry `$s20SwiftBindingsTestLib15DefaultedHasherV6append_7options3tagyx_ShySiGSit10Foundation12DataProtocolRzlF`; `void PInvoke_append_49B80544(IP dataPayload, IP optionsBuffer, nint tag, IP DMetadata, SS self)` | `@_cdecl("SBW_SwiftBindingsTestLib_DefaultedHasher_append_49B80544") public func _sbw_method_95258082(_ dataPayload: UnsafeRawPointer, _ options: UnsafeRawPointer, _ __tag: Int, _ _metadata0: UnsafeRawPointer, _ self_: UnsafeMutableRawPointer, _ _openRefused: UnsafeMutablePointer<UInt8>)`; `void PInvoke_append_95258082(IP dataPayload, IP optionsBuffer, nint tag, IP DMetadata, IP _self, out byte _openRefused)` | old managed symbol hash `49B80544`: 0; old mangled entry absent |
| `DefaultedHasherWithFile.append` | `wrapper_managed_to_native_SwiftBindingsTestLib_DefaultedHasherWithFile_PInvoke_append_C656DF2C_intptr_intptr_intptr_intptr_System_Runtime_InteropServices_Swift_SwiftSelf`; `x20`; sequence B | Entry `$s20SwiftBindingsTestLib23DefaultedHasherWithFileV6append_7options3tag4fileyx_ShySiGSis12StaticStringVt10Foundation12DataProtocolRzlF`; `void PInvoke_append_C656DF2C(IP dataPayload, IP optionsBuffer, nint tag, IP DMetadata, SS self)` | `@_cdecl("SBW_SwiftBindingsTestLib_DefaultedHasherWithFile_append_C656DF2C") public func _sbw_method_D67E1A71(_ dataPayload: UnsafeRawPointer, _ options: UnsafeRawPointer, _ __tag: Int, _ _metadata0: UnsafeRawPointer, _ self_: UnsafeMutableRawPointer, _ _openRefused: UnsafeMutablePointer<UInt8>)`; `void PInvoke_append_D67E1A71(IP dataPayload, IP optionsBuffer, nint tag, IP DMetadata, IP _self, out byte _openRefused)` | old hash `C656DF2C`: 0; old mangled entry absent |

### Exact pre-fix instruction windows

The cookie-register writes in every row are `mov x20, x0` at the park and `mov x20, x4` before the native branch. The intervening instructions, recorded verbatim by the classifier, are:

Sequence A (`BufferModeDescribablePair` and `BufferModeQuad` properties):

```asm
mov x20, x0
add x0, x29, #0x10
add x0, x29, #0x70
ldr x0, [x29, #0x10]
str x0, [x29, #0x70]
ldr x0, [x29, #0x20]
ldr x1, [x29, #0x28]
ldr x2, [x29, #0x30]
ldr x3, [x29, #0x38]
add x4, x29, #0x40
add x4, x29, #0x68
ldr x4, [x29, #0x40]
str x4, [x29, #0x68]
add x4, x29, #0x70
ldr x4, [x29, #0x70]
mov x8, x4
add x4, x29, #0x68
ldr x4, [x29, #0x68]
mov x20, x4
bl <the row's plt__icall_native target>
```

Sequence B (`DefaultedHasher` methods):

```asm
mov x20, x0
ldr x0, [x29, #0x10]
ldr x1, [x29, #0x18]
ldr x2, [x29, #0x20]
ldr x3, [x29, #0x28]
add x4, x29, #0x30
add x4, x29, #0x58
ldr x4, [x29, #0x30]
str x4, [x29, #0x58]
add x4, x29, #0x58
ldr x4, [x29, #0x58]
mov x20, x4
bl <the row's plt__icall_native target>
```

Sequence C (`CarrierBox.relayThrough`) is sequence A with `ldrsw x0, [x29, #0x20]` in place of the first argument's `ldr x0, [x29, #0x20]`; its cookie park, result-indirection setup, metadata/PWT setup, `SwiftSelf` clobber and native branch are otherwise byte-for-byte the same instruction forms.

## Validation

```text
nuke compile
Build succeeded; 0 warnings, 0 errors.

nuke UnitTests
19085 passed, 0 failed, 2 skipped.

nuke SeedApiManifestBaseline
Seeded schema-v1 API manifest baseline with 5659 entries for the intentional route retargets.

nuke binding-tests --compile-only
Generated 1707 C# files and 3 Swift wrapper files; generated C# and Swift compiled.
Wrapper-strip gate: 0 stripped; getter parity: 255; artifact parity: 0 new violations.
API-manifest gate: 5659 current / 5659 baselined, 0 added, 0 removed, 0 retargets.
Resilience, ingestion-closure and downstream compile-only gates passed.

Repair-pass logs:
/private/tmp/owner03-x20-review-unit-final.log
/private/tmp/owner03-x20-review-api-manifest-seed.log
/private/tmp/owner03-x20-review-binding-compile-final.log

nuke binding-tests --device --mono-aot
4072 passed, 0 failed, 32 skipped, 0 crashed.
The 4067-test baseline remains green; five new route regressions also pass.

python3 build/scripts/classify-mono-aot-cookie.py --self-test
owner03-x20-v2: streaming and fail-closed classification controls passed

python3 build/scripts/classify-mono-aot-cookie.py /private/tmp/owner03-x20-postfx-monoaot.asm \
  --out /private/tmp/owner03-x20-postfx-cookie-v2.json
37 selected; 35 safe, 1 spilled, 1 no-transition, 0 clobber.
```

No runtime skip, waiver, timeout or Mono-keyed route predicate changed. No external review command was run in the repair pass.
