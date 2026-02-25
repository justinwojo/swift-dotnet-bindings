// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits protocol extension methods by:
/// 1. Mapping protocol extension methods (from swiftinterface) to conforming types (from ABI)
/// 2. Creating synthetic MethodDecl entries on each conforming type
/// 3. Generating @_silgen_name Swift wrappers that specialize Self to the concrete type
///
/// Protocol extension methods use static dispatch and do NOT appear in ABI JSON.
/// The C# side is handled by the existing MethodHandler → PInvokeEmitter pipeline
/// via UsesWrapperLibrary + UsesFreeFunctionWrapper flags.
/// </summary>
public static class ProtocolExtensionEmitter
{
    /// <summary>
    /// Accumulated Swift wrapper source lines for the current module.
    /// Written by InjectExtensionMethods, consumed by EmitSwiftWrappers.
    /// </summary>
    private static readonly List<string> _swiftWrapperLines = new();

    /// <summary>
    /// Tracks emitted Swift wrapper symbols to prevent duplicate emission.
    /// </summary>
    private static readonly HashSet<string> _emittedSymbols = new();

    /// <summary>
    /// Count of injected extension methods for logging.
    /// </summary>
    private static int _injectedCount;

    /// <summary>
    /// Resets per-module state. Called from Program.cs before the conditional
    /// inject block — NOT from ModuleHandler.Emit() (which would wipe state
    /// populated by InjectExtensionMethods before EmitSwiftWrappers reads it).
    /// </summary>
    public static void ResetForModule()
    {
        _swiftWrapperLines.Clear();
        _emittedSymbols.Clear();
        _injectedCount = 0;
    }

    /// <summary>
    /// Scans the module's conforming types, matches them to parsed protocol extension methods,
    /// and injects synthetic MethodDecl entries with corresponding Swift wrapper code.
    /// </summary>
    public static void InjectExtensionMethods(
        ModuleDecl moduleDecl,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> protocolExtensionMethods,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        if (protocolExtensionMethods.Count == 0)
            return;

        // Build protocol name → list of conforming ClassDecl map
        var conformanceMap = BuildConformanceMap(moduleDecl);

        foreach (var (protocolQualifiedName, extensionMethods) in protocolExtensionMethods)
        {
            // Extract unqualified protocol name for conformance lookup
            var dotIdx = protocolQualifiedName.LastIndexOf('.');
            var unqualifiedProtocolName = dotIdx >= 0
                ? protocolQualifiedName.Substring(dotIdx + 1)
                : protocolQualifiedName;

            if (!conformanceMap.TryGetValue(unqualifiedProtocolName, out var conformingTypes))
                continue;

            foreach (var extMethod in extensionMethods)
            {
                // Skip constrained extensions for now — we can't validate constraints
                // against conforming types without full type system resolution
                if (extMethod.WhereConstraints.Count > 0)
                    continue;

                // Skip properties (defer to later session)
                if (extMethod.IsProperty)
                    continue;

                // Skip static methods (defer to later session)
                if (extMethod.IsStatic)
                    continue;

                // Skip async methods (wrapper semantics not implemented)
                if (IsAsyncSignature(extMethod.RawSignature))
                    continue;

                // Skip throwing methods (wrapper needs try/catch handling)
                if (IsThrowingSignature(extMethod.RawSignature))
                    continue;

                foreach (var conformingType in conformingTypes)
                {
                    TryInjectMethod(moduleDecl, conformingType, extMethod, typeDatabase, logger);
                }
            }
        }

        if (_injectedCount > 0)
        {
            logger.LogInformation("Injected {Count} protocol extension methods across conforming types", _injectedCount);
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

        swiftWriter.WriteLine();
        swiftWriter.WriteLine("// --- Protocol extension method wrappers ---");
        foreach (var line in _swiftWrapperLines)
        {
            swiftWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Builds a map from unqualified protocol name → list of conforming ClassDecls.
    /// Only includes class types (struct self ABI is different — deferred to later sessions).
    /// </summary>
    private static Dictionary<string, List<ClassDecl>> BuildConformanceMap(ModuleDecl moduleDecl)
    {
        var map = new Dictionary<string, List<ClassDecl>>();
        CollectConformances(moduleDecl.Types, map);
        return map;
    }

    /// <summary>
    /// Recursively collects conformances from types and their nested types.
    /// </summary>
    private static void CollectConformances(IEnumerable<TypeDecl> types, Dictionary<string, List<ClassDecl>> map)
    {
        foreach (var type in types)
        {
            if (type is ClassDecl classDecl)
            {
                foreach (var conformance in classDecl.Conformances)
                {
                    var protocolName = conformance.Protocol.Name;
                    if (!map.ContainsKey(protocolName))
                        map[protocolName] = new List<ClassDecl>();
                    if (!map[protocolName].Contains(classDecl))
                        map[protocolName].Add(classDecl);
                }
            }

            // Recurse into nested types
            if (type.Types.Any())
            {
                CollectConformances(type.Types, map);
            }
        }
    }

    /// <summary>
    /// Attempts to inject a single protocol extension method onto a conforming class type.
    /// Applies conservative gates and generates the Swift wrapper + synthetic MethodDecl.
    /// </summary>
    private static void TryInjectMethod(
        ModuleDecl moduleDecl,
        ClassDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        var typeName = conformingType.SwiftTypeName.ModuleQualifiedName;
        var flatTypeName = FlattenTypeName(conformingType.SwiftTypeName);

        // Parse the raw signature to extract parameter types and return type
        var parseResult = ParseExtensionSignature(extMethod, typeDatabase, logger);
        if (parseResult == null)
            return;

        var (parameters, returnTypeSpec, returnTypeName) = parseResult.Value;

        // Gate: all parameter types must be resolvable and cdecl-compatible
        foreach (var (_, paramTypeSpec, _) in parameters)
        {
            if (!IsCdeclCompatibleType(paramTypeSpec, typeDatabase))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: parameter type not cdecl-compatible",
                    typeName, extMethod.MethodName);
                return;
            }
        }

        // Gate: return type must be Self, Void, or a class type
        if (!extMethod.ReturnsSelf && returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple)
        {
            if (!IsClassType(returnTypeSpec, typeDatabase))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: return type not class/Self/Void",
                    typeName, extMethod.MethodName);
                return;
            }
        }

