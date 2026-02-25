// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# static extension classes for Swift extensions on foreign types
/// (types not defined in the current module, e.g., UIKit.UIView).
///
/// Unlike ProtocolExtensionEmitter (which injects methods onto existing ClassDecl),
/// foreign types have no ClassDecl in the current module. This emitter generates:
/// 1. @_silgen_name Swift wrappers (same pattern as ProtocolExtensionEmitter)
/// 2. C# static extension classes with proper marshalling for each return type
/// </summary>
public static class ForeignTypeExtensionEmitter
{
    /// <summary>
    /// Accumulated Swift wrapper source lines for the current module.
    /// </summary>
    private static readonly List<string> _swiftWrapperLines = new();

    /// <summary>
    /// Tracks emitted Swift wrapper symbols to prevent duplicate emission.
    /// </summary>
    private static readonly HashSet<string> _emittedSymbols = new();

    /// <summary>
    /// Count of emitted extension members for logging.
    /// </summary>
    private static int _emittedCount;

    /// <summary>
    /// Collected extension class info grouped by foreign type qualified name.
    /// Key: foreign type qualified name (e.g., "UIKit.UIView")
    /// Value: list of C# member emission actions
    /// </summary>
    private static readonly Dictionary<string, ExtensionClassInfo> _extensionClasses = new();

    /// <summary>
    /// Foreign modules that need to be imported in the Swift wrapper file.
    /// </summary>
    private static readonly HashSet<string> _neededImports = new();

    /// <summary>
    /// Info for a single C# extension class.
    /// </summary>
    private class ExtensionClassInfo
    {
        public string ForeignTypeQualifiedName { get; init; } = "";
        public string ModuleName { get; init; } = "";
        public List<ExtensionMemberInfo> Members { get; } = new();
    }

    /// <summary>
    /// Info for a single extension member (method or property getter/setter).
    /// </summary>
    private class ExtensionMemberInfo
    {
        public required string SymbolName { get; init; }
        public required string CSharpMethodName { get; init; }
        public required ProtocolExtensionMethodDecl ExtMethod { get; init; }
        public required List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> Parameters { get; init; }
        public TypeSpec? ReturnTypeSpec { get; init; }
        public required string ReturnTypeName { get; init; }
        public required ReturnKind ReturnCategory { get; init; }
        public bool IsPropertyGetter { get; init; }
        public bool IsPropertySetter { get; init; }
    }

    /// <summary>
    /// Categorizes return types for correct C# marshalling.
    /// </summary>
    private enum ReturnKind
    {
        Void,
        Primitive,
        ObjCClass,
        SwiftClass,
        NonFrozenStruct,
    }

    /// <summary>
    /// Resets per-module state. Called from Program.cs before processing.
    /// </summary>
    public static void ResetForModule()
    {
        _swiftWrapperLines.Clear();
        _emittedSymbols.Clear();
        _emittedCount = 0;
        _extensionClasses.Clear();
        _neededImports.Clear();
    }

    /// <summary>
    /// Processes foreign type extensions: applies gates, generates Swift wrappers,
    /// and collects C# extension class info for later emission.
    /// </summary>
    public static void ProcessForeignTypeExtensions(
        ModuleDecl moduleDecl,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> foreignExtensions,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        if (foreignExtensions.Count == 0)
            return;

        foreach (var (foreignTypeQualifiedName, members) in foreignExtensions)
        {
            // Gate: foreign type must be an ObjC class (not a primitive, struct, or protocol)
            if (!IsForeignObjCClassType(foreignTypeQualifiedName))
            {
                logger.LogDebug("Skipping foreign extension on non-ObjC type: {Type}", foreignTypeQualifiedName);
                continue;
            }

            // Track the foreign module for Swift imports
            var dotIdx = foreignTypeQualifiedName.IndexOf('.');
            if (dotIdx > 0)
            {
                _neededImports.Add(foreignTypeQualifiedName.Substring(0, dotIdx));
            }

            foreach (var extMethod in members)
            {
                TryProcessMember(moduleDecl, foreignTypeQualifiedName, extMethod, typeDatabase, logger);
            }
        }

        if (_emittedCount > 0)
        {
            logger.LogInformation("Emitted {Count} foreign type extension members", _emittedCount);
        }
    }

