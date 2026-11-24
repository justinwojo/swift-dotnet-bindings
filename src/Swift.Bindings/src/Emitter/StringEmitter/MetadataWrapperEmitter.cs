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
    /// Declares <paramref name="methodName"/>() over a type's own Swift metadata accessor
    /// (<c>$s…Ma</c>). The accessor reads the metadata request from its first argument register,
    /// so the import takes the request and the zero-argument method passes
    /// <c>TypeMetadataRequest.Complete</c>; without it the accessor sees whatever the caller left in
    /// that register, and a stray non-blocking or abstract-state bit returns incomplete metadata.
    /// Callers keep the zero-argument spelling. The import is private, so it neither hides nor is
    /// hidden by a base class's import, and <paramref name="hasNewModifier"/> applies only to the
    /// zero-argument method, as it did when that method was the import itself.
    /// <paramref name="emissionContext"/>, when supplied, records the import's Swift call facts for
    /// the module's transition ledger; it never changes the rendered text.
    /// <paramref name="refusal"/>, the type's runtime availability guard
    /// (<see cref="AvailabilityAttributeEmitter.BuildRuntimeAvailabilityGuardStatement"/>), opens the
    /// zero-argument method when the type has an OS floor: below it the accessor may be a symbol the
    /// OS does not ship, and the method is internal, so it refuses itself rather than relying on the
    /// callers in this file.
    /// </summary>
    public static IEnumerable<string> FormatSwiftAccessorImportLines(
        string libraryPath, string accessorSymbol, string methodName, bool hasNewModifier,
        ModuleEmissionContext? emissionContext = null, string? refusal = null)
    {
        var newModifier = hasNewModifier ? "new " : "";
        if (refusal is null)
            yield return $"internal static {newModifier}TypeMetadata {methodName}() => {methodName}(global::Swift.Runtime.TypeMetadataRequest.Complete);";
        else
        {
            yield return $"internal static {newModifier}TypeMetadata {methodName}()";
            yield return "{";
            yield return $"    {refusal}";
            yield return $"    return {methodName}(global::Swift.Runtime.TypeMetadataRequest.Complete);";
            yield return "}";
        }
        yield return "";
        foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
        {
            LibraryPath = libraryPath,
            EntryPoint = accessorSymbol,
            MethodName = methodName,
            ReturnType = "TypeMetadata",
            ParametersString = "global::Swift.Runtime.TypeMetadataRequest request",
            Visibility = PInvokeVisibility.Private,
            CallingConvention = PInvokeCallingConvention.Swift,
            EmissionContext = emissionContext
        }))
            yield return line;
    }

    /// <summary>
    /// The refusal a type's metadata accessors open with when the type has an OS floor (its own,
    /// its ancestors', or one inferred from the OS symbol inventory), or <c>null</c> without one.
    /// The method holding the native metadata call must throw before it on an OS below the floor,
    /// whichever route reaches it (the runtime's <c>SwiftObjectHelper&lt;T&gt;</c>, a direct
    /// <c>ISwiftObject.GetTypeMetadata()</c> call, or another member compiled into the assembly).
    /// </summary>
    public static string? BuildMetadataRefusal(TypeDecl typeDecl) =>
        AvailabilityAttributeEmitter.BuildRuntimeAvailabilityGuardStatement(
            WrapperEmitterHelpers.MergeAvailabilityFromAncestors(null, typeDecl),
            $"Swift type '{typeDecl.SwiftTypeName.ModuleQualifiedName}'");

    /// <summary>
    /// <c>static TypeMetadata ISwiftObject.GetTypeMetadata()</c> returning
    /// <paramref name="callExpression"/>, opened by <paramref name="refusal"/> when there is one.
    /// </summary>
    public static string FormatGetTypeMetadataForwarder(string callExpression, string? refusal) =>
        refusal is null
            ? $"static TypeMetadata ISwiftObject.GetTypeMetadata() => {callExpression};"
            : $"static TypeMetadata ISwiftObject.GetTypeMetadata()\n{{\n    {refusal}\n    return {callExpression};\n}}";

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
    /// <param name="availability">Floors the type is referenced at besides <paramref name="typeDecl"/>'s,
    /// for a type the module does not declare (a framework type named by gated members).</param>
    public static void EmitIfNeeded(
        SwiftWriter swiftWriter, string moduleName,
        string moduleQualifiedSwiftName, string symbolName,
        ModuleEmissionContext ctx,
        BaseDecl? typeDecl = null,
        IReadOnlyList<AvailabilityAnnotation>? availability = null)
    {
        // one metadata accessor per type. The `SBW_GetMetadata_`-style
        // prefix is structurally distinct from method/property/constructor symbols, so the
        // per-kind metadata bucket is collision-safe by construction.
        if (!ctx.TryAddMetadataWrapperSymbol(symbolName, typeDecl is TypeDecl td ? DeclIdFactory.ForType(td) : null))
            return;

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Metadata accessor @_cdecl wrapper for {moduleQualifiedSwiftName}.");
        var merged = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(availability, typeDecl);
        var availabilityGuard = WrapperEmitterHelpers.BuildAvailabilityGuardExpression(merged);
        var funcName = $"_sbw_getMetadata_{EmitterUtility.DeterministicHash8(symbolName)}";
        var typeReference = $"Swift.unsafeBitCast({moduleQualifiedSwiftName}.self as Any.Type, to: Swift.UnsafeMutableRawPointer.self)";

        if (string.IsNullOrEmpty(availabilityGuard))
        {
            // Type is available at the module's deployment floor — reference it unconditionally.
            swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
            swiftWriter.WriteLine($"public func {funcName}() -> Swift.UnsafeMutableRawPointer {{");
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
            swiftWriter.WriteLine($"public func {funcName}() -> Swift.UnsafeMutableRawPointer? {{");
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
        // Refuse before the wrapper call: the wrapper's export reaches the type's metadata accessor,
        // which an OS below the floor may not ship. The wrapper's own #available check and the zero
        // metadata test below stay as the second line for a platform the guard does not name.
        var refusal = AvailabilityAttributeEmitter.BuildRuntimeAvailabilityGuardStatement(availability, $"Swift type '{moduleQualifiedSwiftName}'");
        // The fallback calls the library's own metadata accessor directly, past the wrapper's
        // #available check, so it runs only where the floor holds: below it the accessor may be a
        // symbol the OS does not ship (a floor inferred from the OS symbol inventory), and a call
        // through it would fault natively. Below the floor the zero metadata becomes the exception.
        var available = AvailabilityAttributeEmitter.BuildIsAvailableCondition(availability);
        var fallback = available is null
            ? "PInvoke_getMetadata_fallback()"
            : $"({available}) ? PInvoke_getMetadata_fallback() : default";
        return $$"""
            static TypeMetadata ISwiftObject.GetTypeMetadata()
            {
                {{refusal}}
                TypeMetadata __metadata;
                try
                {
                    __metadata = PInvoke_getMetadata();
                }
                catch (global::System.DllNotFoundException)
                {
                    __metadata = {{fallback}};
                }
                catch (global::System.EntryPointNotFoundException)
                {
                    __metadata = {{fallback}};
                }
                if (!__metadata.IsValid)
                    throw new global::System.PlatformNotSupportedException("{{message}}");
                return __metadata;
            }
            """;
    }
}
