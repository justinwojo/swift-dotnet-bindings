// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Enumerates the distinct strategies for marshalling Swift.Optional&lt;T&gt; across the
/// Swift/C# interop boundary.  Every emitter that deals with Optional types should call
/// <see cref="OptionalMarshalClassifier.Classify"/> once and switch on the result,
/// rather than reimplementing the decision tree inline.
/// </summary>
public enum OptionalMarshalStrategy
{
    /// <summary>
    /// Not an Optional type — the classifier returns this for non-Optional input.
    /// Callers should treat the value as a normal (non-optional) type.
    /// </summary>
    NotOptional,

    /// <summary>
    /// Optional&lt;Class&gt;, Optional&lt;ObjC-bridged&gt;, Optional&lt;ObjC-rooted&gt;.
    /// The inner type is a reference (pointer-sized).  nil encodes as IntPtr.Zero.
    /// Swift side: UnsafeMutableRawPointer? / nullable pointer ABI.
    /// C# side: IntPtr (zero = None, non-zero = Some).
    /// </summary>
    NullablePointer,

    /// <summary>
    /// Optional&lt;ComplexEnum&gt; or Optional&lt;NonFrozenStruct&gt;.
    /// The inner type uses an opaque SafeHandle payload where VWT InitializeWithCopy crashes Mono.
    /// Decomposed into (rawPayload, hasValue) as separate parameters.
    /// Swift side: resultPtr + hasValuePtr (getter) or UnsafeRawPointer + Int8 (setter).
    /// C# side: IntPtr payload + bool hasValue.
    /// </summary>
    DecomposedBuffers,

    /// <summary>
    /// Optional&lt;T&gt; where T &gt;= 8 bytes (Int, String, URL, non-frozen struct, etc.).
    /// Too large for IntPtr transport.  Routed through UnsafeRawPointer out-buffer.
    /// This is the general "large optional" path used by MethodWrapperEmitter / OptionalPointerWrapperEmitter.
    /// Swift side: UnsafeRawPointer parameter or result buffer.
    /// C# side: SwiftOptional&lt;T&gt; with DangerousGetHandle / out-buffer.
    /// </summary>
    LargeOptionalPointer,

    /// <summary>
    /// Optional&lt;BlittablePrimitive&gt; where the inner type has a known compile-time size &lt; 8 bytes
    /// (Bool, Int8, UInt8, Int16, UInt16, Int32, UInt32, Float — but NOT Bool for extra-inhabitant reasons
    ///  on the runtime side).
    /// The tag byte is at a fixed offset (sizeof(T)) in the Optional buffer.
    /// Avoids VWT GetEnumTag which returns incorrect values on some runtimes.
    /// Swift side: read/write tag byte at known offset.
    /// C# side: direct byte read at offset.
    /// </summary>
    BlittableFastPath,

    /// <summary>
    /// General case: Optional&lt;T&gt; where no specialized path applies.
    /// Uses full SwiftOptional&lt;T&gt; with VWT GetEnumTag / DestructiveInjectEnumTag.
    /// Swift side: initializeMemory(as: Optional&lt;T&gt;.self).
    /// C# side: SwiftOptional&lt;T&gt;.ToNullable() / NewSome / NewNone.
    /// </summary>
    FullSwiftOptional,
}

/// <summary>
/// Single source of truth for classifying how a given Optional&lt;T&gt; type should be marshalled.
/// Consolidates logic previously spread across PropertyWrapperEmitter, MethodWrapperEmitter,
/// WrapperValidation, BoundGenericsHandler, and OptionalProjection.
/// </summary>
public static class OptionalMarshalClassifier
{
    /// <summary>
    /// Classifies the Optional marshalling strategy for a given type spec.
    /// Returns <see cref="OptionalMarshalStrategy.NotOptional"/> if the type is not Optional.
    ///
    /// Priority order (first match wins):
    /// 1. NullablePointer — reference inner (class, ObjC-bridged, ObjC-rooted)
    /// 2. DecomposedBuffers — complex enum or non-frozen struct inner (opaque SafeHandle payload)
    /// 3. BlittableFastPath — blittable primitive inner with known compile-time size
    /// 4. LargeOptionalPointer — inner type &gt;= 8 bytes (not reference, not decomposed, not blittable fast path)
    /// 5. FullSwiftOptional — everything else
    /// </summary>
    public static OptionalMarshalStrategy Classify(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!WrapperValidation.IsOptionalType(typeSpec))
            return OptionalMarshalStrategy.NotOptional;

