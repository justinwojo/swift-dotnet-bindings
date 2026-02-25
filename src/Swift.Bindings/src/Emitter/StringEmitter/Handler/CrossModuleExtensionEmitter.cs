// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

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
        var origCSharpType = ResolveOriginalTypeCSharpName(classDecl, typeDatabase);

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
            var csharpType = ResolveCSharpType(arg.SwiftTypeSpec, typeDatabase);
            var pinvokeExpr = GetPInvokeArgExpression(paramName, arg.SwiftTypeSpec, paramCategory.Value, typeDatabase);
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
        var csharpReturnType = ResolveCSharpReturnType(returnTypeSpec, returnCategory.Value, typeDatabase);

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

        EmitMethodBody(csWriter, method, pInvokeName, parameters, returnCategory.Value, csharpReturnType, isStatic, typeDatabase);

        csWriter.Indent--;
        csWriter.WriteLine("}");

        return true;
    }

    private static void EmitMethodBody(
        CSharpWriter csWriter,
        MethodDecl method,
        string pInvokeName,
        List<(string name, string csharpType, string pinvokeExpr, TypeSpec typeSpec)> parameters,
        ReturnKind returnCategory,
        string csharpReturnType,
        bool isStatic,
        ITypeDatabase typeDatabase)
    {
        var nativeArgs = new List<string>();

        // Non-frozen struct returns use SwiftIndirectResult as first param
        if (returnCategory == ReturnKind.NonFrozenStruct)
            nativeArgs.Add("indirectResult");

        // Self parameter
        if (!isStatic)
            nativeArgs.Add("self.Payload.DangerousGetHandle()");

        // Method parameters
        foreach (var (name, _, pinvokeExpr, _) in parameters)
        {
            nativeArgs.Add(pinvokeExpr);
        }

        var entryPoint = NameProvider.GetMangledName(method);
        // Use the method's real mangled name as the NativeMethods entry
        var nativeMethodName = GetNativeMethodName(method);
        var nativeCall = $"NativeMethods.{nativeMethodName}({string.Join(", ", nativeArgs)})";

        switch (returnCategory)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                break;

            case ReturnKind.Primitive:
                csWriter.WriteLine($"return {nativeCall};");
                break;

            case ReturnKind.ObjCClass:
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{csharpReturnType}>(result)!;");
                break;

            case ReturnKind.SwiftClass:
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        var result = {{nativeCall}};
                        var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                        try
                        {
                            *(IntPtr*)classPayload = result;
                            return ({{csharpReturnType}})SwiftMarshal.MarshalFromSwift<{{csharpReturnType}}>(new IntPtr(classPayload));
                        }
                        catch
                        {
                            NativeMemory.Free(classPayload);
                            throw;
                        }
                    }
                    """);
                break;

            case ReturnKind.NonFrozenStruct:
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        var metadata = SwiftObjectHelper<{{csharpReturnType}}>.GetTypeMetadata();
                        IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                        try
                        {
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            {{nativeCall}};
                            return SwiftMarshal.MarshalFromSwift<{{csharpReturnType}}>(buffer);
                        }
                        catch
                        {
                            NativeMemory.Free((void*)buffer);
                            throw;
                        }
                    }
                    """);
                break;
        }
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

        var csharpType = ResolveCSharpReturnType(property.SwiftTypeSpec, returnCategory.Value, typeDatabase);

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
            nativeArgs.Add("self.Payload.DangerousGetHandle()");
            var nativeCall = $"NativeMethods.{nativeMethodName}({string.Join(", ", nativeArgs)})";

            switch (returnCategory.Value)
            {
                case ReturnKind.Primitive:
                    csWriter.WriteLine($"return {nativeCall};");
                    break;
                case ReturnKind.ObjCClass:
                    csWriter.WriteLine($"var result = {nativeCall};");
                    csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{csharpType}>(result)!;");
                    break;
                case ReturnKind.SwiftClass:
                    csWriter.WriteLines($$"""
                        unsafe
                        {
                            var result = {{nativeCall}};
                            var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                            try
                            {
                                *(IntPtr*)classPayload = result;
                                return ({{csharpType}})SwiftMarshal.MarshalFromSwift<{{csharpType}}>(new IntPtr(classPayload));
                            }
                            catch
                            {
                                NativeMemory.Free(classPayload);
                                throw;
                            }
                        }
                        """);
                    break;
                case ReturnKind.NonFrozenStruct:
                    csWriter.WriteLines($$"""
                        unsafe
                        {
                            var metadata = SwiftObjectHelper<{{csharpType}}>.GetTypeMetadata();
                            IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                            try
                            {
                                var indirectResult = new SwiftIndirectResult((void*)buffer);
                                {{nativeCall}};
                                return SwiftMarshal.MarshalFromSwift<{{csharpType}}>(buffer);
                            }
                            catch
                            {
                                NativeMemory.Free((void*)buffer);
                                throw;
                            }
                        }
                        """);
                    break;
            }

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
            csWriter.WriteLine($"NativeMethods.{nativeMethodName}(value, self.Payload.DangerousGetHandle());");

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
            var pinvokeType = ResolvePInvokeType(arg.SwiftTypeSpec, paramCategory.Value, typeDatabase);
            if (arg.SwiftTypeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool")
                pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {paramName}");
            else
                pinvokeParams.Add($"{pinvokeType} {paramName}");
        }

        var (entryPoint, needsWrapperLib) = PInvokeEmitter.ComputeEntryPoint(method);
        var libPath = needsWrapperLib && typeDatabase.AsyncLibraryName != null
            ? typeDatabase.AsyncLibraryName
            : moduleLibPath;

        var pinvokeReturnType = ResolvePInvokeReturnType(returnTypeSpec, returnCategory.Value, typeDatabase, usesIndirectResult);
        bool returnIsBool = returnTypeSpec is NamedTypeSpec retNamed && retNamed.Name == "Swift.Bool";

        var nativeMethodName = GetNativeMethodName(method);

        csWriter.WriteLine($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof(CallConvSwift) }})]");
        csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{entryPoint}\")]");
        if (returnIsBool)
            csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
        csWriter.WriteLine($"internal static partial {pinvokeReturnType} {nativeMethodName}({string.Join(", ", pinvokeParams)});");
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

            var pinvokeReturnType = ResolvePInvokeReturnType(property.SwiftTypeSpec, returnCategory.Value, typeDatabase, usesIndirectResult);
            bool returnIsBool = property.SwiftTypeSpec is NamedTypeSpec retNamed && retNamed.Name == "Swift.Bool";

            var nativeMethodName = GetNativeMethodName(method);

            csWriter.WriteLine($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof(CallConvSwift) }})]");
            csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{entryPoint}\")]");
            if (returnIsBool)
                csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
            csWriter.WriteLine($"internal static partial {pinvokeReturnType} {nativeMethodName}({string.Join(", ", pinvokeParams)});");
            csWriter.WriteLine();
        }

        // Setter P/Invoke (primitives only)
        var setterAccessor = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setterAccessor != null && returnCategory.Value == ReturnKind.Primitive)
        {
            var method = setterAccessor.Method;
            var pinvokeParams = new List<string>();

            var pinvokeType = ResolvePInvokeType(property.SwiftTypeSpec, ParamKind.Primitive, typeDatabase);
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

            csWriter.WriteLine($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof(CallConvSwift) }})]");
            csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{entryPoint}\")]");
            csWriter.WriteLine($"internal static partial void {nativeMethodName}({string.Join(", ", pinvokeParams)});");
            csWriter.WriteLine();
        }
    }

    // ==================== Type Classification ====================

    private enum ReturnKind
    {
        Void,
        Primitive,
        ObjCClass,
        SwiftClass,
        NonFrozenStruct,
    }

    private enum ParamKind
    {
        Primitive,
        ObjCClass,
        SwiftClass,
        SimpleEnum,
    }

    private static ReturnKind? ClassifyReturnType(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null)
            return ReturnKind.Void;

        if (typeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple)
            return ReturnKind.Void;

        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        // Skip generics, closures, existentials
        if (namedType.ContainsGenericParameters)
            return null;

        if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            return ReturnKind.Primitive;

        if (ForeignTypeExtensionEmitter.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            return ReturnKind.Primitive;

        try
        {
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return ReturnKind.ObjCClass;

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return ReturnKind.SwiftClass;
                if (typeRecord.Kind == TypeRecordKind.Struct)
                {
                    bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                    bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                    if (isFrozen && !hasRefFields)
                        return null; // Frozen value struct not supported yet
                    return ReturnKind.NonFrozenStruct;
                }
                if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return ReturnKind.Primitive;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static ParamKind? ClassifyParameterType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (namedType.ContainsGenericParameters)
            return null;

        if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            return ParamKind.Primitive;

        if (ForeignTypeExtensionEmitter.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            return ParamKind.Primitive;

        try
        {
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return ParamKind.ObjCClass;

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return ParamKind.SwiftClass;
                if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return ParamKind.SimpleEnum;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    // ==================== Type Resolution ====================

    private static string ResolveOriginalTypeCSharpName(ClassDecl classDecl, ITypeDatabase typeDatabase)
    {
        // Try TypeDatabase lookup first
        if (typeDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var typeRecord))
            return typeRecord.CSharpTypeName.FullyQualifiedName;

        // Fallback: Swift.{Module}.{TypeName}
        return $"Swift.{classDecl.SwiftTypeName.Module}.{classDecl.Name}";
    }

    private static string ResolveCSharpType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return "void";

        if (ForeignTypeExtensionEmitter.TypeAliasToCSPrimitive.TryGetValue(namedType.Name, out var aliased))
            return aliased;

        if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
        {
            return namedType.Name switch
            {
                "Swift.Int" => "nint",
                "Swift.UInt" => "nuint",
                "Swift.Int8" => "sbyte",
                "Swift.Int16" => "short",
                "Swift.Int32" => "int",
                "Swift.Int64" => "long",
                "Swift.UInt8" => "byte",
                "Swift.UInt16" => "ushort",
                "Swift.UInt32" => "uint",
                "Swift.UInt64" => "ulong",
                "Swift.Float" => "float",
                "Swift.Double" => "double",
                "Swift.Bool" => "bool",
                "CoreFoundation.CGFloat" => "nfloat",
                "CoreFoundation.CGSize" => "CoreGraphics.CGSize",
                "CoreFoundation.CGPoint" => "CoreGraphics.CGPoint",
                "CoreFoundation.CGRect" => "CoreGraphics.CGRect",
                _ => namedType.Name,
            };
        }

        if (typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            return typeRecord.CSharpTypeName.FullyQualifiedName;

        return namedType.Name;
    }

    private static string ResolveCSharpReturnType(TypeSpec? typeSpec, ReturnKind category, ITypeDatabase typeDatabase)
    {
        if (category == ReturnKind.Void || typeSpec == null)
            return "void";
        return ResolveCSharpType(typeSpec, typeDatabase);
    }

    private static string ResolvePInvokeType(TypeSpec typeSpec, ParamKind category, ITypeDatabase typeDatabase)
    {
        return category switch
        {
            ParamKind.Primitive => ResolveCSharpType(typeSpec, typeDatabase),
            ParamKind.ObjCClass => "IntPtr",
            ParamKind.SwiftClass => "IntPtr",
            ParamKind.SimpleEnum => ResolveCSharpType(typeSpec, typeDatabase),
            _ => "IntPtr",
        };
    }

    private static string ResolvePInvokeReturnType(TypeSpec? typeSpec, ReturnKind category, ITypeDatabase typeDatabase, bool usesIndirectResult)
    {
        if (usesIndirectResult)
            return "void";

        return category switch
        {
            ReturnKind.Void => "void",
            ReturnKind.Primitive => typeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
                ? "bool"
                : ResolveCSharpType(typeSpec!, typeDatabase),
            ReturnKind.ObjCClass => "IntPtr",
            ReturnKind.SwiftClass => "IntPtr",
            ReturnKind.NonFrozenStruct => "void", // shouldn't reach here
            _ => "void",
        };
    }

    private static string GetPInvokeArgExpression(string paramName, TypeSpec typeSpec, ParamKind category, ITypeDatabase typeDatabase)
    {
        return category switch
        {
            ParamKind.Primitive => paramName,
            ParamKind.ObjCClass => $"{paramName}.Handle",
            ParamKind.SwiftClass => $"{paramName}.Payload.DangerousGetHandle()",
            ParamKind.SimpleEnum => paramName,
            _ => paramName,
        };
    }

    private static string GetNativeMethodName(MethodDecl method)
    {
        return NameProvider.GetPInvokeName(method);
    }

    /// <summary>
    /// TypeAliasToCSPrimitive exposed for reuse. Delegates to ForeignTypeExtensionEmitter.
    /// </summary>
    internal static readonly Dictionary<string, string> TypeAliasToCSPrimitive =
        ForeignTypeExtensionEmitter.TypeAliasToCSPrimitive;
}
