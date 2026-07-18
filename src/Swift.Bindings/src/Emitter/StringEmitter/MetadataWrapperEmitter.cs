// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-type @_cdecl Swift wrappers that return type metadata as raw pointers,
/// eliminating CallConvSwift from metadata accessor P/Invokes.
/// </summary>
public static class MetadataWrapperEmitter
{
    /// <summary>
    /// Gets the @_cdecl symbol name for a metadata wrapper.
    /// Uses the module-qualified type name to avoid collisions for nested types.
    /// </summary>
    public static string GetMetadataSymbolName(string moduleName, string moduleQualifiedTypeName)
    {
        var safeTypeName = moduleQualifiedTypeName.Replace(".", "_");
        var hash = EmitterUtility.DeterministicHash8($"{moduleName}.{moduleQualifiedTypeName}");
        return $"SBW_GetMetadata_{moduleName}_{safeTypeName}_{hash}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper that returns type metadata as a raw pointer.
    /// Uses ModuleEmissionContext for dedup (each type emitted once).
    /// </summary>
    /// <param name="typeDecl">The type declaration whose metadata is being accessed.
    /// Used to compute the merged availability annotations from the type and its
    /// ancestors so the emitted Swift wrapper compiles when the type (or an enclosing
    /// type) is gated behind an OS version (e.g., iOS 16.4+).</param>
    public static void EmitIfNeeded(
        SwiftWriter swiftWriter, string moduleName,
        string moduleQualifiedSwiftName, string symbolName,
        ModuleEmissionContext ctx,
        BaseDecl? typeDecl = null)
    {
        // one metadata accessor per type. The `SBW_GetMetadata_`-style
        // prefix is structurally distinct from method/property/constructor symbols, so the
        // per-kind metadata bucket is collision-safe by construction.
        if (!ctx.TryAddMetadataWrapperSymbol(symbolName, typeDecl is TypeDecl td ? DeclIdFactory.ForType(td) : null))
            return;

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Metadata accessor @_cdecl wrapper for {moduleQualifiedSwiftName}.");
        var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(null, typeDecl);
        var availabilityGuard = WrapperEmitterHelpers.BuildAvailabilityGuardExpression(availability);
        var funcName = $"_sbw_getMetadata_{EmitterUtility.DeterministicHash8(symbolName)}";
        var typeReference = $"unsafeBitCast({moduleQualifiedSwiftName}.self as Any.Type, to: UnsafeMutableRawPointer.self)";

        if (string.IsNullOrEmpty(availabilityGuard))
        {
            // Type is available at the module's deployment floor — reference it unconditionally.
            swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
            swiftWriter.WriteLine($"public func {funcName}() -> UnsafeMutableRawPointer {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine(typeReference);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            // Availability-gated type: the underlying Swift metadata accessor is weak-imported
            // and resolves to null on OS versions below the type's floor. Referencing the type
            // unconditionally branches through that null accessor (a native SIGSEGV at pc=0 that
            // no managed try/catch can intercept). Guard the reference behind a runtime
            // #available check and return nil when unavailable; the managed caller maps the null
            // metadata to a PlatformNotSupportedException. Deliberately NO declaration-level
            // @available here — it would lift the function's availability context to the floor,
            // making the inner #available always-true and dead-coding the else branch.
            swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
            swiftWriter.WriteLine($"public func {funcName}() -> UnsafeMutableRawPointer? {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"if {availabilityGuard} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"return {typeReference}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("} else {");
            swiftWriter.Indent++;
            swiftWriter.WriteLine("return nil");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }

    /// <summary>
    /// Builds the C# <c>static TypeMetadata ISwiftObject.GetTypeMetadata()</c> method body for the
    /// xcframework wrapper path: call the Cdecl <c>@_cdecl</c> wrapper (<c>PInvoke_getMetadata</c>),
    /// falling back to the dylib's CallConvSwift accessor (<c>PInvoke_getMetadata_fallback</c>) when
    /// the wrapper DLL isn't present.
    ///
    /// <para>For an availability-gated type, <see cref="EmitIfNeeded"/> makes the Swift wrapper
    /// return <c>nil</c> below the type's OS floor, so the P/Invoke yields a zero
    /// <see cref="Swift.Runtime.TypeMetadata"/>. Convert that to a
    /// <see cref="System.PlatformNotSupportedException"/> at the method boundary so every caller —
    /// including a direct <c>ISwiftObject.GetTypeMetadata()</c> call that bypasses
    /// <c>SwiftObjectHelper&lt;T&gt;</c> — gets a catchable managed error rather than a zero metadata
    /// that later faults on a value-witness dereference (Size / ValueWitnessTable). When the type
    /// has no availability floor the body is unchanged from the original direct-return form.</para>
    /// </summary>
    /// <param name="availability">Merged availability annotations for the type (from
    /// <c>WrapperEmitterHelpers.MergeAvailabilityFromAncestors</c>); empty/null means the type is
    /// available at the module deployment floor.</param>
    /// <param name="moduleQualifiedSwiftName">Module-qualified Swift type name, used in the
    /// diagnostic message.</param>
    public static string BuildGetTypeMetadataWithFallback(
        IReadOnlyList<AvailabilityAnnotation>? availability,
        string moduleQualifiedSwiftName)
    {
        var floors = WrapperEmitterHelpers.DescribeAvailabilityFloors(availability);
        if (string.IsNullOrEmpty(floors))
        {
            return """
                static TypeMetadata ISwiftObject.GetTypeMetadata()
                {
                    try
                    {
                        return PInvoke_getMetadata();
                    }
                    catch (global::System.DllNotFoundException)
                    {
                        return PInvoke_getMetadata_fallback();
                    }
                    catch (global::System.EntryPointNotFoundException)
                    {
                        return PInvoke_getMetadata_fallback();
                    }
                }
                """;
        }

        // Names are controlled (Swift identifier path + version strings), so they need no escaping.
        var message = $"Swift type '{moduleQualifiedSwiftName}' is not available on this OS version; it requires {floors} or later.";
        return $$"""
            static TypeMetadata ISwiftObject.GetTypeMetadata()
            {
                TypeMetadata __metadata;
                try
                {
                    __metadata = PInvoke_getMetadata();
                }
                catch (global::System.DllNotFoundException)
                {
                    __metadata = PInvoke_getMetadata_fallback();
                }
                catch (global::System.EntryPointNotFoundException)
                {
                    __metadata = PInvoke_getMetadata_fallback();
                }
                if (!__metadata.IsValid)
                    throw new global::System.PlatformNotSupportedException("{{message}}");
                return __metadata;
            }
            """;
    }
}