        // 1. NullablePointer: reference inner types use nullable pointer ABI.
        if (WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase))
            return OptionalMarshalStrategy.NullablePointer;

        // 1b. Optional<protocol existential>: decomposed (resultPtr + hasValuePtr).
        //     ExistentialContainer is too large for register return and doesn't have a TypeRecord,
        //     so VWT-based GetEnumTag/DestructiveInjectEnumTag won't work. Decompose instead.
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, typeDatabase))
            return OptionalMarshalStrategy.DecomposedBuffers;

        // 2. DecomposedBuffers: complex enum or non-frozen struct with opaque SafeHandle payload.
        //    Must be checked before LargeOptionalPointer because decomposed types are also "large"
        //    but need the decomposed (payload, hasValue) pattern instead of the pointer-buffer path.
        if (WrapperValidation.IsDecomposedOptionalType(typeSpec, typeDatabase))
            return OptionalMarshalStrategy.DecomposedBuffers;

        // 3. BlittableFastPath: known-size primitive inner type (tag byte at compile-time offset).
        var innerSpec = GetInnerSpec(typeSpec);
        if (innerSpec is NamedTypeSpec innerNamed &&
            CdeclParamMapper.IsBlittablePrimitiveSwiftType(innerNamed.Name))
        {
            // Verify the blittable size is < 8 bytes (for the fast path).
            // Types with size >= 8 go to LargeOptionalPointer.
            var tagOffset = GetSwiftTagByteOffset(innerNamed.Name);
            if (tagOffset != null && tagOffset.Value < 8)
                return OptionalMarshalStrategy.BlittableFastPath;
        }

        // 4. LargeOptionalPointer: inner type >= 8 bytes (Int, String, etc.).
        //    Uses the same criteria as BoundGenericsHandler.IsLargeOptionalParam:
        //    - Not reference (already handled above)
        //    - Not in the small-optional set
        //    We delegate to the existing small-type check for consistency.
        if (IsLargeOptionalInner(innerSpec, typeSpec, typeDatabase))
            return OptionalMarshalStrategy.LargeOptionalPointer;

        // 5. FullSwiftOptional: general case for remaining types.
        return OptionalMarshalStrategy.FullSwiftOptional;
    }

    /// <summary>
    /// Returns true when an Optional type should use the decomposed (payload, hasValue) pattern
    /// in property wrapper contexts. This is the same as checking for
    /// <see cref="OptionalMarshalStrategy.DecomposedBuffers"/>.
    /// </summary>
    public static bool IsDecomposed(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => Classify(typeSpec, typeDatabase) == OptionalMarshalStrategy.DecomposedBuffers;

    /// <summary>
    /// Returns true when an Optional type is "large" (inner >= 8 bytes) and needs
    /// pointer-buffer transport.  Includes both DecomposedBuffers and LargeOptionalPointer
    /// strategies, matching the scope of BoundGenericsHandler.IsLargeOptionalParam.
    /// </summary>
    public static bool IsLargeOptional(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        var strategy = Classify(typeSpec, typeDatabase);
        return strategy is OptionalMarshalStrategy.DecomposedBuffers
            or OptionalMarshalStrategy.LargeOptionalPointer;
    }

    /// <summary>
    /// Extracts the inner type spec from an Optional type spec.
    /// Returns null if not a well-formed Optional.
    /// </summary>
    internal static TypeSpec? GetInnerSpec(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named &&
            named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1)
            return named.GenericParameters[0];
        return null;
    }

    /// <summary>
    /// Returns the Swift-side tag byte offset (as a string literal for emitted code) for a
    /// blittable primitive Swift type, or null if not applicable.
    ///
    /// This is the single source of truth for the emitter-side tag byte offset computation.
    /// The mapping corresponds to the runtime's GetBlittablePrimitiveTagOffset() in SwiftOptional.cs,
    /// but operates on Swift type names (compile-time) rather than CLR types (runtime).
    ///
    /// Both mappings MUST stay in sync. See OptionalTagOffsetConsistencyTests for enforcement.
    /// </summary>
    public static int? GetSwiftTagByteOffset(string swiftTypeName) => swiftTypeName switch
    {
        "Swift.Bool" or "Bool" or "Swift.Int8" or "Int8" or "Swift.UInt8" or "UInt8" => 1,
        "Swift.Int16" or "Int16" or "Swift.UInt16" or "UInt16" => 2,
        "Swift.Int32" or "Int32" or "Swift.UInt32" or "UInt32" or "Swift.Float" or "Float" => 4,
        "Swift.Int" or "Int" or "Swift.UInt" or "UInt" or
        "Swift.Int64" or "Int64" or "Swift.UInt64" or "UInt64" or
        "Swift.Double" or "Double" or
        "CoreFoundation.CGFloat" or "CGFloat" => 8,
        _ => null
    };

    /// <summary>
    /// Returns the tag byte offset as a string literal for use in emitted Swift/C# code.
    /// Delegates to <see cref="GetSwiftTagByteOffset"/>.
    /// </summary>
    public static string? GetSwiftTagByteOffsetString(string swiftTypeName)
        => GetSwiftTagByteOffset(swiftTypeName)?.ToString();

    /// <summary>
    /// For an <c>Optional&lt;BlittablePrimitive&gt;</c> param marshalled across @_cdecl
    /// as an <c>UnsafeRawPointer</c>, returns the Swift decode shape — local-binding
    /// type plus RHS expression — so callers can compose:
    /// <code>let {localName}: {LocalType} = {Rhs}</code>
    /// inside their wrapper body. The RHS references the supplied
    /// <paramref name="paramName"/> as the source pointer. Returns <c>null</c> when
    /// the Optional inner isn't a blittable primitive (Bool, frozen value-type
    /// struct, reference type, opaque struct/enum, …) — caller falls through to its
    /// own fallback shape (typically <c>assumingMemoryBound(to: Swift.Optional&lt;T&gt;.self).pointee</c>).
    ///
    /// Shared by <see cref="CdeclParamMapper"/>.<c>Map</c> (regular method path) and
    /// <c>ProtocolExtensionEmitter</c> (synthesised protocol-extension wrapper path)
    /// so the blittable-primitive set × tag-byte offset table × decode RHS shape can
    /// only drift in one place.
    /// </summary>
    public static (string LocalType, string Rhs)? TryGetBlittablePrimitiveOptionalDecode(
        TypeSpec optionalSpec, string paramName)
    {
        if (optionalSpec is not NamedTypeSpec optSpec
            || optSpec.Name != "Swift.Optional"
            || optSpec.GenericParameters.Count != 1)
            return null;
        if (optSpec.GenericParameters[0] is not NamedTypeSpec innerNamed
            || !CdeclParamMapper.IsBlittablePrimitiveSwiftType(innerNamed.Name))
            return null;

        var rawType = CdeclParamMapper.GetSwiftRawValueType(innerNamed.Name);
        var tagOffset = GetSwiftTagByteOffsetString(innerNamed.Name) ?? "8";
        return ($"{rawType}?",
                $"{paramName}.advanced(by: {tagOffset}).load(as: UInt8.self) == 0 ? {paramName}.load(as: {rawType}.self) : nil");
    }

    /// <summary>
    /// Checks whether the inner type of an Optional makes it "large" (>= 8 bytes),
    /// matching BoundGenericsHandler.IsLargeOptionalParam logic for non-reference,
    /// non-protocol inner types that are not in the small-optional set.
    /// </summary>
    private static bool IsLargeOptionalInner(TypeSpec? innerSpec, TypeSpec optionalSpec, ITypeDatabase typeDatabase)
    {
        if (innerSpec is not NamedTypeSpec innerNamed)
            return false;

        // Protocol existentials use nullable pointer ABI — not large.
        if (typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
            innerRecord.Kind == TypeRecordKind.Protocol)
            return false;
        if (CdeclParamMapper.IsProtocolExistentialType(optionalSpec, typeDatabase))
            return false;

        // Small value types (< 8 bytes) fit in IntPtr — not large.
        if (BoundGenericsHandler.SmallOptionalInnerTypes.Contains(innerNamed.Name))
            return false;

        // Everything else is large.
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Decomposed Optional naming/access pattern helpers (Item 6)
    //
    // These constants and helpers standardize the hasValue parameter/variable
    // naming and the byte read/write patterns used by decomposed Optional
    // getters and setters across Swift and C# emitted code.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Swift-side hasValue pointer parameter name (for getters that write the flag).</summary>
    public const string SwiftHasValuePtrParam = "hasValuePtr";

    /// <summary>Swift-side hasValue direct parameter name (for setters that receive the flag).</summary>
    public const string SwiftHasValueParam = "hasValue";

    /// <summary>Swift type for the hasValue parameter/value across @_cdecl boundaries.</summary>
    public const string SwiftHasValueType = "Int8";

    /// <summary>C#-side hasValue local variable name (for reading the flag from a buffer).</summary>
    public const string CSharpHasValueLocal = "_hasValue";

    /// <summary>
    /// Returns the Swift code to write a hasValue flag (1 = Some, 0 = None) to a pointer.
    /// Used by decomposed Optional getters.
    /// </summary>
    /// <param name="ptrName">The Swift variable name of the UnsafeMutableRawPointer to write to.</param>
    /// <param name="hasValue">True to write Some (1), false to write None (0).</param>
    public static string SwiftWriteHasValue(string ptrName, bool hasValue)
        => $"{ptrName}.storeBytes(of: {SwiftHasValueType}({(hasValue ? "1" : "0")}), as: {SwiftHasValueType}.self)";

    /// <summary>
    /// Returns the Swift conditional expression to reconstruct an Optional from
    /// a decomposed (payload, hasValue) pair.
    /// Used by decomposed Optional setters.
    /// </summary>
    /// <param name="hasValueVar">The Swift variable name holding the Int8 hasValue flag.</param>
    /// <param name="payloadVar">The Swift variable name for the UnsafeRawPointer payload.</param>
    /// <param name="innerSwiftType">The fully-qualified Swift inner type name.</param>
    /// <param name="resultVar">The Swift variable name for the reconstructed Optional.</param>
    public static string SwiftReconstructOptional(string hasValueVar, string payloadVar, string innerSwiftType, string resultVar)
    {
        // Protocol existential metatypes need parentheses: (any Protocol).self, not any Protocol.self
        var metatype = innerSwiftType.StartsWith("any ") ? $"({innerSwiftType}).self" : $"{innerSwiftType}.self";
        // Type annotations also need parenthesization: (any P1 & P2)? not any P1 & P2?
        var typeAnnotation = innerSwiftType.StartsWith("any ") ? $"({innerSwiftType})?" : $"{innerSwiftType}?";
        return $"let {resultVar}: {typeAnnotation} = {hasValueVar} != 0 ? {payloadVar}.assumingMemoryBound(to: {metatype}).pointee : nil";
    }

    /// <summary>
    /// Returns the C# code to read a hasValue byte from a pointer buffer.
    /// Used by decomposed Optional return marshalling.
    /// </summary>
    /// <param name="ptrName">The C# variable name of the IntPtr/byte* buffer.</param>
    public static string CSharpReadHasValue(string ptrName)
        => $"byte {CSharpHasValueLocal} = ((byte*){ptrName})[0];";

    /// <summary>
    /// Returns the C# null-check expression for the hasValue local.
    /// Uses `return default;` (not `return null;`) so unconstrained generic `TValue?`
    /// returns compile — `null` is invalid for unconstrained T (CS0403). `default`
    /// is equivalent to `null` for concrete nullable types (string?, int?).
    /// </summary>
    public static string CSharpHasValueNullCheck()
        => $"if ({CSharpHasValueLocal} == 0) return default;";
}
