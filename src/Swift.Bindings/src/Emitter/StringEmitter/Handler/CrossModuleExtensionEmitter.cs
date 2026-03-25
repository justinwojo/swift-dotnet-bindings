// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Emits C# static extension classes for cross-module type extensions.
///
/// When module B extends a type from module A, the ABI parser creates a ClassDecl
/// with both original and extension members merged. ClassHandler detects this via
/// ClassDecl.SwiftTypeName.Module != ModuleDecl.Name and delegates here.
///
/// This emitter:
/// 1. Filters to only members from the current module (extension members)
/// 2. Emits a static extension class: {TypeName}{CurrentModule}Extensions
/// 3. Instance methods → `public static RetType Method(this OrigType self, params...)`
/// 4. Properties → Get/Set extension method pairs
/// 5. P/Invoke uses existing mangled names (no Swift wrappers needed)
/// </summary>
public static class CrossModuleExtensionEmitter
{
    /// <summary>
    /// Emits a C# static extension class for cross-module type extensions.
    /// </summary>
    public static void Emit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        Conductor conductor,
        IEnvironment env,
        ILogger logger)
    {
        var typeDatabase = env.TypeDatabase;
        var origModule = classDecl.SwiftTypeName.Module;
        var currentModule = moduleDecl.Name;

        // Resolve the C# type name for the original type
        var origCSharpType = ResolveOriginalTypeCSharpName(classDecl, typeDatabase, conductor.NamespacePatternResolver);

        // Collect members from the current module only
        var methods = new List<MethodDecl>();
        var properties = new List<PropertyDecl>();

        foreach (var method in classDecl.Methods)
        {
            if (method.ModuleDecl?.Name != currentModule)
                continue;
            if (method.IsConstructor)
                continue;
            methods.Add(method);
        }

        foreach (var property in classDecl.Properties)
        {
            if (property.ModuleDecl?.Name != currentModule)
                continue;
            properties.Add(property);
        }

        if (methods.Count == 0 && properties.Count == 0)
        {
            logger.LogDebug("Cross-module extension {Type} from {Module}: no members from current module, skipping.",
                classDecl.Name, currentModule);
            return;
        }

        var className = $"{classDecl.Name}{currentModule}Extensions";
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        var moduleLibPath = typeDatabase.GetLibraryPath(currentModule);

        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {classDecl.Name} defined in {currentModule}.");
        csWriter.WriteLine($"/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        int emittedCount = 0;
        var emittedSignatures = new HashSet<string>();

        // Emit properties as Get/Set methods
        foreach (var property in properties)
        {
            if (TryEmitPropertyExtension(csWriter, property, classDecl, origCSharpType, moduleLibPath, wrapperLibPath, typeDatabase, emittedSignatures, logger))
                emittedCount++;
        }

        // Emit methods
        foreach (var method in methods)
        {
            if (method.IsAccessor)
                continue; // Property accessors handled above

            if (TryEmitMethodExtension(csWriter, method, classDecl, origCSharpType, moduleLibPath, wrapperLibPath, typeDatabase, emittedSignatures, logger))
                emittedCount++;
        }

        // Emit NativeMethods if we emitted anything
        if (emittedCount > 0)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("private static partial class NativeMethods");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (var property in properties)
            {
                EmitPropertyNativeMethods(csWriter, property, classDecl, moduleLibPath, wrapperLibPath, typeDatabase, logger);
            }

            foreach (var method in methods)
            {
                if (method.IsAccessor)
                    continue;
                EmitMethodNativeMethod(csWriter, method, classDecl, moduleLibPath, wrapperLibPath, typeDatabase, logger);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        if (emittedCount > 0)
        {
            logger.LogInformation("Emitted {Count} cross-module extension members for {Type} from {Module}",
                emittedCount, classDecl.Name, currentModule);
        }
    }

    // ==================== Method Extension Emission ====================

    private static bool TryEmitMethodExtension(
        CSharpWriter csWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string origCSharpType,
        string moduleLibPath,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        ILogger logger)
    {
        // Gate: skip generic methods
        if (method.IsGeneric)
            return false;

        // Gate: skip async methods (complex marshalling)
        if (method.IsAsync)
            return false;

        // Gate: skip throwing methods (complex error handling)
        if (method.Throws)
            return false;

        // Gate: skip mutating methods (value type semantics)
        if (method.IsMutating)
            return false;

        // Resolve return type
        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null)
            return false;

        // Build parameter list — skip methods with unsupported param types
        var parameters = new List<(string name, string csharpType, string pinvokeExpr, TypeSpec typeSpec)>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            var paramCategory = ClassifyParameterType(arg.SwiftTypeSpec, typeDatabase);
            if (paramCategory == null)
                return false;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var csharpType = ResolveCSharpTypeName(arg.SwiftTypeSpec, typeDatabase);
            var pinvokeExpr = GetPInvokeArgExpression(paramName, paramCategory.Value);
            parameters.Add((paramName, csharpType, pinvokeExpr, arg.SwiftTypeSpec));
        }

        // Deduplicate — include static/instance to distinguish overload pairs
        var methodName = NameProvider.ToPascalCase(method.Name);
        var isStatic = method.MethodType == MethodType.Static;
        var staticPrefix = isStatic ? "static:" : "instance:";
        var signatureKey = $"{staticPrefix}{methodName}({string.Join(",", parameters.Select(p => p.csharpType))})";
        if (!emittedSignatures.Add(signatureKey))
            return false;

        var pInvokeName = $"PInvoke_{methodName}_{emittedSignatures.Count}";
        var csharpReturnType = returnCategory.Value == ReturnKind.Void || returnTypeSpec == null
            ? "void"
            : ResolveCSharpTypeName(returnTypeSpec, typeDatabase);

        // Build parameter string
        var paramParts = new List<string> { $"this {origCSharpType} self" };
        foreach (var (name, csharpType, _, _) in parameters)
        {
            if (MarshallingHelpers.IsBoolType(csharpType))
                paramParts.Add($"bool {name}");
            else
                paramParts.Add($"{csharpType} {name}");
        }

        if (isStatic)
        {
            // Static methods don't get `this` parameter
            paramParts.RemoveAt(0);
        }

        csWriter.WriteLine();

        // Emit public extension method
        csWriter.WriteLine($"public static {csharpReturnType} {methodName}({string.Join(", ", paramParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        EmitMethodBody(csWriter, method, pInvokeName, parameters, returnCategory.Value, csharpReturnType, isStatic, classDecl.IsObjCRooted, typeDatabase);

        csWriter.Indent--;
        csWriter.WriteLine("}");

        return true;
    }

    /// <summary>
    /// Gets the self expression for P/Invoke calls.
    /// ObjC-rooted classes use .Handle (ObjC pointer), pure Swift classes use .Payload.DangerousGetHandle().
    /// </summary>
    private static string GetSelfExpression(bool isObjCRooted) =>
        isObjCRooted ? "self.Handle" : "self.Payload.DangerousGetHandle()";

    private static void EmitMethodBody(
        CSharpWriter csWriter,
        MethodDecl method,
        string pInvokeName,
        List<(string name, string csharpType, string pinvokeExpr, TypeSpec typeSpec)> parameters,
        ReturnKind returnCategory,
        string csharpReturnType,
        bool isStatic,
        bool isObjCRooted,
        ITypeDatabase typeDatabase)
    {
        var nativeArgs = new List<string>();

        // Non-frozen struct returns use SwiftIndirectResult as first param
        if (returnCategory == ReturnKind.NonFrozenStruct)
            nativeArgs.Add("indirectResult");

        // Self parameter
        if (!isStatic)
            nativeArgs.Add(GetSelfExpression(isObjCRooted));

        // Method parameters
        foreach (var (name, _, pinvokeExpr, _) in parameters)
        {
            nativeArgs.Add(pinvokeExpr);
        }

        // Use the method's real mangled name as the NativeMethods entry
        var nativeMethodName = GetNativeMethodName(method);
        var nativeCall = $"NativeMethods.{nativeMethodName}({string.Join(", ", nativeArgs)})";

        EmitReturnValueMarshalling(csWriter, returnCategory, nativeCall, csharpReturnType);
    }

    // ==================== Property Extension Emission ====================

    private static bool TryEmitPropertyExtension(
        CSharpWriter csWriter,
        PropertyDecl property,
        ClassDecl classDecl,
        string origCSharpType,
        string moduleLibPath,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        ILogger logger)
    {
        // Gate: skip static properties
        if (property.IsStatic)
            return false;

        // Classify property type
        var returnCategory = ClassifyReturnType(property.SwiftTypeSpec, typeDatabase);
        if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            return false;

        var propertyName = NameProvider.ToPascalCase(property.Name);
        if (!emittedSignatures.Add($"Get{propertyName}"))
            return false;

        var csharpType = returnCategory.Value == ReturnKind.Void || property.SwiftTypeSpec == null
            ? "void"
            : ResolveCSharpTypeName(property.SwiftTypeSpec, typeDatabase);

        // Emit getter
        var getterAccessor = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getterAccessor != null)
        {
            csWriter.WriteLine();
            csWriter.WriteLine($"public static {csharpType} Get{propertyName}(this {origCSharpType} self)");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            var nativeMethodName = GetNativeMethodName(getterAccessor.Method);
            var nativeArgs = new List<string>();
            if (returnCategory.Value == ReturnKind.NonFrozenStruct)
                nativeArgs.Add("indirectResult");
            nativeArgs.Add(GetSelfExpression(classDecl.IsObjCRooted));
            var nativeCall = $"NativeMethods.{nativeMethodName}({string.Join(", ", nativeArgs)})";

            EmitReturnValueMarshalling(csWriter, returnCategory.Value, nativeCall, csharpType);

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        // Emit setter (primitives only)
        var setterAccessor = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setterAccessor != null && returnCategory.Value == ReturnKind.Primitive)
        {
            var setParamType = MarshallingHelpers.IsBoolType(csharpType) ? "bool" : csharpType;
            csWriter.WriteLine();
            csWriter.WriteLine($"public static void Set{propertyName}(this {origCSharpType} self, {setParamType} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            var nativeMethodName = GetNativeMethodName(setterAccessor.Method);
            csWriter.WriteLine($"NativeMethods.{nativeMethodName}(value, {GetSelfExpression(classDecl.IsObjCRooted)});");

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        return true;
    }

    // ==================== P/Invoke Emission ====================

    private static void EmitMethodNativeMethod(
        CSharpWriter csWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string moduleLibPath,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        if (method.IsGeneric || method.IsAsync || method.Throws || method.IsMutating || method.IsAccessor)
            return;

        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null)
            return;

        // Build P/Invoke parameters — skip if any param is unsupported
        var pinvokeParams = new List<string>();
        bool usesIndirectResult = returnCategory.Value == ReturnKind.NonFrozenStruct;

        if (usesIndirectResult)
            pinvokeParams.Add("SwiftIndirectResult result");

        // Self parameter (instance methods use SwiftSelf)
        bool isStatic = method.MethodType == MethodType.Static;
        if (!isStatic)
            pinvokeParams.Add("SwiftSelf self_");

        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            var paramCategory = ClassifyParameterType(arg.SwiftTypeSpec, typeDatabase);
            if (paramCategory == null)
                return;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var pinvokeType = ResolvePInvokeParamType(arg.SwiftTypeSpec, paramCategory.Value, typeDatabase);
            if (arg.SwiftTypeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool")
                pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {paramName}");
            else
                pinvokeParams.Add($"{pinvokeType} {paramName}");
        }

        var (entryPoint, needsWrapperLib) = PInvokeEmitter.ComputeEntryPoint(method);
        var libPath = needsWrapperLib && typeDatabase.AsyncLibraryName != null
            ? typeDatabase.AsyncLibraryName
            : moduleLibPath;

        var pinvokeReturnType = ExtensionMarshallingHelper.ResolvePInvokeReturnType(returnTypeSpec, returnCategory.Value, typeDatabase, usesIndirectResult);

        var nativeMethodName = GetNativeMethodName(method);

        // Cross-module extension P/Invokes always use CallConvSwift because both paths
        // use swiftcc: direct Swift symbols inherently use swiftcc, and wrapper library
        // functions use @_silgen_name (which preserves swiftcc, unlike @_cdecl).
        // SwiftSelf and SwiftIndirectResult only map to correct registers under swiftcc.
        var methodCallingConvention = PInvokeCallingConvention.Swift;

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = libPath,
            EntryPoint = entryPoint,
            MethodName = nativeMethodName,
            ReturnType = pinvokeReturnType,
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = methodCallingConvention,
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    private static void EmitPropertyNativeMethods(
        CSharpWriter csWriter,
        PropertyDecl property,
        ClassDecl classDecl,
        string moduleLibPath,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        if (property.IsStatic)
            return;

        var returnCategory = ClassifyReturnType(property.SwiftTypeSpec, typeDatabase);
        if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            return;

        // Getter P/Invoke
        var getterAccessor = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getterAccessor != null)
        {
            var method = getterAccessor.Method;
            var pinvokeParams = new List<string>();
            bool usesIndirectResult = returnCategory.Value == ReturnKind.NonFrozenStruct;

            if (usesIndirectResult)
                pinvokeParams.Add("SwiftIndirectResult result");

            pinvokeParams.Add("SwiftSelf self_");

            var (entryPoint, needsWrapperLib) = PInvokeEmitter.ComputeEntryPoint(method);
            var libPath = needsWrapperLib && typeDatabase.AsyncLibraryName != null
                ? typeDatabase.AsyncLibraryName
                : moduleLibPath;

            var pinvokeReturnType = ExtensionMarshallingHelper.ResolvePInvokeReturnType(property.SwiftTypeSpec, returnCategory.Value, typeDatabase, usesIndirectResult);

            var nativeMethodName = GetNativeMethodName(method);

            // Cross-module extension P/Invokes always use CallConvSwift — see method emission comment.
            var callingConvention = PInvokeCallingConvention.Swift;

            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = libPath,
                EntryPoint = entryPoint,
                MethodName = nativeMethodName,
                ReturnType = pinvokeReturnType,
                ParametersString = string.Join(", ", pinvokeParams),
                CallingConvention = callingConvention,
                Visibility = PInvokeVisibility.Internal
            });
            csWriter.WriteLine();
        }

        // Setter P/Invoke (primitives only)
        var setterAccessor = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setterAccessor != null && returnCategory.Value == ReturnKind.Primitive)
        {
            var method = setterAccessor.Method;
            var pinvokeParams = new List<string>();

            var pinvokeType = ResolvePInvokeParamType(property.SwiftTypeSpec, ParamKind.Primitive, typeDatabase);
            if (property.SwiftTypeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool")
                pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool value");
            else
                pinvokeParams.Add($"{pinvokeType} value");

            pinvokeParams.Add("SwiftSelf self_");

            var (entryPoint, needsWrapperLib) = PInvokeEmitter.ComputeEntryPoint(method);
            var libPath = needsWrapperLib && typeDatabase.AsyncLibraryName != null
                ? typeDatabase.AsyncLibraryName
                : moduleLibPath;

            var nativeMethodName = GetNativeMethodName(method);

            // Cross-module extension P/Invokes always use CallConvSwift — see method emission comment.
            var setterCallingConvention = PInvokeCallingConvention.Swift;

            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = libPath,
                EntryPoint = entryPoint,
                MethodName = nativeMethodName,
                ReturnType = "void",
                ParametersString = string.Join(", ", pinvokeParams),
                CallingConvention = setterCallingConvention,
                Visibility = PInvokeVisibility.Internal
            });
            csWriter.WriteLine();
        }
    }

    // ==================== Type Classification (delegated to ExtensionMarshallingHelper) ====================

    // ==================== Type Resolution (delegated to ExtensionMarshallingHelper) ====================

    private static string ResolveOriginalTypeCSharpName(ClassDecl classDecl, ITypeDatabase typeDatabase, NamespacePatternResolver resolver)
    {
        // Try TypeDatabase lookup first
        if (typeDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var typeRecord))
            return typeRecord.CSharpTypeName.FullyQualifiedName;

        // Fallback: use namespace resolver for the original module
        return $"{resolver.ResolveNamespace(classDecl.SwiftTypeName.Module)}.{classDecl.Name}";
    }

    private static string GetNativeMethodName(MethodDecl method)
    {
        return NameProvider.GetPInvokeName(method);
    }

}
