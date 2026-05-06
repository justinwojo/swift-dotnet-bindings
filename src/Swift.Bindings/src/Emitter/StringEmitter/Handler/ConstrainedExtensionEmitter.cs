// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Emits C# extension method classes for constrained-extension properties on generic types.
///
/// When a generic type has constrained extensions like:
///   extension GenericType where T == ConcreteA { var prop: String { get } }
///   extension GenericType where T == ConcreteB { var prop: String { get } }
///
/// C# generics cannot dispatch among specializations at the call site. Rather than
/// skip these entirely, this emitter creates closed extension methods:
///   public static class GenericTypeConcreteAExtensions {
///       public static string GetProp(this GenericType&lt;ConcreteA&gt; self) { ... }
///   }
///
/// Each specialization gets its own @_cdecl Swift wrapper taking the concrete closed
/// generic type — no generic dispatch or metadata passing needed.
/// </summary>
public static class ConstrainedExtensionEmitter
{
    /// <summary>
    /// Scans a generic type for constrained-extension property groups and emits
    /// C# extension method classes for each concrete type specialization.
    /// </summary>
    public static void EmitConstrainedExtensions(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        if (!typeDecl.IsGeneric) return;

        // Value-type-projected frozen-struct parents are skipped: the call sites at
        // ~lines 315/331 emit `self.Payload.DangerousGetHandle()`, which assumes the
        // SafeHandle-backed `Payload` projection used by classes, non-frozen structs,
        // AND class-projected frozen structs (FrozenStructHandler.cs:168 emits
        // `public SwiftSafeHandle<T> Payload`). A *value-type*-projected frozen
        // struct (the `else` branch at FrozenStructHandler.cs:207) has no `Payload`
        // member at all — only direct backing fields matching Swift's memory layout.
        // Routing that shape through `self.Payload.DangerousGetHandle()` produces
        // uncompilable C#. The original bug 6 target (WeatherKit `Forecast<T>`
        // .Summary / .MinuteWeather) is a resilient (non-frozen) struct, so the
        // SafeHandle path covers it. A value-type-projected frozen-struct generic
        // with a same-type-constrained property is theoretical for current
        // validation libraries; supporting it requires teaching this emitter to
        // emit address/buffer-based dispatch, which is out of scope for Bundle 02.
        if (typeDecl is StructDecl sd && sd.IsFrozen)
        {
            var typeRecord = typeDatabase.GetTypeRecordOrThrow(typeDecl.SwiftTypeName);
            if (!MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                logger.LogDebug(
                    $"Skipping constrained-extension emission for value-type-projected frozen struct {typeDecl.Name}: " +
                    "ConstrainedExtensionEmitter assumes SafeHandle-backed Payload, " +
                    "but value-type-projected frozen structs expose direct backing fields. " +
                    "See bug-0.10.0-property-accessor-bound-to-specialization-symbol.md.");
                return;
            }
        }

        // Group constrained-extension properties by concrete type
        var specializations = FindConstrainedSpecializations(typeDecl);
        if (specializations.Count == 0) return;

        var moduleName = typeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        foreach (var (concreteTypeName, properties) in specializations)
        {
            EmitSpecializationClass(
                csWriter, swiftWriter, typeDecl, concreteTypeName, properties,
                moduleName, wrapperLibPath, typeDatabase, emissionContext, logger);
        }
    }

