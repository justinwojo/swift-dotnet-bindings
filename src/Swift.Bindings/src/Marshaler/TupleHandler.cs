// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling tuple types in Swift bindings.
/// It provides methods to detect tuple arguments and translate them to appropriate
/// C# ValueTuple types.
/// </summary>
public class TupleHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ExistentialHandler _existentialHandler;

    /// <summary>
    /// Maximum number of tuple elements supported.
    /// C# ValueTuple supports up to 7 elements natively; beyond that requires nesting.
    /// </summary>
    public const int MaxSupportedTupleElements = 7;

    public TupleHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    /// <summary>
    /// Determines whether the specified argument declaration represents a tuple type.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is a non-empty tuple; otherwise, <c>false</c>.</returns>
    public bool IsTuple(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple;

    /// <summary>
    /// Determines whether the specified type spec represents a tuple type.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is a non-empty tuple; otherwise, <c>false</c>.</returns>
    public bool IsTuple(TypeSpec typeSpec) =>
        typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple;

    /// <summary>
    /// Gets the TupleTypeSpec from an argument declaration.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The TupleTypeSpec if the argument is a non-empty tuple; otherwise, null.</returns>
    public TupleTypeSpec? GetTupleTypeSpec(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple ? tuple : null;

    /// <summary>
    /// Determines whether the tuple is a supported type.
    /// Supported tuples have:
    /// - Maximum 7 tuple elements
    /// - Only frozen/primitive element types
    /// - No nested tuples
    /// - No closures as tuple elements
    /// - No generic type parameters as elements
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns><c>true</c> if the tuple is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedTuple(TupleTypeSpec tupleTypeSpec)
    {
        // Empty tuples are void, not tuples
        if (tupleTypeSpec.IsEmptyTuple)
            return false;

        // Maximum 7 elements (C# ValueTuple limit)
        if (tupleTypeSpec.Elements.Count > MaxSupportedTupleElements)
            return false;

        // Check that all element types are supported
        foreach (var element in tupleTypeSpec.Elements)
        {
            if (!IsSupportedTupleElementType(element))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether any element of the tuple contains a generic type parameter,
    /// either directly (e.g., τ_0_0) or nested inside a bound generic (e.g., Optional&lt;τ_0_0&gt;).
    /// Tuples with generic elements use indirect result (SwiftIndirectResult) in P/Invoke
    /// and per-element extraction via SwiftMarshal.MarshalFromSwift at runtime.
    /// </summary>
    public bool HasGenericTypeParameterElements(TupleTypeSpec tupleTypeSpec) =>
        tupleTypeSpec.Elements.Any(ContainsGenericTypeParameter);

    /// <summary>
    /// Checks whether every top-level element of the tuple is a bare generic type parameter
    /// (e.g., T, U, τ_0_0). Swift returns bare generic parameters via @out indirect result,
    /// so a tuple where every element is a bare generic uniformly uses N @out registers.
    ///
    /// Bound generics like Array&lt;T&gt;, UnsafePointer&lt;T&gt;, and similar are returned direct
    /// (refcounted reference, pointer) even though they contain a generic parameter; mixing
    /// them with bare T produces a mixed indirect/direct ABI (per-element @out pointers in
    /// leading argument registers plus direct results in return registers) that this branch
    /// does not model. Optional&lt;T&gt; is also address-only when T is generic but is excluded
    /// here for safety. Members with such mixed shapes are skipped fail-closed at emission
    /// (MarshallingHelpers.IsUnmodeledMixedGenericTupleReturn) — the legacy single-buffer
    /// SwiftIndirectResult fallback must never be used for them, its register assignment is
    /// wrong for every mixed shape.
    /// </summary>
    public bool AllElementsAreBareGenericTypeParameter(TupleTypeSpec tupleTypeSpec) =>
        tupleTypeSpec.Elements.Count > 0
        && tupleTypeSpec.Elements.All(static e => e is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name));

    /// <summary>
    /// Recursively checks whether a TypeSpec contains a generic type parameter,
    /// either directly or nested inside bound generic arguments.
    /// </summary>
    private static bool ContainsGenericTypeParameter(TypeSpec typeSpec)
    {
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec))
            return true;

        if (typeSpec is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            return namedType.GenericParameters.Any(ContainsGenericTypeParameter);

        if (typeSpec is TupleTypeSpec tupleType)
            return tupleType.Elements.Any(ContainsGenericTypeParameter);

        return false;
    }

    /// <summary>
    /// Determines whether the tuple is supported when a generic context is available.
    /// Generic type parameter elements are allowed when a context can resolve them.
    /// </summary>
    public bool IsSupportedTuple(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        if (tupleTypeSpec.IsEmptyTuple)
            return false;
        if (tupleTypeSpec.Elements.Count > MaxSupportedTupleElements)
            return false;
        foreach (var element in tupleTypeSpec.Elements)
        {
            if (!IsSupportedTupleElementType(element, genericContext))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string, resolving generic type parameters
    /// via the provided generic context.
    /// </summary>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        return GetCSharpTupleType(tupleTypeSpec, typeSpec =>
        {
            if (typeSpec is NamedTypeSpec namedType &&
                TypeSpecHelpers.IsGenericTypeParameter(namedType.Name) &&
                genericContext.TryResolve(namedType.Name, out var csName))
            {
                return csName;
            }
            return TranslateElementTypeToCSharp(typeSpec);
        });
    }

    /// <summary>
    /// Gets the P/Invoke tuple type, resolving generic type parameters to IntPtr.
    /// </summary>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        return GetPInvokeTupleType(tupleTypeSpec, typeSpec =>
        {
            if (typeSpec is NamedTypeSpec namedType &&
                TypeSpecHelpers.IsGenericTypeParameter(namedType.Name) &&
                genericContext.TryResolve(namedType.Name, out _))
            {
                return "IntPtr";
            }
            return TranslateElementTypeToPInvoke(typeSpec);
        });
    }

    /// <summary>
    /// Checks if a type is supported as a tuple element.
    /// </summary>
    private bool IsSupportedTupleElementType(TypeSpec typeSpec) =>
        IsSupportedTupleElementType(typeSpec, GenericContext.Empty);

    /// <summary>
    /// Checks if a type is supported as a tuple element, with optional generic context.
    /// </summary>
    private bool IsSupportedTupleElementType(TypeSpec typeSpec, GenericContext genericContext)
    {
        // Generic type parameters are valid tuple elements when a mapping is available
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec) && !genericContext.IsEmpty)
            return true;

        // Nested tuples are not supported yet
        if (typeSpec is TupleTypeSpec)
            return false;

        // Closures within tuples are not supported yet
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Existential types are supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        // Named types should be resolvable in the type database
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle bound generic types (e.g., Optional<T>, Array<T>)
            if (namedType.ContainsGenericParameters)
            {
                return IsSupportedGenericTupleElement(namedType);
            }

            // Try to get the type record
            if (!_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
                return false;

            // Frozen types and ObjC-bridged types are supported
            // Non-frozen, non-ObjC types are also allowed since they can be wrapped
            return true;
        }

        // Other type specs (ProtocolList, etc.) not supported
        return false;
    }

    /// <summary>
    /// Checks if a generic type is supported as a tuple element.
    /// Supports bound generic types (Optional, Array, etc.) where the base type is in the database.
    /// </summary>
    private bool IsSupportedGenericTupleElement(NamedTypeSpec namedType)
    {
        // Check if base type is in type database
        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
            return false;

        // Recursively check all generic parameters are supported
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                    return false;
                continue;
            }

            // Recursively check element type
            if (!IsSupportedTupleElementType(genericParam))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string for wrapper methods.
    /// Uses C# tuple syntax with named elements if labels are present.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns>The C# tuple type string (e.g., "(int, string)" or "(int x, string y)").</returns>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec)
    {
        return GetCSharpTupleType(tupleTypeSpec, TranslateElementTypeToCSharp);
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string for wrapper methods,
    /// using a custom type translator for element types.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <param name="typeTranslator">A function that translates element TypeSpecs to C# type names.</param>
    /// <returns>The C# tuple type string (e.g., "(int, string)" or "(int x, string y)").</returns>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec, Func<TypeSpec, string> typeTranslator)
    {
        var elementTypes = new List<string>();
        foreach (var element in tupleTypeSpec.Elements)
        {
            var typeString = typeTranslator(element);

            // Include label if present
            if (!string.IsNullOrEmpty(element.TypeLabel))
            {
                typeString = $"{typeString} {element.TypeLabel}";
            }

            elementTypes.Add(typeString);
        }

        return $"({string.Join(", ", elementTypes)})";
    }

    /// <summary>
    /// Gets the P/Invoke tuple type for a tuple.
    /// Uses ValueTuple<> generic type for P/Invoke compatibility.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns>The ValueTuple type string (e.g., "ValueTuple<int, string>").</returns>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec)
    {
        return GetPInvokeTupleType(tupleTypeSpec, TranslateElementTypeToPInvoke);
    }

    /// <summary>
    /// Gets the P/Invoke tuple type for a tuple, using a custom type translator for element types.
    /// Uses ValueTuple<> generic type for P/Invoke compatibility.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <param name="typeTranslator">A function that translates element TypeSpecs to P/Invoke type names.</param>
    /// <returns>The ValueTuple type string (e.g., "ValueTuple<int, IntPtr>").</returns>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec, Func<TypeSpec, string> typeTranslator)
    {
        var elementTypes = new List<string>();
        foreach (var element in tupleTypeSpec.Elements)
        {
            elementTypes.Add(typeTranslator(element));
        }

        return $"ValueTuple<{string.Join(", ", elementTypes)}>";
    }

    /// <summary>
    /// Checks if any element has a P/Invoke-to-C# type mismatch that would break closure callbacks.
    /// Returns true when a tuple element becomes IntPtr in P/Invoke but has a different C# type,
    /// meaning the callback would receive ValueTuple&lt;IntPtr,...&gt; while the delegate expects the C# type.
    /// Pointer types (UnsafeMutablePointer&lt;T&gt; etc.) are IntPtr in BOTH contexts, so they're safe.
    /// </summary>
    public bool HasClosureUnsafeTupleElements(TupleTypeSpec tupleTypeSpec)
    {
        foreach (var element in tupleTypeSpec.Elements)
        {
            var pinvokeType = TranslateElementTypeToPInvoke(element);
            var csharpType = TranslateElementTypeToCSharp(element);
            // IntPtr mismatch: P/Invoke uses IntPtr but C# uses a different type
            if (pinvokeType == "IntPtr" &&
                csharpType != "IntPtr" && csharpType != "System.IntPtr")
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when any tuple element needs per-element marshalling conversion that the converted-tuple
    /// call-argument path does NOT yet implement — i.e. its P/Invoke element type differs from its C#
    /// element type. The WORKING tuple-PARAMETER paths are (a) buffer-marshallable tuples
    /// (<see cref="IsCdeclBufferMarshallableTuple"/>: cdecl-primitive and/or pure-Swift-class elements,
    /// via the @_cdecl stackalloc-buffer path) and (b) tuples whose every element's P/Invoke type
    /// already equals its C# type (frozen-blittable structs, pointer types), passed as a raw
    /// <c>ValueTuple</c> with no conversion. NOTE: this predicate still returns true for a class
    /// element (its IntPtr P/Invoke type differs from the C# wrapper), so the validator pairs it with
    /// <c>!IsCdeclBufferMarshallableTuple</c> to let class tuples through. Every other element makes the standard
    /// <c>ValueTuple</c> path hand Swift a raw tuple of public C# types against a P/Invoke expecting
    /// differing element types — e.g. an existential element's P/Invoke <c>ExistentialContainerN</c> vs
    /// its <c>I{Composition}</c> interface, a simple enum's underlying-int vs the enum type, a
    /// class/non-frozen-struct's <c>IntPtr</c> vs the class, a frozen-mem-mgmt struct's <c>.Buffer</c>
    /// vs the struct — a CS1503 the generator emits FAIL-OPEN today. Until the full per-element
    /// tuple-conversion + call-arg-threading path lands (its own work unit), such a member is skipped
    /// fail-closed. Strict superset of <see cref="HasClosureUnsafeTupleElements"/> (which catches only
    /// the IntPtr subset, for the closure-callback delegate-shape contract); pointer types are
    /// <c>IntPtr</c> in BOTH contexts (modulo the <c>System.</c> prefix) and stay supported.
    /// </summary>
    public bool HasUnmarshalledTupleElements(TupleTypeSpec tupleTypeSpec)
    {
        // "IntPtr" (P/Invoke form) and "System.IntPtr" (C# form) name the same pointer-sized type;
        // pointer-typed elements are safe and must not trip the mismatch (matches the prefix-tolerant
        // comparison in HasClosureUnsafeTupleElements).
        static string Norm(string t) => t == "System.IntPtr" ? "IntPtr" : t;
        foreach (var element in tupleTypeSpec.Elements)
        {
            if (Norm(TranslateElementTypeToPInvoke(element)) != Norm(TranslateElementTypeToCSharp(element)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when EVERY element of this tuple can be marshalled into the @_cdecl tuple's raw ABI
    /// buffer — the <c>stackalloc</c> + per-element <c>Unsafe.Write</c>-at-tuple-metadata-offset path
    /// in <see cref="WrapperEmitter"/>. A non-empty tuple PARAMETER always forces a @_cdecl wrapper
    /// that receives the tuple as <c>UnsafeRawPointer</c>
    /// (<see cref="WrapperValidation.IsParamTypeCdeclRequired"/>) — ValueTuple's StructLayout.Auto is
    /// incompatible with by-value P/Invoke — so the offset-keyed buffer is the only correct transport,
    /// NOT a converted <c>ValueTuple</c> call argument. This predicate is the single source of truth
    /// shared by the validator gate (which lets such a member emit instead of fail-closing), the
    /// PInvoke tuple-type selection, the buffer-emission gate, and the unsafe-body flag.
    ///
    /// Supported element kinds (each has a fixed-size, ABI-faithful slot representation):
    ///  - cdecl primitive scalars (Int*, Float/Double, Bool, CGFloat, …) — written by value;
    ///  - pure Swift reference types (class) — one pointer-width slot, written as the borrowed object
    ///    handle (the source tuple is GC.KeepAlive'd past the call);
    ///  - Swift.String — a 16-byte (2-word) frozen value; the element is already projected as a
    ///    Swift.SwiftString owning its storage, so its borrowed 16-byte value is bit-copied straight
    ///    into the slot and the source tuple is GC.KeepAlive'd past the call (same lifetime model as a
    ///    class slot). (Unlike the @_cdecl String-PARAMETER fast path, which passes utf8 ptr+len —
    ///    inside a tuple the slot IS a Swift.String value.)
    ///  - composition existentials (any P &amp; Q, two or more non-marker protocols → EC2+) — a
    ///    fixed-stride multi-word container, bit-copied into the slot as a +0 BORROWED container
    ///    (GetExistentialContainer never boxes) with the source tuple kept alive; the slot metadata is
    ///    sized by the count-based GetExistentialTypeMetadata(N).
    /// Deliberately UNSUPPORTED (stay fail-closed): simple enums (tag-width vs underlying-type risk),
    /// non-frozen / frozen-with-memory-management structs (Swift stores them INLINE at value size, so a
    /// handle write corrupts the slot), and single-protocol (EC1, may box a value-type conformer at +1)
    /// / bare-Any (EC0) existentials — those require per-element owned-payload teardown, so they remain
    /// caught by <see cref="HasUnmarshalledTupleElements"/> and skipped.
    /// </summary>
    public bool IsCdeclBufferMarshallableTuple(TupleTypeSpec tupleTypeSpec) =>
        !tupleTypeSpec.IsEmptyTuple &&
        // Preserve IsSupportedTuple's arity boundary: TypeMetadata.GetTupleTypeMetadataFromElements
        // throws above 7 elements, so an over-arity tuple must stay fail-closed at generation (the
        // all-primitive case already did via the ValueTuple ceiling) rather than throw at runtime.
        tupleTypeSpec.Elements.Count <= MaxSupportedTupleElements &&
        tupleTypeSpec.Elements.All(IsCdeclBufferMarshallableElement);

    /// <summary>
    /// True when a single tuple element has a fixed-size representation that can be written into the
    /// @_cdecl tuple buffer at its runtime-metadata offset without corrupting adjacent slots. See
    /// <see cref="IsCdeclBufferMarshallableTuple"/> for the supported/unsupported element taxonomy.
    /// </summary>
    public bool IsCdeclBufferMarshallableElement(TypeSpec element)
    {
        // Blittable primitive scalar — its C# representation is byte-for-byte the Swift element slot
        // and is written by value at the metadata offset. (The original all-primitive buffer path.)
        if (CdeclParamMapper.IsCdeclPrimitive(element))
            return true;

        // Swift.String — a 16-byte frozen value. The element is projected as a Swift.SwiftString that
        // owns its storage; its borrowed 16-byte value is bit-copied into the slot and the source tuple
        // is kept alive across the call. See the String branch in the WrapperEmitter buffer-write loop.
        if (MarshallingHelpers.IsSwiftString(element))
            return true;

        // Composition (EC2+) existential: a fixed-stride multi-word container (3 payload + 1 metadata +
        // N witness-table words). The element is bit-copied into the slot as a +0 BORROWED container
        // (GetExistentialContainer never boxes — owns is always false for EC2+), so the source tuple is
        // kept alive and no destroy/free runs. See the existential branch in the buffer-write loop.
        if (IsCompositionExistentialElement(element))
            return true;

        // Pure Swift reference type (class): the element occupies a single pointer-width slot and is
        // written as the object handle (an IntPtr). Excludes value types projected as C# classes
        // (non-frozen / frozen-with-memory structs), which Swift stores INLINE at value size — a handle
        // write there would corrupt the slot — and ObjC bridged/rooted classes, whose handle accessor
        // differs. Those stay fail-closed.
        if (IsBorrowedClassElement(element))
            return true;

        return false;
    }

    /// <summary>
    /// True when every tuple element is a blittable cdecl primitive scalar — the one tuple shape whose
    /// raw, public, and P/Invoke representations are identical. This is the admit key for paths that
    /// P/Invoke a dispatch thunk directly with the tuple by value and have NO per-element conversion
    /// layer (today: the subscript index/return gate in MemberValidationPipeline). Deliberately much
    /// narrower than <see cref="IsCdeclBufferMarshallableTuple"/>: String/class/existential elements are
    /// only sound where the @_cdecl tuple buffer transport carries them per-slot (method/ctor params) —
    /// on a bufferless path their projected public type (string, wrapper class) diverges from the raw
    /// accessor/P-Invoke tuple type and the emitted code does not compile. Carries the same arity
    /// ceiling as the buffer predicate: an over-arity tuple resolves to AnyType downstream and would be
    /// dropped only AFTER the caller reserves its dedup key, suppressing a valid sibling overload — it
    /// must fail closed here instead.
    /// </summary>
    public bool IsAllPrimitiveTuple(TupleTypeSpec tupleTypeSpec) =>
        !tupleTypeSpec.IsEmptyTuple &&
        tupleTypeSpec.Elements.Count <= MaxSupportedTupleElements &&
        tupleTypeSpec.Elements.All(CdeclParamMapper.IsCdeclPrimitive);

    /// <summary>
    /// True for a tuple element that is a SUPPORTED composition existential of two or more non-marker
    /// protocols (e.g. <c>any Nameable &amp; Ageable</c> → <c>ExistentialContainer2</c>). Such an element
    /// is marshalled through the always-borrowed <c>GetExistentialContainer()</c> path (owns == false —
    /// only the single-protocol EC1 boxing branch can own a +1), so its fixed-stride container is
    /// bit-copied into the slot as a +0 alias of the source and kept valid by the source tuple's
    /// keep-alive. Single-protocol (EC1, may box a value-type conformer at +1) and bare-Any (EC0)
    /// existentials are deliberately EXCLUDED — they require per-element owned-payload teardown and stay
    /// fail-closed at the validator.
    /// </summary>
    public bool IsCompositionExistentialElement(TypeSpec element)
    {
        if (!_existentialHandler.IsExistential(element))
            return false;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(element);
        if (protocolList == null
            || !_existentialHandler.IsSupportedExistential(protocolList)
            || ExistentialHandler.GetNonMarkerProtocols(protocolList).Count < 2)
            return false;
        // Parity with ExistentialHandler.GetEffectiveProtocols (per-module ObjC-prefix gate): a mixed
        // ObjC+Swift composition (e.g. any NSItemProviderWriting & SwiftP) projects its PUBLIC element
        // type through GetEffectiveProtocols, which drops the ObjC protocol and collapses to a single
        // interface whose proxy implements ISwiftExistentialConvertible<ExistentialContainer1>; the ABI
        // slot/cast here keep the unfiltered EC{nonMarker} count, so stride and witness-table count
        // disagree → cast/size mismatch. Same guard the EC2+ paths in BoundGenericsHandler and
        // ClosureHandler apply. If ObjC filtering would drop a protocol, stay fail-closed.
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
        return filteredCount == protocolList.Protocols.Count;
    }

    /// <summary>
    /// The borrowed-container conversion expression for a composition existential tuple element —
    /// <c>((Swift.Runtime.ISwiftExistentialConvertible&lt;ExistentialContainerN&gt;){elementVar}).GetExistentialContainer()</c>.
    /// Mirrors the EC2+ branch of <see cref="ExistentialProjection.GetParameterElementConversion"/>; the
    /// returned container is +0 borrowed from the source element.
    /// </summary>
    public string GetCompositionExistentialElementConversion(TypeSpec element, string elementVar)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(element)!;
        var containerType = _existentialHandler.GetCSharpExistentialType(protocolList);
        return $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){elementVar}).GetExistentialContainer()";
    }

    /// <summary>
    /// The non-marker protocol count for a composition existential tuple element — the argument to
    /// <c>TypeMetadata.GetExistentialTypeMetadata(int)</c>, which sizes the slot (the opaque existential
    /// stride is determined by the count alone, independent of the specific protocol identities).
    /// </summary>
    public int GetCompositionExistentialElementProtocolCount(TypeSpec element)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(element)!;
        return ExistentialHandler.GetNonMarkerProtocols(protocolList).Count;
    }

    /// <summary>
    /// True for a tuple element whose buffer slot holds a BORROWED copy that aliases the SOURCE tuple's
    /// ARC root — a pure Swift class written as its +0 object handle, a Swift.String written as the
    /// borrowed 16-byte value read through the source element's payload handle, or a composition
    /// existential written as its +0 borrowed container. Such elements require the source tuple to be
    /// <c>GC.KeepAlive</c>'d past the native call so the backing SafeHandle cannot finalize and release
    /// the value mid-call. Primitives (written by value) do NOT need this. Pairs with the buffer-write
    /// loop's per-element classification.
    /// </summary>
    public bool TupleElementNeedsBorrowKeepAlive(TypeSpec element) =>
        IsBorrowedClassElement(element)
        || MarshallingHelpers.IsSwiftString(element)
        || IsCompositionExistentialElement(element);

    /// <summary>
    /// A pure Swift reference type (class) eligible for the borrowed-object-handle tuple slot — excludes
    /// value types projected as C# classes (non-frozen / frozen-with-memory structs) and ObjC
    /// bridged/rooted classes.
    /// </summary>
    private bool IsBorrowedClassElement(TypeSpec element) =>
        element is NamedTypeSpec named &&
        _typeDatabase.TryGetTypeRecord(named, out var record) &&
        record.Kind == TypeRecordKind.Class &&
        !MarshallingHelpers.IsObjCBridged(record) &&
        !MarshallingHelpers.IsObjCRooted(record);

    /// <summary>
    /// Translates a TypeSpec element to its C# equivalent type.
    /// </summary>
    internal string TranslateElementTypeToCSharp(TypeSpec typeSpec)
    {
        // Handle existential types — use public type (interface/object), not ExistentialContainer
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPublicExistentialType(protocolList);
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle bound generic types (e.g., Optional<T>, Array<T>)
            if (namedType.ContainsGenericParameters)
            {
                return TranslateBoundGenericToCSharp(namedType);
            }

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a bound generic NamedTypeSpec to its full C# type name with generic parameters.
    /// </summary>
    private string TranslateBoundGenericToCSharp(NamedTypeSpec namedType)
    {
        // The bound-generic body is shared with ClosureHandler via BoundGenericTranslation. The tuple
        // path does NOT special-case an empty-tuple argument and omits the bare-generic safety net;
        // nested arguments recurse through this handler's own element translator.
        return BoundGenericTranslation.TranslateBoundGenericToCSharp(
            _typeDatabase,
            _existentialHandler,
            namedType,
            translateGenericArgument: genericParam => TranslateElementTypeToCSharp(genericParam),
            mapEmptyTupleArgumentToSwiftVoid: false,
            bareGenericSafetyNet: false);
    }

    /// <summary>
    /// Records Apple-supplement references for tuple elements whose type records resolve
    /// to supplement-homed C# types (e.g. Swift.Foundation.Data).
    /// </summary>
    /// <remarks>
    /// Method parameter tuples bypass TypeProjectionFactory — the P/Invoke expects ABI
    /// types and there is no per-element conversion in the wrapper body — so the factory's
    /// supplement recording never fires for them. The emission arms that surface a tuple's
    /// element type names into the wrapper signature or P/Invoke declaration must record
    /// explicitly; otherwise the generated csproj lacks the SwiftBindings.Apple
    /// PackageReference and the binding's own C# verify build fails on the missing
    /// namespace. Call only from arms that actually emit the tuple type text — recording
    /// tracks emission, not resolution.
    /// </remarks>
    public void RecordAppleSupplementReferences(TupleTypeSpec tupleTypeSpec, string callerHint)
    {
        foreach (var element in tupleTypeSpec.Elements)
            RecordAppleSupplementReference(element, callerHint);
    }

    private void RecordAppleSupplementReference(TypeSpec typeSpec, string callerHint)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return;

        if (namedType.ContainsGenericParameters)
        {
            // The C# tuple type nests bound-generic arguments (e.g.
            // SwiftOptional<Swift.Foundation.Data>), so recurse to the concrete element
            // types whose names it surfaces.
            foreach (var genericParam in namedType.GenericParameters)
                RecordAppleSupplementReference(genericParam, callerHint);
            return;
        }

        // Swift.Foundation.* managed types are homed exclusively in the SwiftBindings.Apple
        // supplement (Swift.Runtime declares no Swift.Foundation namespace), so the
        // resolved C# namespace is the supplement test.
        if (_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord) &&
            typeRecord.CSharpTypeName.FullyQualifiedName.StartsWith("Swift.Foundation.", StringComparison.Ordinal))
        {
            AppleSupplementReferences.Record(namedType.Name, callerHint);
        }
    }

    /// <summary>
    /// Translates a TypeSpec element to its P/Invoke equivalent type.
    /// </summary>
    private string TranslateElementTypeToPInvoke(TypeSpec typeSpec)
    {
        // Handle existential types
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPInvokeExistentialType(protocolList);
            return "IntPtr";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Bound generic types with optional containing ObjC types → IntPtr
            if (namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0)
                {
                    var innerType = namedType.GenericParameters[0];
                    if (innerType is NamedTypeSpec innerNamed &&
                        _typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                        MarshallingHelpers.IsObjCBridged(innerRecord))
                    {
                        // Optional ObjC type → IntPtr (null represented as IntPtr.Zero)
                        return "IntPtr";
                    }
                }
                // Other bound generics → IntPtr (opaque pointer, safe for C# generic type arguments)
                return "IntPtr";
            }

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);

            // ObjC bridged types use IntPtr in P/Invoke
            if (MarshallingHelpers.IsObjCBridged(typeRecord))
            {
                return "IntPtr";
            }

            // Enums: simple enums use underlying integer type, complex enums use IntPtr
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                return "IntPtr";
            }

            // Swift classes are non-blittable C# classes — must use IntPtr (no .Buffer)
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return "IntPtr";
            }

            // Non-frozen structs (ClassWithOpaquePayload) are non-blittable C# classes — must use IntPtr (no .Buffer)
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return "IntPtr";
            }

            // Frozen types with memory management use Buffer type
            if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
            {
                return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
            }

            // Frozen blittable structs — use type name directly
            if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Fallback — IntPtr is safe for any unknown type (was class name, which breaks LibraryImport)
            return "IntPtr";
        }

        // Fallback for unsupported types
        return "IntPtr";
    }
}