    /// <summary>
    /// Emits accumulated Swift wrapper functions to the SwiftWriter.
    /// Called from ModuleHandler.Emit() after all types have been processed.
    /// </summary>
    public static void EmitSwiftWrappers(SwiftWriter swiftWriter)
    {
        if (_swiftWrapperLines.Count == 0)
            return;

        // Emit any additional imports needed for foreign type modules
        // These go before the wrappers but after the module's own imports.
        // The SwiftWriter is already past the import section, but that's OK —
        // Swift allows import statements anywhere at the top level.
        foreach (var import in _neededImports.OrderBy(s => s))
        {
            swiftWriter.WriteLine($"import {import}");
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLine("// --- Foreign type extension method wrappers ---");
        foreach (var line in _swiftWrapperLines)
        {
            swiftWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Emits C# static extension classes for all processed foreign types.
    /// Called from ModuleHandler.Emit() after types have been emitted.
    /// </summary>
    public static void EmitCSharpExtensionClasses(CSharpWriter csWriter, ITypeDatabase typeDatabase, string moduleName)
    {
        if (_extensionClasses.Count == 0)
            return;

        foreach (var (foreignTypeQualifiedName, classInfo) in _extensionClasses.OrderBy(kv => kv.Key))
        {
            EmitExtensionClass(csWriter, classInfo, typeDatabase, moduleName);
        }
    }

    /// <summary>
    /// Attempts to process a single foreign extension member. Applies gates, generates
    /// Swift wrapper, and collects C# member info.
    /// </summary>
    private static void TryProcessMember(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Gate: skip constrained extensions
        if (extMethod.WhereConstraints.Count > 0)
            return;

        // Gate: skip deprecated members
        if (extMethod.IsDeprecated)
            return;

        // Gate: skip static members (deferred)
        if (extMethod.IsStatic)
            return;

        // Gate: skip async methods
        if (!extMethod.IsProperty && ProtocolExtensionEmitter.IsAsyncSignature(extMethod.RawSignature))
            return;

        // Gate: skip throwing methods
        if (!extMethod.IsProperty && ProtocolExtensionEmitter.IsThrowingSignature(extMethod.RawSignature))
            return;

        if (extMethod.IsProperty)
        {
            TryProcessProperty(moduleDecl, foreignTypeQualifiedName, extMethod, typeDatabase, logger);
        }
        else
        {
            TryProcessMethod(moduleDecl, foreignTypeQualifiedName, extMethod, typeDatabase, logger);
        }
    }

    /// <summary>
    /// Processes a property getter (and optionally setter) from a foreign extension.
    /// </summary>
    private static void TryProcessProperty(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Parse property type from raw signature: "public var name: Type { get [set] }"
        var colonIdx = extMethod.RawSignature.IndexOf($"{extMethod.MethodName}:", StringComparison.Ordinal);
        if (colonIdx < 0)
            return;

        var afterColon = extMethod.RawSignature.Substring(colonIdx + extMethod.MethodName.Length + 1).Trim();
        // Remove trailing "{ get [set] }"
        var braceIdx = afterColon.IndexOf('{');
        if (braceIdx >= 0)
            afterColon = afterColon.Substring(0, braceIdx).Trim();

        // Strip attributes
        afterColon = StripSwiftAttributes(afterColon);

        if (string.IsNullOrWhiteSpace(afterColon))
            return;

        TypeSpec? propertyTypeSpec;
        try
        {
            propertyTypeSpec = TypeSpecParser.Parse(afterColon);
        }
        catch
        {
            logger.LogDebug("Skipping foreign extension property {Type}.{Name}: TypeSpecParser error for '{TypeStr}'",
                foreignTypeQualifiedName, extMethod.MethodName, afterColon);
            return;
        }
        if (propertyTypeSpec == null)
            return;

        // Determine return category
        var returnCategory = ClassifyReturnType(propertyTypeSpec, typeDatabase);
        if (returnCategory == null)
        {
            logger.LogDebug("Skipping foreign extension property {Type}.{Name}: unsupported return type '{TypeStr}'",
                foreignTypeQualifiedName, extMethod.MethodName, afterColon);
            return;
        }

        var flatTypeName = FlattenQualifiedName(foreignTypeQualifiedName);
        var getterSymbol = $"SBW_{flatTypeName}_get_{extMethod.MethodName}";

        if (!_emittedSymbols.Add(getterSymbol))
            return;

        // Emit Swift getter wrapper
        EmitSwiftPropertyGetter(foreignTypeQualifiedName, extMethod, propertyTypeSpec, getterSymbol, returnCategory.Value);

        // Collect C# getter info
        var csharpMethodName = $"Get{ToPascalCase(extMethod.MethodName)}";
        var classInfo = GetOrCreateClassInfo(foreignTypeQualifiedName, moduleDecl.Name);
        classInfo.Members.Add(new ExtensionMemberInfo
        {
            SymbolName = getterSymbol,
            CSharpMethodName = csharpMethodName,
            ExtMethod = extMethod,
            Parameters = new(),
            ReturnTypeSpec = propertyTypeSpec,
            ReturnTypeName = afterColon,
            ReturnCategory = returnCategory.Value,
            IsPropertyGetter = true,
        });
        _emittedCount++;

        // Emit setter if applicable (only for primitives)
        if (extMethod.HasSetter)
        {
            var setterReturnCategory = ClassifyParameterForSetter(propertyTypeSpec, typeDatabase);
            if (setterReturnCategory != null)
            {
                var setterSymbol = $"SBW_{flatTypeName}_set_{extMethod.MethodName}";
                if (_emittedSymbols.Add(setterSymbol))
                {
                    EmitSwiftPropertySetter(foreignTypeQualifiedName, extMethod, propertyTypeSpec, setterSymbol, afterColon);

                    classInfo.Members.Add(new ExtensionMemberInfo
                    {
                        SymbolName = setterSymbol,
                        CSharpMethodName = $"Set{ToPascalCase(extMethod.MethodName)}",
                        ExtMethod = extMethod,
                        Parameters = new() { ("value", propertyTypeSpec, afterColon, false) },
                        ReturnTypeSpec = null,
                        ReturnTypeName = "void",
                        ReturnCategory = ReturnKind.Void,
                        IsPropertySetter = true,
                    });
                    _emittedCount++;
                }
            }
        }
    }

    /// <summary>
    /// Processes a method from a foreign extension.
    /// </summary>
    private static void TryProcessMethod(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Gate: skip generic methods
        if (extMethod.RawSignature.Contains($"func {extMethod.MethodName}<"))
            return;

        // Parse signature
        var parseResult = ParseMethodSignature(extMethod, typeDatabase, logger);
        if (parseResult == null)
            return;

        var (allParameters, returnTypeSpec, returnTypeName) = parseResult.Value;

        // Classify return type
        ReturnKind returnCategory;
        if (returnTypeSpec == null || (returnTypeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple))
        {
            returnCategory = ReturnKind.Void;
        }
        else
        {
            var classified = ClassifyReturnType(returnTypeSpec, typeDatabase);
            if (classified == null)
            {
                logger.LogDebug("Skipping foreign extension method {Type}.{Method}: unsupported return type",
                    foreignTypeQualifiedName, extMethod.MethodName);
                return;
            }
            returnCategory = classified.Value;
        }

        // Apply default parameter reduction: emit with only compatible params
        var compatibleParams = new List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)>();
        bool hasIncompatibleNonDefault = false;

        foreach (var (label, typeSpec, swiftType, hasDefault) in allParameters)
        {
            if (IsCdeclCompatibleType(typeSpec, typeDatabase))
            {
                compatibleParams.Add((label, typeSpec, swiftType, hasDefault));
            }
            else if (!hasDefault)
            {
                // Non-default incompatible param — can't emit this method at all
                hasIncompatibleNonDefault = true;
                break;
            }
            // else: incompatible with default — omit (Swift fills default)
        }

        if (hasIncompatibleNonDefault)
        {
            logger.LogDebug("Skipping foreign extension method {Type}.{Method}: incompatible non-default parameter",
                foreignTypeQualifiedName, extMethod.MethodName);
            return;
        }

        var flatTypeName = FlattenQualifiedName(foreignTypeQualifiedName);
        var symbolName = BuildSymbolName(flatTypeName, extMethod.MethodName, compatibleParams);

        if (!_emittedSymbols.Add(symbolName))
            return;

        // Emit Swift wrapper
        EmitSwiftMethodWrapper(foreignTypeQualifiedName, extMethod, allParameters, compatibleParams,
            returnTypeSpec, symbolName, returnCategory);

        // Collect C# info
        var classInfo = GetOrCreateClassInfo(foreignTypeQualifiedName, moduleDecl.Name);
        classInfo.Members.Add(new ExtensionMemberInfo
        {
            SymbolName = symbolName,
            CSharpMethodName = ToPascalCase(extMethod.MethodName),
            ExtMethod = extMethod,
            Parameters = compatibleParams,
            ReturnTypeSpec = returnTypeSpec,
            ReturnTypeName = returnTypeName,
            ReturnCategory = returnCategory,
        });
        _emittedCount++;
    }

    // ==================== Swift Wrapper Emission ====================

    /// <summary>
    /// Emits a Swift property getter wrapper.
    /// </summary>
    private static void EmitSwiftPropertyGetter(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        TypeSpec propertyTypeSpec,
        string symbolName,
        ReturnKind returnCategory)
    {
        string swiftReturnType;
        bool wrapAsOpaque;

        switch (returnCategory)
        {
            case ReturnKind.Void:
                swiftReturnType = "";
                wrapAsOpaque = false;
                break;
            case ReturnKind.Primitive:
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);
                wrapAsOpaque = false;
                break;
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftReturnType = "UnsafeMutableRawPointer";
                wrapAsOpaque = true;
                break;
            case ReturnKind.NonFrozenStruct:
                // Return by value — CallConvSwift handles indirect return automatically
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);
                wrapAsOpaque = false;
                break;
            default:
                return;
        }

        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        _swiftWrapperLines.Add("");
        _swiftWrapperLines.Add($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            _swiftWrapperLines.Add("@MainActor");
        }
        _swiftWrapperLines.Add($"public func {symbolName}(_ self_: UnsafeMutableRawPointer){returnArrow} {{");
        _swiftWrapperLines.Add($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");

        if (wrapAsOpaque)
        {
            _swiftWrapperLines.Add($"    let result = instance.{extMethod.MethodName}");
            _swiftWrapperLines.Add($"    return Unmanaged.passUnretained(result).toOpaque()");
        }
        else if (returnCategory == ReturnKind.NonFrozenStruct)
        {
            _swiftWrapperLines.Add($"    return instance.{extMethod.MethodName}");
        }
        else if (returnCategory == ReturnKind.Primitive)
        {
            _swiftWrapperLines.Add($"    return instance.{extMethod.MethodName}");
        }
        else
        {
            _swiftWrapperLines.Add($"    instance.{extMethod.MethodName}");
        }
        _swiftWrapperLines.Add("}");
    }

    /// <summary>
    /// Emits a Swift property setter wrapper (primitives only).
    /// </summary>
    private static void EmitSwiftPropertySetter(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        TypeSpec propertyTypeSpec,
        string symbolName,
        string swiftTypeName)
    {
        var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);

        _swiftWrapperLines.Add("");
        _swiftWrapperLines.Add($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            _swiftWrapperLines.Add("@MainActor");
        }
        _swiftWrapperLines.Add($"public func {symbolName}(_ self_: UnsafeMutableRawPointer, _ value: {renderedType}) {{");
        _swiftWrapperLines.Add($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
        _swiftWrapperLines.Add($"    instance.{extMethod.MethodName} = value");
        _swiftWrapperLines.Add("}");
    }

    /// <summary>
    /// Emits a Swift method wrapper. Passes only compatible parameters;
    /// Swift fills defaults for omitted ones.
    /// </summary>
    private static void EmitSwiftMethodWrapper(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> allParameters,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> compatibleParams,
        TypeSpec? returnTypeSpec,
        string symbolName,
        ReturnKind returnCategory)
    {
        // Build Swift parameter list for wrapper
        var swiftParams = new List<string>();
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        foreach (var (label, typeSpec, swiftType, _) in compatibleParams)
        {
            var paramName = SanitizeSwiftParamName(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            {
                swiftParams.Add($"_ {paramName}: UnsafeMutableRawPointer");
            }
            else
            {
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                swiftParams.Add($"_ {paramName}: {renderedType}");
            }
        }

        // Build return type
        string swiftReturnType;
        bool returnIsClass;
        switch (returnCategory)
        {
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
                break;
            case ReturnKind.Primitive:
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
                break;
            case ReturnKind.NonFrozenStruct:
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
                break;
            default:
                swiftReturnType = "";
                returnIsClass = false;
                break;
        }

        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        _swiftWrapperLines.Add("");
        _swiftWrapperLines.Add($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            _swiftWrapperLines.Add("@MainActor");
        }
        _swiftWrapperLines.Add($"public func {symbolName}({string.Join(", ", swiftParams)}){returnArrow} {{");
        _swiftWrapperLines.Add($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");

        // Build call arguments — map compatible params into call, skip incompatible (use Swift defaults)
        var compatibleSet = new HashSet<int>();
        int compatIdx = 0;
        for (int i = 0; i < allParameters.Count; i++)
        {
            if (compatIdx < compatibleParams.Count &&
                allParameters[i].label == compatibleParams[compatIdx].label &&
                allParameters[i].swiftType == compatibleParams[compatIdx].swiftType)
            {
                compatibleSet.Add(i);
                compatIdx++;
            }
        }

        var callArgs = new List<string>();
        compatIdx = 0;
        for (int i = 0; i < allParameters.Count; i++)
        {
            if (!compatibleSet.Contains(i))
                continue; // Omitted — Swift fills default

            var (label, typeSpec, swiftType, _) = allParameters[i];
            var paramName = SanitizeSwiftParamName(label == "_" ? GetParamNameFromType(swiftType) : label);

            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            {
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                var localName = $"__{paramName}";
                _swiftWrapperLines.Add($"    let {localName} = Unmanaged<{renderedType}>.fromOpaque({paramName}).takeUnretainedValue()");
                callArgs.Add(label == "_" ? localName : $"{label}: {localName}");
            }
            else
            {
                callArgs.Add(label == "_" ? paramName : $"{label}: {paramName}");
            }
        }

        var callStr = $"instance.{extMethod.MethodName}({string.Join(", ", callArgs)})";

        if (returnIsClass)
        {
            _swiftWrapperLines.Add($"    let result = {callStr}");
            _swiftWrapperLines.Add($"    return Unmanaged.passUnretained(result).toOpaque()");
        }
        else if (string.IsNullOrEmpty(swiftReturnType))
        {
            _swiftWrapperLines.Add($"    {callStr}");
        }
        else
        {
            _swiftWrapperLines.Add($"    return {callStr}");
        }

        _swiftWrapperLines.Add("}");
    }

    // ==================== C# Extension Class Emission ====================

    /// <summary>
    /// Emits a single C# static extension class for a foreign type.
    /// </summary>
    private static void EmitExtensionClass(CSharpWriter csWriter, ExtensionClassInfo classInfo,
        ITypeDatabase typeDatabase, string moduleName)
    {
        var foreignTypeName = classInfo.ForeignTypeQualifiedName;
        var dotIdx = foreignTypeName.LastIndexOf('.');
        var unqualifiedTypeName = dotIdx >= 0 ? foreignTypeName.Substring(dotIdx + 1) : foreignTypeName;
        var className = $"{unqualifiedTypeName}{moduleName}Extensions";

        // Resolve the C# namespace-qualified type name for the foreign type
        var csharpSelfType = ResolveForeignTypeCSharpName(foreignTypeName, typeDatabase);

        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {foreignTypeName} defined in {moduleName}.");
        csWriter.WriteLine($"/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Emit each member
        foreach (var member in classInfo.Members)
        {
            EmitExtensionMember(csWriter, member, csharpSelfType, wrapperLibPath, typeDatabase, moduleName);
        }

        // Emit NativeMethods nested class
        csWriter.WriteLine($"private static partial class NativeMethods");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var member in classInfo.Members)
        {
            EmitNativeMethod(csWriter, member, wrapperLibPath, typeDatabase);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a single public extension method in the extension class.
    /// </summary>
    private static void EmitExtensionMember(CSharpWriter csWriter, ExtensionMemberInfo member,
        string csharpSelfType, string wrapperLibPath, ITypeDatabase typeDatabase, string moduleName)
    {
        var csharpReturnType = ResolveCSharpReturnType(member, typeDatabase, moduleName);

        // Build parameter list
        var paramList = new List<string>();
        paramList.Add($"this {csharpSelfType} self");
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramTypeName = ResolveCSharpParameterType(typeSpec, typeDatabase);
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";
            paramList.Add($"{paramTypeName} {paramName}");
        }

        // For setter, return type is void
        var methodReturnType = member.IsPropertySetter ? "void" : csharpReturnType;

        csWriter.WriteLine();
        csWriter.WriteLine($"public static {methodReturnType} {member.CSharpMethodName}({string.Join(", ", paramList)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        EmitMethodBody(csWriter, member, typeDatabase, moduleName);

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the method body with proper marshalling based on return type category.
    /// </summary>
    private static void EmitMethodBody(CSharpWriter csWriter, ExtensionMemberInfo member,
        ITypeDatabase typeDatabase, string moduleName)
    {
        // Build native call arguments
        var nativeArgs = new List<string>();

        // For non-frozen struct returns, SwiftIndirectResult is the first parameter
        if (member.ReturnCategory == ReturnKind.NonFrozenStruct)
        {
            nativeArgs.Add("indirectResult");
        }

        nativeArgs.Add("self.Handle");

        // Add method parameters
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";

            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name) &&
                !TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            {
                // Distinguish ObjC classes (.Handle) from same-module Swift classes (.Payload.DangerousGetHandle())
                if (IsSameModuleSwiftClass(namedType, typeDatabase))
                    nativeArgs.Add($"{paramName}.Payload.DangerousGetHandle()");
                else
                    nativeArgs.Add($"{paramName}.Handle");
            }
            else
            {
                nativeArgs.Add(paramName);
            }
        }

        var nativeCall = $"NativeMethods.{member.SymbolName}({string.Join(", ", nativeArgs)})";

        switch (member.ReturnCategory)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                break;

            case ReturnKind.Primitive:
                csWriter.WriteLine($"return {nativeCall};");
                break;

            case ReturnKind.ObjCClass:
            {
                var csharpType = ResolveCSharpReturnType(member, typeDatabase, moduleName);
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{csharpType}>(result)!;");
                break;
            }

            case ReturnKind.SwiftClass:
            {
                var csharpType = ResolveCSharpReturnType(member, typeDatabase, moduleName);
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
            }

            case ReturnKind.NonFrozenStruct:
            {
                var csharpType = ResolveCSharpReturnType(member, typeDatabase, moduleName);
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
        }
    }

    /// <summary>
    /// Emits a P/Invoke declaration in the NativeMethods nested class.
    /// </summary>
    private static void EmitNativeMethod(CSharpWriter csWriter, ExtensionMemberInfo member,
        string wrapperLibPath, ITypeDatabase typeDatabase)
    {
        var pinvokeParams = new List<string>();

        // Non-frozen struct returns use SwiftIndirectResult as first param
        bool usesIndirectResult = member.ReturnCategory == ReturnKind.NonFrozenStruct;
        string pinvokeReturnType;
        bool returnIsBool = false;

        if (usesIndirectResult)
        {
            pinvokeParams.Add("SwiftIndirectResult result");
            pinvokeReturnType = "void";
        }
        else
        {
            pinvokeReturnType = member.ReturnCategory switch
            {
                ReturnKind.Void => "void",
                ReturnKind.Primitive => ResolvePInvokeReturnType(member.ReturnTypeSpec!, typeDatabase),
                ReturnKind.ObjCClass => "IntPtr",
                ReturnKind.SwiftClass => "IntPtr",
                _ => "void",
            };

            // Bool needs [MarshalAs(UnmanagedType.U1)] and uses bool type (not byte)
            if (member.ReturnTypeSpec is NamedTypeSpec retNamed && retNamed.Name == "Swift.Bool")
            {
                pinvokeReturnType = "bool";
                returnIsBool = true;
            }
        }

        // Self parameter
        pinvokeParams.Add("IntPtr self_");

        // Method parameters
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";

            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name) &&
                !TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            {
                pinvokeParams.Add($"IntPtr {paramName}");
            }
            else
            {
                var pinvokeType = ResolvePInvokeParamType(typeSpec, typeDatabase);
                // Bool parameters need [MarshalAs(UnmanagedType.U1)]
                if (typeSpec is NamedTypeSpec paramNamed && paramNamed.Name == "Swift.Bool")
                    pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {paramName}");
                else
                    pinvokeParams.Add($"{pinvokeType} {paramName}");
            }
        }

        csWriter.WriteLine($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof(CallConvSwift) }})]");
        csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{member.SymbolName}\")]");
        if (returnIsBool)
            csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
        csWriter.WriteLine($"internal static partial {pinvokeReturnType} {member.SymbolName}({string.Join(", ", pinvokeParams)});");
        csWriter.WriteLine();
    }

    // ==================== Type Resolution Helpers ====================

    /// <summary>
    /// Checks if a foreign type qualified name represents an ObjC class type.
    /// Only ObjC classes (UIView, UILabel, etc.) are supported — not primitives,
    /// structs, or other Swift value types.
    ///
    /// Unqualified names (no module prefix) are rejected because IsObjCModuleType
    /// requires a module to check. SPM-built .swiftinterface files always use
    /// fully-qualified names for foreign types (e.g., "UIKit.UIView"), so this
    /// limitation only affects the safety-net unqualified parser path.
    /// </summary>
    private static bool IsForeignObjCClassType(string foreignTypeQualifiedName)
    {
        // Use TypeDatabaseExtensions.IsObjCModuleType via a temporary NamedTypeSpec.
        // This handles both qualified ("UIKit.UIView") and rejects unqualified names
        // (NamedTypeSpec.HasModule() returns false → IsObjCModuleType returns false).
        try
        {
            var namedType = new NamedTypeSpec(foreignTypeQualifiedName);
            return TypeDatabaseExtensions.IsObjCModuleType(namedType);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Classifies a return TypeSpec into a ReturnKind for marshalling.
    /// Returns null if the type is not supported.
    /// </summary>
    private static ReturnKind? ClassifyReturnType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple)
            return ReturnKind.Void;

        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (namedType.ContainsGenericParameters)
            return null;

        if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            return ReturnKind.Primitive;

        if (TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            return ReturnKind.Primitive;

        // Check if it's an ObjC framework type
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return ReturnKind.ObjCClass;

            // Check TypeDatabase for same-module types
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return ReturnKind.SwiftClass;
                if (typeRecord.Kind == TypeRecordKind.Struct)
                {
                    // Frozen structs without ref-type fields are C# value types (Struct marshalling label).
                    // Only non-frozen or memory-managed structs use SwiftIndirectResult + MarshalFromSwift.
                    bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                    bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                    if (isFrozen && !hasRefFields)
                    {
                        // Frozen value-type struct — not currently supported as a return type
                        // from foreign extension methods (would need blittable struct marshalling).
                        return null;
                    }
                    return ReturnKind.NonFrozenStruct;
                }
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Classifies a property type for setter emission. Only primitives are supported.
    /// </summary>
    private static ReturnKind? ClassifyParameterForSetter(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;
        if (namedType.ContainsGenericParameters)
            return null;
        if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
            return ReturnKind.Primitive;
        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec is cdecl-compatible (same logic as ProtocolExtensionEmitter).
    /// </summary>
    private static bool IsCdeclCompatibleType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            if (namedType.ContainsGenericParameters)
            {
                // Optional parameters have no marshalling path in ForeignTypeExtensionEmitter —
                // reject them here so they are either omitted (if they have defaults) or cause
                // the whole method to be skipped.
                return false;
            }

            if (ProtocolExtensionEmitter.IsSwiftPrimitive(namedType.Name))
                return true;

            // Handle known type aliases that resolve to primitives
            if (TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
                return true;

            try
            {
                if (typeDatabase.TryGetTypeRecord(
                        SwiftTypeName.FromModuleQualifiedName(namedType.Name), out var typeRecord))
                {
                    return typeRecord.Kind == TypeRecordKind.Class;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            // Also accept ObjC class types (not in TypeDatabase but known as classes)
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return true;

            return false;
        }

        if (typeSpec is ClosureTypeSpec) return false;
        if (typeSpec is TupleTypeSpec t) return t.IsEmptyTuple;
        if (typeSpec is ProtocolListTypeSpec) return false;
        return false;
    }

    /// <summary>
    /// Checks if a NamedTypeSpec represents a same-module Swift class (not an ObjC class).
    /// Same-module Swift classes expose .Payload (SafeHandle) instead of .Handle (IntPtr).
    /// ObjC-bridged classes (UIColor, UIImage, etc.) use .Handle even if they appear in TypeDatabase.
    /// </summary>
    private static bool IsSameModuleSwiftClass(NamedTypeSpec namedType, ITypeDatabase typeDatabase)
    {
        // ObjC types always use .Handle — check this first to avoid false positives
        // from TypeDatabase entries for ObjC-bridged types
        if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.Kind == TypeRecordKind.Class
                    && !typeRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridged);
        }
        catch (ArgumentException)
        {
            // Not a module-qualified name
        }
        return false;
    }

    /// <summary>
    /// Resolves a foreign type's C# name using TypeDatabase or ObjC module conventions.
    /// Uses the NamedTypeSpec overload of TryGetTypeRecord which auto-creates ObjC bridged
    /// type records (handling class remappings like Foundation.HTTPURLResponse → NSHttpUrlResponse).
    /// </summary>
    private static string ResolveForeignTypeCSharpName(string foreignTypeQualifiedName, ITypeDatabase typeDatabase)
    {
        // Use NamedTypeSpec-based lookup which goes through CreateObjCBridgedTypeRecord
        // for ObjC types, handling Apple framework class remappings correctly
        try
        {
            var namedType = new NamedTypeSpec(foreignTypeQualifiedName);
            if (typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
        }
        catch (ArgumentException)
        {
            // Fall through
        }

        // Fallback: manual module.typeName construction
        var dotIdx = foreignTypeQualifiedName.IndexOf('.');
        if (dotIdx >= 0)
        {
            var module = foreignTypeQualifiedName.Substring(0, dotIdx);
            var typeName = foreignTypeQualifiedName.Substring(dotIdx + 1);
            if (ModuleToCSharpNamespace.TryGetValue(module, out var csharpNamespace))
                return $"{csharpNamespace}.{typeName}";
            return $"{module}.{typeName}";
        }

        return foreignTypeQualifiedName;
    }

    /// <summary>
    /// Resolves the C# return type for an extension member.
    /// </summary>
    private static string ResolveCSharpReturnType(ExtensionMemberInfo member, ITypeDatabase typeDatabase, string moduleName)
    {
        if (member.ReturnCategory == ReturnKind.Void)
            return "void";

        if (member.ReturnTypeSpec == null)
            return "void";

        return ResolveCSharpTypeName(member.ReturnTypeSpec, typeDatabase);
    }

    /// <summary>
    /// Resolves a C# type name for a parameter TypeSpec.
    /// </summary>
    private static string ResolveCSharpParameterType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        return ResolveCSharpTypeName(typeSpec, typeDatabase);
    }

    /// <summary>
    /// Resolves a TypeSpec to a C# type name.
    /// </summary>
    /// <summary>
    /// Swift module → C# namespace overrides for ObjC framework types.
    /// </summary>
    private static readonly Dictionary<string, string> ModuleToCSharpNamespace = new(StringComparer.Ordinal)
    {
        { "QuartzCore", "CoreAnimation" },
    };

    /// <summary>
    /// Swift type aliases that resolve to primitives.
    /// </summary>
    private static readonly Dictionary<string, string> TypeAliasToCSPrimitive = new(StringComparer.Ordinal)
    {
        { "Foundation.TimeInterval", "double" },
    };

    private static string ResolveCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Check type aliases first
            if (TypeAliasToCSPrimitive.TryGetValue(namedType.Name, out var aliasedType))
                return aliasedType;

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

            // Use NamedTypeSpec-based lookup which goes through CreateObjCBridgedTypeRecord
            // for ObjC types, handling Apple framework class remappings correctly
            if (typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return namedType.Name;
        }

        return "void";
    }

    /// <summary>
    /// Resolves a primitive TypeSpec to P/Invoke type.
    /// </summary>
    private static string ResolvePInvokeReturnType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Check type aliases first
            if (namedType.Name == "Foundation.TimeInterval")
                return "double";

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
                "Swift.Bool" => "byte", // Bool is marshalled as byte in P/Invoke
                "CoreFoundation.CGFloat" => "nfloat",
                "CoreFoundation.CGSize" => "CoreGraphics.CGSize",
                "CoreFoundation.CGPoint" => "CoreGraphics.CGPoint",
                "CoreFoundation.CGRect" => "CoreGraphics.CGRect",
                _ => "IntPtr",
            };
        }
        return "IntPtr";
    }

    /// <summary>
    /// Resolves a TypeSpec to P/Invoke parameter type.
    /// </summary>
    private static string ResolvePInvokeParamType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        return ResolvePInvokeReturnType(typeSpec, typeDatabase);
    }

    // ==================== Parsing Helpers ====================

    /// <summary>
    /// Parses a method signature into structured parameter and return type info.
    /// Includes default value detection for each parameter.
    /// </summary>
    private static (List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> parameters,
                     TypeSpec? returnTypeSpec, string returnTypeName)?
        ParseMethodSignature(
            ProtocolExtensionMethodDecl extMethod,
            ITypeDatabase typeDatabase,
            ILogger logger)
    {
        var line = extMethod.RawSignature;

        var funcIdx = line.IndexOf($"func {extMethod.MethodName}", StringComparison.Ordinal);
        if (funcIdx < 0)
            return null;

        var parenStart = line.IndexOf('(', funcIdx);
        if (parenStart < 0)
            return null;

        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);

        var parameters = new List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)>();
        if (!string.IsNullOrWhiteSpace(paramStr))
        {
            var parts = SplitParameters(paramStr);
            foreach (var part in parts)
            {
                var parsed = ParseParameter(part.Trim());
                if (parsed == null)
                {
                    logger.LogDebug("Skipping foreign extension method {Method}: could not parse parameter '{Param}'",
                        extMethod.MethodName, part.Trim());
                    return null;
                }
                parameters.Add(parsed.Value);
            }
        }

        // Parse return type
        TypeSpec? returnTypeSpec = null;
        string returnTypeName = "void";

        var afterParen = line.Substring(parenEnd + 1).Trim();
        var braceIdx = afterParen.IndexOf('{');
        if (braceIdx >= 0)
            afterParen = afterParen.Substring(0, braceIdx).Trim();

        var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx >= 0)
        {
            var returnTypeStr = afterParen.Substring(arrowIdx + 2).Trim();
            try
            {
                returnTypeSpec = TypeSpecParser.Parse(returnTypeStr);
            }
            catch
            {
                logger.LogDebug("Skipping foreign extension method {Method}: TypeSpecParser error for return type '{Type}'",
                    extMethod.MethodName, returnTypeStr);
                return null;
            }
            if (returnTypeSpec == null)
            {
                logger.LogDebug("Skipping foreign extension method {Method}: could not parse return type '{Type}'",
                    extMethod.MethodName, returnTypeStr);
                return null;
            }
            returnTypeName = returnTypeStr;
        }

        return (parameters, returnTypeSpec, returnTypeName);
    }

    /// <summary>
    /// Parses a single parameter, including default value detection.
    /// </summary>
    private static (string label, TypeSpec typeSpec, string swiftType, bool hasDefault)?
        ParseParameter(string paramDecl)
    {
        var colonIdx = paramDecl.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var beforeColon = paramDecl.Substring(0, colonIdx).Trim();
        var afterColon = paramDecl.Substring(colonIdx + 1).Trim();

        afterColon = StripSwiftAttributes(afterColon);

        // Detect and remove default value
        bool hasDefault = false;
        var defaultIdx = FindDefaultValueStart(afterColon);
        if (defaultIdx >= 0)
        {
            hasDefault = true;
            afterColon = afterColon.Substring(0, defaultIdx).Trim();
        }

        if (string.IsNullOrWhiteSpace(afterColon))
            return null;

        var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var label = words.Length > 0 ? words[0] : "_";

        TypeSpec? typeSpec;
        try
        {
            typeSpec = TypeSpecParser.Parse(afterColon);
        }
        catch
        {
            return null;
        }
        if (typeSpec == null)
            return null;

        return (label, typeSpec, afterColon, hasDefault);
    }

    // ==================== String Helpers ====================

    /// <summary>
    /// Strips Swift parameter attributes.
    /// </summary>
    private static string StripSwiftAttributes(string typeStr)
    {
        while (typeStr.StartsWith("@"))
        {
            var spaceIdx = typeStr.IndexOf(' ');
            if (spaceIdx < 0) break;
            typeStr = typeStr.Substring(spaceIdx + 1).TrimStart();
        }
        if (typeStr.StartsWith("inout "))
            typeStr = typeStr.Substring(6).TrimStart();
        return typeStr;
    }

    /// <summary>
    /// Finds the start of a default value in a parameter type string.
    /// </summary>
    private static int FindDefaultValueStart(string typeStr)
    {
        int depth = 0;
        for (int i = 0; i < typeStr.Length; i++)
        {
            char c = typeStr[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == '=' && depth == 0 && i > 0 && typeStr[i - 1] == ' ')
                return i - 1;
        }
        return -1;
    }

    /// <summary>
    /// Splits parameters respecting nested brackets.
    /// </summary>
    private static List<string> SplitParameters(string paramStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < paramStr.Length; i++)
        {
            char c = paramStr[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == ',' && depth == 0)
            {
                result.Add(paramStr.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(paramStr.Substring(start));
        return result;
    }

    private static string FlattenQualifiedName(string qualifiedName)
    {
        return qualifiedName.Replace(".", "_");
    }

    private static string BuildSymbolName(string flatTypeName, string methodName,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> parameters)
    {
        var baseName = $"SBW_{flatTypeName}_{methodName}";
        if (parameters.Count > 0)
        {
            var labels = string.Join("_", parameters.Select(p =>
            {
                var label = p.label == "_" ? "" : p.label;
                var typeSpec = p.typeSpec;
                var typeSuffix = typeSpec is NamedTypeSpec named
                    ? named.Name.Substring(named.Name.LastIndexOf('.') + 1)
                    : "";
                return string.IsNullOrEmpty(label) ? typeSuffix : $"{label}{typeSuffix}";
            }));
            baseName += $"_{labels}";
        }
        return baseName;
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string GetParamNameFromType(string swiftType)
    {
        var dotIdx = swiftType.LastIndexOf('.');
        var typeName = dotIdx >= 0 ? swiftType.Substring(dotIdx + 1) : swiftType;

        if (typeName == "Bool") return "enabled";
        if (typeName is "Int" or "Int32" or "Int64") return "value";
        if (typeName is "Float" or "Double" or "CGFloat") return "value";
        if (typeName == "String") return "str";

        if (typeName.Length > 0)
            return char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);

        return "arg";
    }

    private static string SanitizeSwiftParamName(string name)
    {
        return name switch
        {
            "self" => "self_",
            "class" => "class_",
            "struct" => "struct_",
            "enum" => "enum_",
            "protocol" => "protocol_",
            "func" => "func_",
            "return" => "return_",
            "import" => "import_",
            "let" => "let_",
            "var" => "var_",
            "in" => "in_",
            "for" => "for_",
            "if" => "if_",
            "else" => "else_",
            "while" => "while_",
            "switch" => "switch_",
            "case" => "case_",
            "default" => "default_",
            "where" => "where_",
            "guard" => "guard_",
            "throw" => "throw_",
            "try" => "try_",
            "catch" => "catch_",
            "as" => "as_",
            "is" => "is_",
            "true" => "true_",
            "false" => "false_",
            "nil" => "nil_",
            _ => name,
        };
    }

    private static ExtensionClassInfo GetOrCreateClassInfo(string foreignTypeQualifiedName, string moduleName)
    {
        if (!_extensionClasses.TryGetValue(foreignTypeQualifiedName, out var info))
        {
            info = new ExtensionClassInfo
            {
                ForeignTypeQualifiedName = foreignTypeQualifiedName,
                ModuleName = moduleName,
            };
            _extensionClasses[foreignTypeQualifiedName] = info;
        }
        return info;
    }
}