    /// <summary>
    /// Finds constrained-extension property groups: properties whose accessor
    /// carries a same-type equality constraint (e.g.
    /// `extension Wrapper where T == Concrete`). Returns a map from concrete
    /// type to list of properties for that specialization.
    ///
    /// Both single-specialization (one sibling) and multi-specialization (many
    /// siblings) cases are emitted, because in either case the accessor mangled
    /// name is bound to the closed generic instantiation — not the open generic
    /// — and emitting at the open-generic class level would PInvoke the wrong
    /// symbol for non-matching instantiations. Multi-spec groups are accepted
    /// only when EVERY sibling carries a same-type constraint; mixed
    /// open + constrained groups are rejected to avoid namespace collision with
    /// the open-generic property emission.
    /// </summary>
    internal static Dictionary<SwiftTypeName, List<PropertyDecl>> FindConstrainedSpecializations(TypeDecl typeDecl)
    {
        var result = new Dictionary<SwiftTypeName, List<PropertyDecl>>();

        if (!typeDecl.IsGeneric)
            return result;

        // Group properties by (name, isStatic) so we can detect mixed
        // open-generic + constrained-extension siblings.
        var groups = new Dictionary<(string Name, bool IsStatic), List<PropertyDecl>>();
        foreach (var property in typeDecl.Properties)
        {
            var key = (property.Name, property.IsStatic);
            if (!groups.ContainsKey(key))
                groups[key] = new List<PropertyDecl>();
            groups[key].Add(property);
        }

        foreach (var (_, siblings) in groups)
        {
            // For both single- and multi-sibling groups: every sibling must
            // carry a parseable same-type constraint. Mixed open + constrained
            // groups are rejected so we don't emit closed extension methods
            // that conflict with an open-generic property of the same name.
            bool allConstrained = true;
            foreach (var sibling in siblings)
            {
                if (ExtractSameTypeConstraint(sibling) == null)
                {
                    allConstrained = false;
                    break;
                }
            }

            if (!allConstrained) continue;

            // Group each sibling under its concrete type.
            foreach (var sibling in siblings)
            {
                var concreteType = ExtractSameTypeConstraint(sibling)!;
                if (!result.ContainsKey(concreteType))
                    result[concreteType] = new List<PropertyDecl>();
                result[concreteType].Add(sibling);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the concrete type from a same-type equality constraint on a property's
    /// getter accessor. Returns null if the property is not from a constrained extension.
    /// </summary>
    internal static SwiftTypeName? ExtractSameTypeConstraint(PropertyDecl property)
    {
        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter == null) return null;

        foreach (var genericParam in getter.Method.GenericParameters)
        {
            foreach (var conformance in genericParam.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.ConcreteType)
                    return conformance.ConformanceTarget;
            }
        }

        return null;
    }

    private static void EmitSpecializationClass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        SwiftTypeName concreteTypeName,
        List<PropertyDecl> properties,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        // Resolve C# type names
        var parentCsName = typeDecl.Name;
        var concreteCsName = ResolveCSharpName(concreteTypeName, typeDatabase);
        if (concreteCsName == null)
        {
            logger.LogWarning(
                "ConstrainedExtensionEmitter: Cannot resolve C# name for concrete type {Type}, skipping.",
                concreteTypeName);
            return;
        }

        // Primitive types (int, uint, float, etc.) cannot satisfy ISwiftObject constraints
        // on generic parent types, so skip them.
        if (IsCSharpPrimitiveType(concreteCsName))
        {
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping primitive concrete type {Type} — cannot satisfy ISwiftObject.",
                concreteCsName);
            return;
        }

        var closedGenericCsType = $"{parentCsName}<{concreteCsName}>";
        var className = $"{parentCsName}{SanitizeTypeName(concreteTypeName.Name)}Extensions";

        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {closedGenericCsType} from constrained Swift extensions.");
        csWriter.WriteLine("/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        int emittedCount = 0;
        var pinvokeDeclarations = new List<Action>();

        foreach (var property in properties)
        {
            if (TryEmitPropertyExtension(
                csWriter, swiftWriter, property, typeDecl, concreteTypeName,
                closedGenericCsType, moduleName, wrapperLibPath,
                typeDatabase, emissionContext, pinvokeDeclarations, logger))
            {
                emittedCount++;
            }
        }

        // Emit NativeMethods class with P/Invoke declarations
        if (emittedCount > 0 && pinvokeDeclarations.Count > 0)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("private static partial class NativeMethods");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (var emitPInvoke in pinvokeDeclarations)
                emitPInvoke();

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        if (emittedCount > 0)
        {
            logger.LogInformation(
                "Emitted {Count} constrained-extension members for {Type}<{Concrete}>",
                emittedCount, typeDecl.Name, concreteTypeName.Name);
        }
    }

    /// <summary>
    /// Classifies the return shape of a constrained-extension property for emission.
    /// </summary>
    private enum CEReturnShape
    {
        /// <summary>Swift.String — returned via Utf8Slice in a 16-byte caller buffer.</summary>
        String,
        /// <summary>Swift primitive (Int*, Bool, Float, Double, ...) — returned by value.</summary>
        Primitive,
        /// <summary>Resilient (non-frozen) Swift struct (e.g. Foundation.Data) — returned via indirect-result buffer sized by the type's VWT.</summary>
        NonFrozenStruct,
    }

