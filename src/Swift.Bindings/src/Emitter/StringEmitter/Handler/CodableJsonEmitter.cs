// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Codable JSON round-trip emitter. For each non-generic non-frozen
/// struct (the <c>ClassWithOpaquePayload</c> projection scheme: <c>_payload</c>
/// + <c>NewFromPayloadCore</c> + <c>_payloadSize</c>) that conforms to both
/// Encodable and Decodable (including the Codable typealias), emits:
///
/// <list type="bullet">
///   <item><description>A Swift <c>@_cdecl</c> trampoline <c>SBW_&lt;Module&gt;_&lt;Type&gt;_EncodeJson</c>
///     that reads the receiver, runs <c>JSONEncoder().encode(value)</c>, and writes the resulting
///     bytes into a caller-supplied <c>SBW_Utf8Slice</c> result slot. Returns Int32 status
///     (0 = success, non-zero = encode failure).</description></item>
///   <item><description>A Swift <c>@_cdecl</c> trampoline <c>SBW_&lt;Module&gt;_&lt;Type&gt;_DecodeJson</c>
///     that runs <c>JSONDecoder().decode(Type.self, from: data)</c> and initializes the caller's
///     result buffer with the decoded value on success.</description></item>
///   <item><description>A C# instance method <c>byte[] EncodeToJson()</c> that pins the receiver and
///     reads back the JSON bytes via the Utf8Slice helper.</description></item>
///   <item><description>A C# static factory <c>DecodeFromJson(byte[])</c> that allocates a payload buffer,
///     invokes the trampoline, and wraps the result in a new instance via <c>NewFromPayloadCore</c>.</description></item>
/// </list>
///
/// Bridges JSON specifically because <c>JSONEncoder</c>/<c>JSONDecoder</c> are concrete Foundation
/// types — the synthesized <c>encode(to: any Encoder)</c> / <c>init(from: any Decoder)</c> still
/// remain skipped because <c>Encoder</c>/<c>Decoder</c> are unresolvable existential protocols.
/// Generic closed-instantiation support is tracked separately.
/// </summary>
internal static class CodableJsonEmitter
{
    /// <summary>
    /// Returns true when <paramref name="typeDecl"/> is a non-generic struct projected as a C# class
    /// via the ClassWithOpaquePayload scheme (non-frozen) and conforms to both Encodable and Decodable.
    /// The ClassWithBufferStruct scheme (frozen + ref fields, also class-projected) is intentionally
    /// excluded because it lacks <c>_payloadSize</c> and <c>NewFromPayloadCore</c> — the JSON decode
    /// factory cannot construct an instance without them.
    /// </summary>
    public static bool ShouldEmit(TypeDecl typeDecl, bool isProjectedAsClass)
    {
        if (typeDecl is not StructDecl structDecl) return false;
        if (structDecl.IsGeneric) return false;
        if (!isProjectedAsClass) return false;
        if (structDecl.IsModuleInternal) return false;
        // ClassWithBufferStruct (frozen struct with ref fields, projected as class) only exposes
        // `_payload` + `PayloadBuffer<Buffer>` — not the `_payloadSize`/`NewFromPayloadCore` factory
        // primitives the decoder relies on. Skip until a dedicated decode path is added.
        if (structDecl.IsFrozen) return false;

        return ConformsToCodable(structDecl);
    }

    /// <summary>
    /// Emits Swift <c>@_cdecl</c> trampolines and matching C# methods for JSON round-trip.
    /// Caller is responsible for ensuring the type satisfies <see cref="ShouldEmit"/>.
    /// </summary>
    public static void Emit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        StructDecl structDecl,
        ModuleDecl moduleDecl,
        string typeNameWithGenerics,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        var moduleName = moduleDecl.Name;
        var swiftQualifiedName = structDecl.SwiftTypeName?.ToString() ?? $"{moduleName}.{structDecl.Name}";
        // Include the full nested path in the @_cdecl symbol so two nested structs that share a leaf
        // name (e.g. MPIOptions.Marker and MPIOptions.FloatingLabelAppearance.Marker) don't both emit
        // SBW_<Module>_Marker_EncodeJson and trigger swiftc's "multiple definitions of symbol" error.
        var qualifiedSymbolPart = SanitizeSymbol(swiftQualifiedName);
        var encodeSymbol = $"SBW_{qualifiedSymbolPart}_EncodeJson";
        var decodeSymbol = $"SBW_{qualifiedSymbolPart}_DecodeJson";
        var wrapperLib = typeDatabase.AsyncLibraryName;
        if (string.IsNullOrEmpty(wrapperLib))
        {
            // No wrapper library configured (xcframework-less mode); skip JSON emission rather
            // than emit P/Invokes that point nowhere.
            logger.LogInformation(
                "Skipping CodableJson emission for '{Type}' — no wrapper library configured.",
                structDecl.Name);
            return;
        }

