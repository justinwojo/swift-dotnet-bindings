// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// NOTE: Direct-AnyType enum payloads ARE now reproduced in BindingTests.
//
// The skip-gates in EnumHandler.CaseConstruction.cs (the `HasUnsupportedAnyTypeInPayload`
// factory gate) and EnumHandler.CaseInspection.cs (lines 138 and 312) suppress the case
// factory and TryGet emission when an enum case's resolved C# payload type is
// "Swift.AnyType". This happens when a referenced Swift type cannot be resolved to a
// concrete managed binding and the TypeDatabase falls through to AnyType.
//
// Live BindingTests reproduction: `SimdMatrixPayload` in `Types/SimdFixtures.swift` has a
// `case affine2x2(simd_float2x2)`. The `simd` module is flagged `"valueTypesOnly": true` in
// apple-frameworks.json (simd declares zero ObjC classes), so EVERY simd type — including
// simd_float2x2 — is a KNOWN value type and is NOT ObjC-auto-bridged; and simd_float2x2 has
// no managed binding in SimdDatabase.xml (there is no System.Numerics 2×2 matrix), so it
// resolves to a direct `Swift.AnyType` single payload. The generator therefore emits the
// `CaseTag.Affine2x2` slot but skips its factory and TryGet — and critically emits NO
// `simd.simd_float2x2` reference (which would be CS0246). The sibling
// `case transform4x4(simd_float4x4)` (→ System.Numerics.Matrix4x4) and `case untextured(Bool)`
// still emit and round-trip. The compile gate (`nuke binding-tests --compile-only`) fails if
// the apple-frameworks.json fix is reverted.
//
// This was previously documented here as unreproducible because every Apple framework type
// the bindings could reference was auto-bridged. That changed once the `simd` module was
// classified `valueTypesOnly`: a known value type with no XML binding (simd_float2x2) is
// exactly the direct-AnyType shape, with no need for an unregistered or inaccessible Swift
// type. (Reverting `valueTypesOnly` on simd re-bridges simd_float2x2 to a non-existent
// `simd.simd_float2x2` class and re-breaks the compile gate.)
//
// The unit tests `Emit_EnumCaseWithDirectAnyTypePayload_SkipsTryGetMethod` and
// `Emit_EnumCaseWithTupleContainingAnyType_SkipsTryGetMethod` remain the direct gate-level
// assertions (they register a stub module with an unregistered type to force AnyType and
// assert the absence of the dangerous emitted code); the SimdMatrixPayload fixture is the
// end-to-end runtime-and-compile reproduction. See `Types/SimdProjectionTests.cs`.