    private static bool TryEmitPropertyExtension(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyDecl property,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string closedGenericCsType,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        List<Action> pinvokeDeclarations,
        ILogger logger)
    {
        // Classify return shape. String and Primitive are returned by value; NonFrozenStruct
        // (e.g. Foundation.Data on StoreKit2's VerificationResult.headerData) needs the
        // indirect-result buffer shape used by ExtensionMarshallingHelper. Other shapes
        // (frozen-as-value structs like Foundation.Date / Foundation.UUID, ObjC classes,
        // Swift classes, and generic-parameter returns like VerificationResult.payloadValue)
        // remain unsupported here — see the doc resolution for follow-up scope.
        CEReturnShape shape;
        if (WitnessDispatchEmitter.IsStringType(property.SwiftTypeSpec))
        {
            shape = CEReturnShape.String;
        }
        else
        {
            var returnCategory = ClassifyReturnType(property.SwiftTypeSpec, typeDatabase);
            if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            {
                logger.LogDebug(
                    "ConstrainedExtensionEmitter: Skipping property {Name} — unsupported return type.",
                    property.Name);
                return false;
            }
            shape = returnCategory.Value switch
            {
                ReturnKind.Primitive => CEReturnShape.Primitive,
                ReturnKind.NonFrozenStruct => CEReturnShape.NonFrozenStruct,
                _ => (CEReturnShape)(-1),
            };
            if ((int)shape < 0)
            {
                logger.LogDebug(
                    "ConstrainedExtensionEmitter: Skipping property {Name} — return kind {ReturnKind} is not yet supported in constrained-extension emission.",
                    property.Name, returnCategory.Value);
                return false;
            }
        }

        var propertyName = NameProvider.ToPascalCase(property.Name);
        var csharpReturnType = shape == CEReturnShape.String
            ? "string"
            : ResolveCSharpTypeName(property.SwiftTypeSpec, typeDatabase);

        // Build @_cdecl symbol name
        var safeConcreteName = SanitizeTypeName(concreteTypeName.Name);
        var symbolName = $"SBW_CEGet_{moduleName}_{parentTypeDecl.Name}_{safeConcreteName}_{property.Name}";

        // Dedup guard
        if (!emissionContext.TryAddPropertyWrapperSymbol(symbolName))
            return false;

        // ----- C# extension method -----
        csWriter.WriteLine();
        csWriter.WriteLine($"public static {csharpReturnType} Get{propertyName}(this {closedGenericCsType} self)");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        switch (shape)
        {
            case CEReturnShape.String:
                // String: allocate Utf8Slice buffer, call P/Invoke, marshal Utf8Slice to string
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("void* _cdeclBuf = null;");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("_cdeclBuf = System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(nint.Size * 2));");
                csWriter.WriteLine("var resultPtr = (IntPtr)_cdeclBuf;");
                csWriter.WriteLine($"NativeMethods.{symbolName}(resultPtr, self.Payload.DangerousGetHandle());");
                csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("System.Runtime.InteropServices.NativeMemory.Free(_cdeclBuf);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                break;

            case CEReturnShape.Primitive:
                // Primitive: direct P/Invoke call
                csWriter.WriteLine($"return NativeMethods.{symbolName}(self.Payload.DangerousGetHandle());");
                break;

            case CEReturnShape.NonFrozenStruct:
                // Indirect-result shape: allocate VWT-sized buffer, pass SwiftIndirectResult
                // to the @_cdecl wrapper, then marshal the value out. Must match the existing
                // PropertyHandler emission so MarshalFromSwift<T> sees the same layout.
                //
                // Ownership: for non-frozen structs / classes projected as ISwiftObject,
                // SwiftMarshal.MarshalFromSwift<T>(buffer) wraps the buffer in the returned
                // SafeHandle and the wrapper takes ownership. We must NOT free on the
                // success path (use-after-free / double-free on disposal of the returned
                // object). The try / catch+Free+rethrow shape mirrors
                // ExtensionMarshallingHelper.cs:263 (ReturnKind.NonFrozenStruct): on the
                // exceptional path the wrapper call or MarshalFromSwift throws before
                // ownership transfers, so the buffer must be freed to avoid a leak; on
                // the success path control returns through MarshalFromSwift before
                // reaching the catch and the SafeHandle owns the buffer thereafter.
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        var metadata = SwiftObjectHelper<{{csharpReturnType}}>.GetTypeMetadata();
                        IntPtr buffer = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc(metadata.Size);
                        try
                        {
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            NativeMethods.{{symbolName}}(indirectResult, self.Payload.DangerousGetHandle());
                            return SwiftMarshal.MarshalFromSwift<{{csharpReturnType}}>(buffer);
                        }
                        catch
                        {
                            System.Runtime.InteropServices.NativeMemory.Free((void*)buffer);
                            throw;
                        }
                    }
                    """);
                break;
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ----- Swift @_cdecl wrapper -----
        EmitSwiftGetterWrapper(swiftWriter, property, parentTypeDecl, concreteTypeName,
            symbolName, moduleName, shape, emissionContext);

        // ----- Queue P/Invoke declaration -----
        var capturedSymbol = symbolName;
        var capturedShape = shape;
        var capturedReturnType = csharpReturnType;
        pinvokeDeclarations.Add(() =>
        {
            var pinvokeParams = new List<string>();
            string pinvokeReturnType;

            switch (capturedShape)
            {
                case CEReturnShape.String:
                    pinvokeParams.Add("IntPtr resultPtr");
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.NonFrozenStruct:
                    pinvokeParams.Add("SwiftIndirectResult indirectResult");
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                default: // Primitive
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = capturedReturnType;
                    break;
            }

            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = capturedSymbol,
                MethodName = capturedSymbol,
                ReturnType = pinvokeReturnType,
                ParametersString = string.Join(", ", pinvokeParams),
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Internal
            });
            csWriter.WriteLine();
        });

        return true;
    }

    private static void EmitSwiftGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl property,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string symbolName,
        string moduleName,
        CEReturnShape shape,
        ModuleEmissionContext emissionContext)
    {
        var parentSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var concreteSwiftName = concreteTypeName.ModuleQualifiedName;
        var closedGenericSwiftType = $"{parentSwiftName}<{concreteSwiftName}>";

        // Ensure SBW_Utf8Slice is available for string properties
        if (shape == CEReturnShape.String)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constrained-extension getter for {{closedGenericSwiftType}}.{{property.Name}}.
            // Concrete specialization — no generic dispatch needed.
            """);

        // Build parameter list — String and NonFrozenStruct use indirect-result style
        // (a caller-provided buffer in resultPtr); Primitive returns by value.
        var swiftParams = new List<string>();
        if (shape == CEReturnShape.String || shape == CEReturnShape.NonFrozenStruct)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        swiftParams.Add("_ self_: UnsafeRawPointer");

        var returnClause = shape == CEReturnShape.Primitive
            ? $" -> {RenderSwiftReturnType(property)}"
            : "";
        var swiftParamString = string.Join(", ", swiftParams);

        // Swift function name uses hash to avoid collisions
        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var swiftFuncName = $"_sbw_ceget_{property.Name}_{hash}";

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor: false,
            WrapperEmitterHelpers.MergeAvailability(property.AvailabilityAnnotations, parentTypeDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Reconstruct self from pointer — structs / enums use memory binding, classes use Unmanaged
        if (parentTypeDecl is ClassDecl)
            swiftWriter.WriteLine($"let obj = Unmanaged<{closedGenericSwiftType}>.fromOpaque(self_).takeUnretainedValue()");
        else
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {closedGenericSwiftType}.self).pointee");

        // Emit getter body
        var propAccess = $"obj.{property.Name}";
        switch (shape)
        {
            case CEReturnShape.String:
                StringReturnEmitter.EmitGetterBody(swiftWriter, propAccess);
                break;
            case CEReturnShape.Primitive:
                swiftWriter.WriteLine($"return {propAccess}");
                break;
            case CEReturnShape.NonFrozenStruct:
                // Indirect-result write — matches PropertyHandler's emission shape so
                // `MarshalFromSwift<T>(buffer)` reads the correct VWT-sized layout.
                // Module-qualified type spec is required for .initializeMemory(as:) —
                // unqualified names (e.g. `P256.Signing.ECDSASignature`) won't resolve
                // unless the wrapper file imports the source module (e.g. CryptoKit),
                // which we can't guarantee. Fully qualified (`CryptoKit.P256.…`)
                // resolves through the framework's transitive imports.
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {RenderSwiftReturnType(property)}.self, repeating: result, count: 1)");
                break;
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string RenderSwiftReturnType(PropertyDecl property)
    {
        return ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(property.SwiftTypeSpec);
    }

    private static string? ResolveCSharpName(SwiftTypeName swiftTypeName, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record.CSharpTypeName.FullyQualifiedName;

        // Fallback: use simple name (the type might be in the same module)
        return swiftTypeName.Name;
    }

    private static string SanitizeTypeName(string name)
    {
        // Strip every Swift type-syntax character that would produce an invalid C#
        // identifier. Array `[T]`, generic `T<U>`, tuple `(A, B)` and qualified
        // `Module.T` all need to map to bare-word identifiers; otherwise the emitted
        // class name (e.g. `AlamofireExtensionSecCertificate]Extensions`) is a
        // syntax error. Order matters: drop the closer (`>`, `]`, `)`) so the prefix
        // collapses cleanly, replace the opener / separator with underscores so a
        // composed name (`Dictionary_String_Int`) stays readable.
        return name
            .Replace(".", "_")
            .Replace("<", "_")
            .Replace(">", "")
            .Replace("[", "_")
            .Replace("]", "")
            .Replace("(", "_")
            .Replace(")", "")
            .Replace(",", "_")
            .Replace(" ", "")
            .Replace("?", "_Optional");
    }

    private static bool IsCSharpPrimitiveType(string typeName) => typeName switch
    {
        "int" or "uint" or "long" or "ulong" or "short" or "ushort" or
        "byte" or "sbyte" or "float" or "double" or "bool" or "char" or
        "nint" or "nuint" or "decimal" or "string" => true,
        _ => false
    };
}
