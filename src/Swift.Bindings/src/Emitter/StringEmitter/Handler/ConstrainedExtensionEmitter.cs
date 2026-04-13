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
    /// Finds constrained-extension property groups: properties with same-name siblings
    /// where each sibling's accessor carries a same-type equality constraint.
    /// Returns a map from concrete type to list of properties for that specialization.
    /// </summary>
    internal static Dictionary<SwiftTypeName, List<PropertyDecl>> FindConstrainedSpecializations(TypeDecl typeDecl)
    {
        var result = new Dictionary<SwiftTypeName, List<PropertyDecl>>();

        // Find properties with same-name siblings (multi-specialization conflict)
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
            if (siblings.Count <= 1) continue;

            // Check that ALL siblings have parseable same-type constraints
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

            // Group each sibling under its concrete type
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
        bool isString = WitnessDispatchEmitter.IsStringType(property.SwiftTypeSpec);

        // For non-string types, classify return type.
        // Only primitive returns are supported — ObjC classes, Swift classes, and non-frozen
        // structs need IntPtr/indirect-result marshalling that this emitter doesn't implement.
        if (!isString)
        {
            var returnCategory = ClassifyReturnType(property.SwiftTypeSpec, typeDatabase);
            if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            {
                logger.LogDebug(
                    "ConstrainedExtensionEmitter: Skipping property {Name} — unsupported return type.",
                    property.Name);
                return false;
            }
            if (returnCategory.Value != ReturnKind.Primitive)
            {
                logger.LogDebug(
                    "ConstrainedExtensionEmitter: Skipping property {Name} — non-primitive return ({ReturnKind}) requires complex marshalling.",
                    property.Name, returnCategory.Value);
                return false;
            }
        }

        var propertyName = NameProvider.ToPascalCase(property.Name);
        var csharpReturnType = isString
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

        if (isString)
        {
            // String: allocate buffer, call P/Invoke, marshal Utf8Slice to string
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
        }
        else
        {
            // Primitive: direct P/Invoke call
            csWriter.WriteLine($"return NativeMethods.{symbolName}(self.Payload.DangerousGetHandle());");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ----- Swift @_cdecl wrapper -----
        EmitSwiftGetterWrapper(swiftWriter, property, parentTypeDecl, concreteTypeName,
            symbolName, moduleName, isString, emissionContext);

        // ----- Queue P/Invoke declaration -----
        var capturedSymbol = symbolName;
        var capturedIsString = isString;
        var capturedReturnType = csharpReturnType;
        pinvokeDeclarations.Add(() =>
        {
            var pinvokeParams = new List<string>();
            string pinvokeReturnType;

            if (capturedIsString)
            {
                pinvokeParams.Add("IntPtr resultPtr");
                pinvokeParams.Add("IntPtr _self");
                pinvokeReturnType = "void";
            }
            else
            {
                pinvokeParams.Add("IntPtr _self");
                pinvokeReturnType = capturedReturnType;
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
        bool isString,
        ModuleEmissionContext emissionContext)
    {
        var parentSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var concreteSwiftName = concreteTypeName.ModuleQualifiedName;
        var closedGenericSwiftType = $"{parentSwiftName}<{concreteSwiftName}>";

        // Ensure SBW_Utf8Slice is available for string properties
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constrained-extension getter for {{closedGenericSwiftType}}.{{property.Name}}.
            // Concrete specialization — no generic dispatch needed.
            """);

        // Build parameter list
        var swiftParams = new List<string>();
        if (isString)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        swiftParams.Add("_ self_: UnsafeRawPointer");

        var returnClause = isString ? "" : $" -> {RenderSwiftReturnType(property)}";
        var swiftParamString = string.Join(", ", swiftParams);

        // Swift function name uses hash to avoid collisions
        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var swiftFuncName = $"_sbw_ceget_{property.Name}_{hash}";

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor: false,
            WrapperEmitterHelpers.MergeAvailability(property.AvailabilityAnnotations, parentTypeDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Reconstruct self from pointer — structs use memory binding, classes use Unmanaged
        if (parentTypeDecl is ClassDecl)
            swiftWriter.WriteLine($"let obj = Unmanaged<{closedGenericSwiftType}>.fromOpaque(self_).takeUnretainedValue()");
        else
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {closedGenericSwiftType}.self).pointee");

        // Emit getter body
        var propAccess = $"obj.{property.Name}";
        if (isString)
        {
            StringReturnEmitter.EmitGetterBody(swiftWriter, propAccess);
        }
        else
        {
            swiftWriter.WriteLine($"return {propAccess}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string RenderSwiftReturnType(PropertyDecl property)
    {
        return ExistentialBypassEmitter.RenderSwiftTypeSpec(property.SwiftTypeSpec);
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
        return name.Replace(".", "_").Replace("<", "_").Replace(">", "").Replace(",", "_").Replace(" ", "");
    }

    private static bool IsCSharpPrimitiveType(string typeName) => typeName switch
    {
        "int" or "uint" or "long" or "ulong" or "short" or "ushort" or
        "byte" or "sbyte" or "float" or "double" or "bool" or "char" or
        "nint" or "nuint" or "decimal" or "string" => true,
        _ => false
    };
}