        // Build symbol name with overload disambiguation
        var symbolName = BuildSymbolName(flatTypeName, extMethod.MethodName, parameters);

        // Skip if already emitted (e.g., from a parent class conformance)
        if (!_emittedSymbols.Add(symbolName))
            return;

        // Check for duplicate methods using reconstructed PrintedName (includes labels).
        // Build PrintedName-like keys from existing MethodDecl CSSignatures for comparison.
        var existingMethodKeys = new HashSet<string>(
            conformingType.Methods.Select(m => BuildMethodKey(m)));
        var extensionKey = extMethod.PrintedName; // e.g., "targetCache(_:)"
        if (existingMethodKeys.Contains(extensionKey))
        {
            logger.LogDebug("Skipping extension method {Type}.{Method}: collision with ABI method (key: {Key})",
                typeName, extMethod.MethodName, extensionKey);
            return;
        }

        // --- All gates passed: emit Swift wrapper and synthetic MethodDecl ---

        // Build Swift wrapper
        EmitSwiftWrapper(conformingType, extMethod, parameters, returnTypeSpec, symbolName);

        // Build synthetic MethodDecl
        var syntheticMethod = BuildSyntheticMethodDecl(
            moduleDecl, conformingType, extMethod, parameters, returnTypeSpec, returnTypeName, symbolName);