        // Inherit availability from the type and its parent chain so the @_cdecl
        // wrapper bodies don't reference newer-OS types (e.g. MusicKit.Curator at
        // iOS 15.4+) inside a wrapper compiled against the binding's deployment
        // floor. Without this prefix, swiftc rejects the wrapper with
        // "'X' is only available in iOS N or newer".
        var availability = AvailabilityHelpers.MergeAvailabilityFromAncestors(
            structDecl.AvailabilityAnnotations, structDecl);

        // Register both @_cdecl symbols with the wrapper-symbol contract so a
        // future Cdecl P/Invoke caller for these entry points doesn't trip the
        // contract check.
        // the `_EncodeJson` / `_DecodeJson` suffixes are exclusive
        // to these synthetic Codable trampolines (one pair per Codable type per module).
        // No regular method or property wrapper produces a symbol with these suffixes;
        // per-kind method bucket is collision-safe.
        emissionContext?.TryAddMethodWrapperSymbol(encodeSymbol, DeclIdFactory.ForType(structDecl));
        emissionContext?.TryAddMethodWrapperSymbol(decodeSymbol, DeclIdFactory.ForType(structDecl));

        EmitSwiftTrampolines(swiftWriter, swiftQualifiedName, encodeSymbol, decodeSymbol, availability);
        EmitCSharpMembers(csWriter, structDecl, typeNameWithGenerics, encodeSymbol, decodeSymbol, wrapperLib);
    }

    internal static bool ConformsToCodable(StructDecl structDecl)
    {
        bool encodable = false, decodable = false;
        foreach (var c in structDecl.Conformances)
        {
            var name = c.Protocol.Name;
            if (name == "Encodable" || name == "Codable") encodable = true;
            if (name == "Decodable" || name == "Codable") decodable = true;
        }
        return encodable && decodable;
    }

    private static void EmitSwiftTrampolines(
        SwiftWriter swiftWriter,
        string swiftQualifiedName,
        string encodeSymbol,
        string decodeSymbol,
        IReadOnlyList<AvailabilityAnnotation>? availability)
    {
        var availabilityPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(availability, "");

        // Encode: read receiver, JSONEncoder().encode(value), write SBW_Utf8Slice into resultPtr.
        // Returns 0 on success, 1 on encoder failure.
        swiftWriter.WriteLines($$"""

            // Codable JSON encode @_cdecl wrapper for {{swiftQualifiedName}}.
            {{availabilityPrefix}}@_cdecl("{{encodeSymbol}}")
            public func _sbw_encodeJson_{{SanitizeSymbol(swiftQualifiedName)}}(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) -> Int32 {
                let value = self_.assumingMemoryBound(to: {{swiftQualifiedName}}.self).pointee
                do {
                    let data = try JSONEncoder().encode(value)
                    let bytes = [UInt8](data)
                    if bytes.isEmpty {
                        resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                        return 0
                    }
                    let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: bytes.count)
                    ptr.initialize(from: bytes, count: bytes.count)
                    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: bytes.count), as: SBW_Utf8Slice.self)
                    return 0
                } catch {
                    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                    return 1
                }
            }

            // Codable JSON decode @_cdecl wrapper for {{swiftQualifiedName}}.
            // Returns 0 on success, 1 on decoder failure (resultPtr is left untouched on failure).
            {{availabilityPrefix}}@_cdecl("{{decodeSymbol}}")
            public func _sbw_decodeJson_{{SanitizeSymbol(swiftQualifiedName)}}(_ resultPtr: UnsafeMutableRawPointer, _ bytesPtr: UnsafePointer<UInt8>, _ byteCount: Int) -> Int32 {
                let buffer = UnsafeBufferPointer(start: bytesPtr, count: byteCount)
                let data = Data(buffer: buffer)
                do {
                    let result = try JSONDecoder().decode({{swiftQualifiedName}}.self, from: data)
                    resultPtr.initializeMemory(as: {{swiftQualifiedName}}.self, repeating: result, count: 1)
                    return 0
                } catch {
                    return 1
                }
            }
            """);
        swiftWriter.WriteLine();
    }

    private static void EmitCSharpMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        string typeNameWithGenerics,
        string encodeSymbol,
        string decodeSymbol,
        string wrapperLib)
    {
        var encodePInvokeName = $"PInvoke_EncodeJson_{structDecl.Name}";
        var decodePInvokeName = $"PInvoke_DecodeJson_{structDecl.Name}";

        csWriter.WriteLines($$"""

            /// <summary>
            /// Encodes this <see cref="{{typeNameWithGenerics}}"/> as JSON via Foundation's
            /// <c>JSONEncoder</c>. Equivalent to <c>JSONEncoder().encode(value)</c> in Swift.
            /// </summary>
            /// <returns>The JSON-encoded bytes (UTF-8).</returns>
            /// <exception cref="global::System.InvalidOperationException">Thrown when the Swift encoder rejects the value.</exception>
            public byte[] EncodeToJson()
            {
                unsafe
                {
                    var success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        global::Swift.Runtime.Utf8Slice slice = default;
                        int rc = {{encodePInvokeName}}((global::System.IntPtr)(&slice), _payload.DangerousGetHandle());
                        if (rc != 0)
                            throw new global::System.InvalidOperationException("Failed to encode {{structDecl.Name}} as JSON.");
                        return global::Swift.Runtime.InteropServices.SwiftMarshal.ReadUtf8SliceBytes((global::System.IntPtr)(&slice));
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }

            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            [global::System.Runtime.InteropServices.LibraryImport("{{wrapperLib}}", EntryPoint = "{{encodeSymbol}}")]
            private static partial int {{encodePInvokeName}}(global::System.IntPtr resultPtr, global::System.IntPtr self_);

            /// <summary>
            /// Decodes a <see cref="{{typeNameWithGenerics}}"/> from JSON via Foundation's
            /// <c>JSONDecoder</c>. Equivalent to <c>JSONDecoder().decode({{structDecl.Name}}.self, from: data)</c>
            /// in Swift.
            /// </summary>
            /// <param name="json">UTF-8 JSON bytes to decode.</param>
            /// <returns>The decoded instance.</returns>
            /// <exception cref="global::System.ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
            /// <exception cref="global::System.InvalidOperationException">Thrown when the Swift decoder rejects the input.</exception>
            public static {{typeNameWithGenerics}} DecodeFromJson(byte[] json)
            {
                if (json is null) throw new global::System.ArgumentNullException(nameof(json));
                unsafe
                {
                    var resultPtr = (global::System.IntPtr)global::System.Runtime.InteropServices.NativeMemory.Alloc(_payloadSize);
                    fixed (byte* bytesPtr = json)
                    {
                        int rc = {{decodePInvokeName}}(resultPtr, (global::System.IntPtr)bytesPtr, (global::System.IntPtr)json.Length);
                        if (rc != 0)
                        {
                            global::System.Runtime.InteropServices.NativeMemory.Free((void*)resultPtr);
                            throw new global::System.InvalidOperationException("Failed to decode {{structDecl.Name}} from JSON.");
                        }
                    }
                    return ({{typeNameWithGenerics}})NewFromPayloadCore(resultPtr);
                }
            }

            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            [global::System.Runtime.InteropServices.LibraryImport("{{wrapperLib}}", EntryPoint = "{{decodeSymbol}}")]
            private static partial int {{decodePInvokeName}}(global::System.IntPtr resultPtr, global::System.IntPtr bytesPtr, global::System.IntPtr byteCount);
            """);
    }

    private static string SanitizeSymbol(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