        conformingType.Methods.Add(syntheticMethod);
        _injectedCount++;
    }

    /// <summary>
    /// Parses the raw swiftinterface signature into structured parameter and return type info.
    /// Returns null if parsing fails or types can't be resolved.
    /// </summary>
    private static (List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
                     TypeSpec? returnTypeSpec, string returnTypeName)?
        ParseExtensionSignature(
            ProtocolExtensionMethodDecl extMethod,
            ITypeDatabase typeDatabase,
            ILogger logger)
    {
        var line = extMethod.RawSignature;

        // Extract parameter section
        var funcIdx = line.IndexOf($"func {extMethod.MethodName}", StringComparison.Ordinal);
        if (funcIdx < 0)
            return null;

        var parenStart = line.IndexOf('(', funcIdx);
        if (parenStart < 0)
            return null;

        // Find matching close paren
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

        // Parse parameters
        var parameters = new List<(string label, TypeSpec typeSpec, string swiftType)>();
        if (!string.IsNullOrWhiteSpace(paramStr))
        {
            var parts = SplitParameters(paramStr);
            foreach (var part in parts)
            {
                var parsed = ParseParameter(part.Trim());
                if (parsed == null)
                {
                    logger.LogDebug("Skipping extension method {Method}: could not parse parameter '{Param}'",
                        extMethod.MethodName, part.Trim());
                    return null;
                }
                parameters.Add(parsed.Value);
            }
        }

        // Parse return type
        TypeSpec? returnTypeSpec = null;
        string returnTypeName = "void";
        if (extMethod.ReturnsSelf)
        {
            // Will be resolved to the concrete type later
            returnTypeName = "Self";
        }
        else
        {
            // Extract return type from after the closing paren
            var afterParen = line.Substring(parenEnd + 1).Trim();
            // Remove trailing "{"
            var braceIdx = afterParen.IndexOf('{');
            if (braceIdx >= 0)
                afterParen = afterParen.Substring(0, braceIdx).Trim();

            var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                var returnTypeStr = afterParen.Substring(arrowIdx + 2).Trim();
                if (returnTypeStr != "Self")
                {
                    try
                    {
                        returnTypeSpec = TypeSpecParser.Parse(returnTypeStr);
                    }
                    catch
                    {
                        logger.LogDebug("Skipping extension method {Method}: TypeSpecParser error for return type '{Type}'",
                            extMethod.MethodName, returnTypeStr);
                        return null;
                    }
                    if (returnTypeSpec == null)
                    {
                        logger.LogDebug("Skipping extension method {Method}: could not parse return type '{Type}'",
                            extMethod.MethodName, returnTypeStr);
                        return null;
                    }
                    returnTypeName = returnTypeStr;
                }
                else
                {
                    // Detected Self in arrow — set flag
                    returnTypeName = "Self";
                }
            }
        }

        return (parameters, returnTypeSpec, returnTypeName);
    }

    /// <summary>
    /// Parses a single parameter from a swiftinterface parameter declaration.
    /// e.g., "_ cache: Kingfisher.ImageCache" → ("_", NamedTypeSpec("Kingfisher.ImageCache"), "Kingfisher.ImageCache")
    /// </summary>
    private static (string label, TypeSpec typeSpec, string swiftType)?
        ParseParameter(string paramDecl)
    {
        var colonIdx = paramDecl.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var beforeColon = paramDecl.Substring(0, colonIdx).Trim();
        var afterColon = paramDecl.Substring(colonIdx + 1).Trim();

        // Remove @escaping, @Sendable, @autoclosure attributes
        afterColon = StripSwiftAttributes(afterColon);

        // Remove default value (e.g., "= true", "= .init()", "= StatementArguments()")
        var defaultIdx = FindDefaultValueStart(afterColon);
        if (defaultIdx >= 0)
            afterColon = afterColon.Substring(0, defaultIdx).Trim();

        if (string.IsNullOrWhiteSpace(afterColon))
            return null;

        // Extract label (first word before colon)
        var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var label = words.Length > 0 ? words[0] : "_";

        // Parse the type — catch TypeSpecParser errors for unrecognized syntax
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

        return (label, typeSpec, afterColon);
    }

    /// <summary>
    /// Strips Swift parameter attributes like @escaping, @Sendable, @autoclosure.
    /// </summary>
    private static string StripSwiftAttributes(string typeStr)
    {
        // Remove leading @attributes (can appear multiple times)
        while (typeStr.StartsWith("@"))
        {
            var spaceIdx = typeStr.IndexOf(' ');
            if (spaceIdx < 0) break;
            typeStr = typeStr.Substring(spaceIdx + 1).TrimStart();
        }
        // Also handle "inout" prefix
        if (typeStr.StartsWith("inout "))
            typeStr = typeStr.Substring(6).TrimStart();
        return typeStr;
    }

    /// <summary>
    /// Finds the start of a default value assignment in a parameter type string.
    /// Handles nested angle brackets and parentheses.
    /// Returns -1 if no default value.
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
                return i - 1; // Include the space before '='
        }
        return -1;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a cdecl-compatible type for Swift wrapper parameters.
    /// Currently supports: class types (IntPtr) and primitives (Bool, Int, Float, etc.).
    /// SimpleEnum and ObjCBridged are excluded — the wrapper marshals all non-primitives as Unmanaged.
    /// </summary>
    private static bool IsCdeclCompatibleType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Reject closures embedded in named types
            if (namedType.ContainsGenericParameters)
            {
                // Optional<T> is ok if T is cdecl-compatible
                if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
                {
                    var inner = namedType.GenericParameters[0];
                    return IsCdeclCompatibleType(inner, typeDatabase);
                }
                return false; // Generic types like Array<T>, etc. are not simple
            }

            // Check built-in Swift primitives
            if (IsSwiftPrimitive(namedType.Name))
                return true;

            // Check TypeDatabase for class types only
            // SimpleEnum and ObjCBridged are excluded because EmitSwiftWrapper marshals
            // all non-primitive NamedTypeSpec as Unmanaged<T> (class ABI).
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
                // Not a valid module-qualified name (e.g., opaque types like "some Protocol")
                return false;
            }

            return false;
        }

        if (typeSpec is ClosureTypeSpec)
            return false; // Closures not supported in protocol extension wrappers

        if (typeSpec is TupleTypeSpec tuple)
            return tuple.IsEmptyTuple; // Only empty tuple (Void) is ok

        if (typeSpec is ProtocolListTypeSpec)
            return false; // Existentials not supported

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a class type in the TypeDatabase.
    /// </summary>
    private static bool IsClassType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

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

        return false;
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents an async method.
    /// Detects "async" keyword after the closing paren and before "->"/"{".
    /// </summary>
    private static bool IsAsyncSignature(string rawSignature)
    {
        // Find the closing paren of the parameter list
        int depth = 0;
        int parenEnd = -1;
        for (int i = 0; i < rawSignature.Length; i++)
        {
            if (rawSignature[i] == '(') depth++;
            if (rawSignature[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        if (parenEnd < 0)
            return false;

        // Check the text between closing paren and return arrow / opening brace
        var afterParen = rawSignature.Substring(parenEnd + 1);
        var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
        var braceIdx = afterParen.IndexOf('{');
        var endIdx = afterParen.Length;
        if (arrowIdx >= 0) endIdx = Math.Min(endIdx, arrowIdx);
        if (braceIdx >= 0) endIdx = Math.Min(endIdx, braceIdx);

        var qualifiers = afterParen.Substring(0, endIdx);
        // Check for "async" as a whole word
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"\basync\b");
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents a throwing method.
    /// Detects "throws" keyword after the closing paren and before "->"/"{".
    /// </summary>
    private static bool IsThrowingSignature(string rawSignature)
    {
        // Find the closing paren of the parameter list
        int depth = 0;
        int parenEnd = -1;
        for (int i = 0; i < rawSignature.Length; i++)
        {
            if (rawSignature[i] == '(') depth++;
            if (rawSignature[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        if (parenEnd < 0)
            return false;

        // Check the text between closing paren and return arrow / opening brace
        var afterParen = rawSignature.Substring(parenEnd + 1);
        var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
        var braceIdx = afterParen.IndexOf('{');
        var endIdx = afterParen.Length;
        if (arrowIdx >= 0) endIdx = Math.Min(endIdx, arrowIdx);
        if (braceIdx >= 0) endIdx = Math.Min(endIdx, braceIdx);

        var qualifiers = afterParen.Substring(0, endIdx);
        // Check for "throws" as a whole word (also catches "rethrows")
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"\b(re)?throws\b");
    }

    /// <summary>
    /// Checks if a type name represents a Swift primitive type.
    /// </summary>
    private static bool IsSwiftPrimitive(string typeName)
    {
        return typeName switch
        {
            "Swift.Int" or "Swift.Int8" or "Swift.Int16" or "Swift.Int32" or "Swift.Int64" => true,
            "Swift.UInt" or "Swift.UInt8" or "Swift.UInt16" or "Swift.UInt32" or "Swift.UInt64" => true,
            "Swift.Float" or "Swift.Double" => true,
            "Swift.Bool" => true,
            "CoreFoundation.CGFloat" => true,
            "CoreFoundation.CGSize" or "CoreFoundation.CGPoint" or "CoreFoundation.CGRect" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Checks if a Swift primitive type should be passed as UnsafeMutableRawPointer in the wrapper.
    /// CGSize/CGPoint/CGRect are struct types that need pointer passing.
    /// </summary>
    private static bool IsStructPrimitive(string typeName)
    {
        return typeName switch
        {
            "CoreFoundation.CGSize" or "CoreFoundation.CGPoint" or "CoreFoundation.CGRect" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Emits the @_silgen_name Swift wrapper function for a protocol extension method.
    /// </summary>
    private static void EmitSwiftWrapper(
        ClassDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string symbolName)
    {
        var typeName = conformingType.SwiftTypeName.ModuleQualifiedName;

        // Build Swift parameter list for the wrapper function
        var swiftParams = new List<string>();
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        foreach (var (label, typeSpec, swiftType) in parameters)
        {
            var paramName = SanitizeSwiftParamName(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !IsSwiftPrimitive(namedType.Name))
            {
                // Class/ObjC types: pass as UnsafeMutableRawPointer
                swiftParams.Add($"_ {paramName}: UnsafeMutableRawPointer");
            }
            else
            {
                // Primitives and simple enums: pass directly
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                swiftParams.Add($"_ {paramName}: {renderedType}");
            }
        }

        // Build return type
        string swiftReturnType;
        bool returnIsClass;
        if (extMethod.ReturnsSelf || (returnTypeSpec == null && !extMethod.ReturnsSelf))
        {
            if (extMethod.ReturnsSelf)
            {
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
            }
            else
            {
                // Void return
                swiftReturnType = "";
                returnIsClass = false;
            }
        }
        else
        {
            if (returnTypeSpec is NamedTypeSpec retNamedType && !IsSwiftPrimitive(retNamedType.Name))
            {
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
            }
            else
            {
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
            }
        }

        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        // Emit the wrapper function
        _swiftWrapperLines.Add("");
        _swiftWrapperLines.Add($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated)
        {
            _swiftWrapperLines.Add("@MainActor");
        }
        _swiftWrapperLines.Add($"public func {symbolName}({string.Join(", ", swiftParams)}){returnArrow} {{");

        // Emit self conversion
        _swiftWrapperLines.Add($"    let instance = Unmanaged<{typeName}>.fromOpaque(self_).takeUnretainedValue()");

        // Emit parameter conversions
        var callArgs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var (label, typeSpec, swiftType) = parameters[i];
            var paramName = SanitizeSwiftParamName(label == "_" ? GetParamNameFromType(swiftType) : label);

            if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !IsSwiftPrimitive(namedType.Name))
            {
                // Class type: convert from opaque pointer
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                var localName = $"__{paramName}";
                _swiftWrapperLines.Add($"    let {localName} = Unmanaged<{renderedType}>.fromOpaque({paramName}).takeUnretainedValue()");
                callArgs.Add(label == "_" ? localName : $"{label}: {localName}");
            }
            else
            {
                // Primitive: pass through
                callArgs.Add(label == "_" ? paramName : $"{label}: {paramName}");
            }
        }

        // Emit method call
        var callStr = $"instance.{extMethod.MethodName}({string.Join(", ", callArgs)})";

        if (extMethod.ReturnsSelf || returnIsClass)
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

    /// <summary>
    /// Builds a synthetic MethodDecl that the existing MethodHandler → PInvokeEmitter pipeline
    /// will process like any other method. Sets UsesWrapperLibrary + UsesFreeFunctionWrapper
    /// so PInvokeEmitter routes to the wrapper library with explicit IntPtr self.
    /// </summary>
    private static MethodDecl BuildSyntheticMethodDecl(
        ModuleDecl moduleDecl,
        ClassDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string returnTypeName,
        string symbolName)
    {
        // Build CSSignature: [returnType, param1, param2, ...]
        var csSignature = new List<ArgumentDecl>();

        // Return type (first element of CSSignature)
        TypeSpec resolvedReturnTypeSpec;
        if (extMethod.ReturnsSelf || returnTypeName == "Self")
        {
            resolvedReturnTypeSpec = new NamedTypeSpec(conformingType.SwiftTypeName.ModuleQualifiedName);
        }
        else if (returnTypeSpec != null)
        {
            resolvedReturnTypeSpec = returnTypeSpec;
        }
        else
        {
            resolvedReturnTypeSpec = TupleTypeSpec.Empty;
        }

        csSignature.Add(new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = resolvedReturnTypeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        // Parameters
        foreach (var (label, typeSpec, _) in parameters)
        {
            var internalName = label == "_" ? GetParamNameFromType(typeSpec.ToString()) : label;
            csSignature.Add(new ArgumentDecl
            {
                Name = label,
                PrivateName = internalName,
                SwiftTypeSpec = typeSpec,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = conformingType,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = extMethod.MethodName,
            MangledName = symbolName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = conformingType,
            ModuleDecl = moduleDecl,
            UsesWrapperLibrary = true,
            UsesFreeFunctionWrapper = true,
            IsProtocolExtensionMethod = true,
            IsActorIsolated = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated,
        };
    }

    /// <summary>
    /// Builds a unique symbol name for the Swift wrapper function.
    /// Format: SBW_{FlatTypeName}_{methodName}[_{label1}_{label2}_...] for disambiguation.
    /// Uses parameter labels (like Swift's PrintedName) for precise overload disambiguation.
    /// </summary>
    private static string BuildSymbolName(string flatTypeName, string methodName,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters)
    {
        var baseName = $"SBW_{flatTypeName}_{methodName}";
        if (parameters.Count > 0)
        {
            // Use parameter labels for disambiguation (mirrors Swift's PrintedName semantics)
            var labels = string.Join("_", parameters.Select(p =>
            {
                var label = p.label == "_" ? "" : p.label;
                // Also append a short type suffix for same-label overloads
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

    /// <summary>
    /// Reconstructs a PrintedName-like key from a MethodDecl for collision checking.
    /// Format: "methodName(label1:label2:)" matching Swift's PrintedName convention.
    /// </summary>
    private static string BuildMethodKey(MethodDecl method)
    {
        // CSSignature[0] is the return type, params start at [1]
        if (method.CSSignature.Count <= 1)
            return $"{method.Name}()";

        var labels = string.Join("", method.CSSignature.Skip(1).Select(arg =>
        {
            var label = string.IsNullOrEmpty(arg.Name) || arg.Name == "_" ? "_" : arg.Name;
            return $"{label}:";
        }));
        return $"{method.Name}({labels})";
    }

    /// <summary>
    /// Flattens a SwiftTypeName for use in symbol names.
    /// e.g., "Kingfisher.KF.Builder" → "KF_Builder"
    /// </summary>
    private static string FlattenTypeName(SwiftTypeName swiftTypeName)
    {
        // Use ModuleQualifiedName but strip module prefix and replace dots with underscores
        var name = swiftTypeName.ModuleQualifiedName;
        var dotIdx = name.IndexOf('.');
        if (dotIdx >= 0)
            name = name.Substring(dotIdx + 1);
        return name.Replace(".", "_");
    }

    /// <summary>
    /// Extracts a reasonable parameter name from a Swift type string.
    /// e.g., "Kingfisher.ImageCache" → "cache", "Swift.Bool" → "enabled"
    /// </summary>
    private static string GetParamNameFromType(string swiftType)
    {
        var dotIdx = swiftType.LastIndexOf('.');
        var typeName = dotIdx >= 0 ? swiftType.Substring(dotIdx + 1) : swiftType;

        if (typeName == "Bool") return "enabled";
        if (typeName == "Int" || typeName == "Int32" || typeName == "Int64") return "value";
        if (typeName == "Float" || typeName == "Double" || typeName == "CGFloat") return "value";
        if (typeName == "String") return "str";

        // Lowercase first character
        if (typeName.Length > 0)
            return char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);

        return "arg";
    }

    /// <summary>
    /// Sanitizes a Swift parameter name to avoid keyword conflicts.
    /// </summary>
    private static string SanitizeSwiftParamName(string name)
    {
        // Avoid Swift keywords
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

    /// <summary>
    /// Splits a parameter list string by commas, respecting nested angle brackets,
    /// parentheses, and square brackets.
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
}
