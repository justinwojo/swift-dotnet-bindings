// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Parses Clang AST JSON output into an ObjCModule model.
/// </summary>
public static class ClangAstParser
{
    /// <summary>
    /// Parses a Clang AST JSON string into an ObjCModule.
    /// </summary>
    public static ObjCModule Parse(string json, string moduleName, string frameworkHeadersPath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var classes = new List<ObjCClassDecl>();
        var protocols = new List<ObjCProtocolDecl>();
        var enums = new List<ObjCEnumDecl>();
        var structs = new List<ObjCStructDecl>();
        var functions = new List<ObjCFunctionDecl>();
        var constants = new List<ObjCConstantDecl>();
        var typedefs = new List<ObjCTypedefDecl>();
        var categories = new List<(string ClassName, List<ObjCMethodDecl> Methods, List<ObjCPropertyDecl> Properties)>();

        // Normalize headers path for comparison
        frameworkHeadersPath = frameworkHeadersPath.TrimEnd('/');

        if (!root.TryGetProperty("inner", out var inner))
        {
            return new ObjCModule
            {
                ModuleName = moduleName,
                FrameworkPath = frameworkHeadersPath
            };
        }

        // Track the "current file" — clang omits loc.file when it's the same as
        // the previous declaration, so we must carry it forward.
        string? currentFile = null;

        // Pass 1: Parse all top-level declarations
        foreach (var node in inner.EnumerateArray())
        {
            if (!node.TryGetProperty("kind", out var kindProp))
                continue;

            var kind = kindProp.GetString();
            if (kind == null)
                continue;

            // Update current file tracking and filter by framework headers path
            if (!IsPublicDeclaration(node, frameworkHeadersPath, ref currentFile))
                continue;

            switch (kind)
            {
                case "ObjCInterfaceDecl":
                    if (!IsForwardDeclaration(node))
                    {
                        var classDecl = ParseClassDecl(node);
                        if (classDecl != null)
                            classes.Add(classDecl);
                    }
                    break;

                case "ObjCProtocolDecl":
                    if (!IsForwardDeclaration(node))
                    {
                        var protocolDecl = ParseProtocolDecl(node);
                        if (protocolDecl != null)
                            protocols.Add(protocolDecl);
                    }
                    break;

                case "ObjCCategoryDecl":
                    var category = ParseCategoryDecl(node);
                    if (category != null)
                        categories.Add(category.Value);
                    break;

                case "EnumDecl":
                    var enumDecl = ParseEnumDecl(node);
                    if (enumDecl != null)
                        enums.Add(enumDecl);
                    break;

                case "RecordDecl":
                    var structDecl = ParseStructDecl(node);
                    if (structDecl != null)
                        structs.Add(structDecl);
                    break;

                case "FunctionDecl":
                    var funcDecl = ParseFunctionDecl(node);
                    if (funcDecl != null)
                        functions.Add(funcDecl);
                    break;

                case "VarDecl":
                    var constDecl = ParseConstantDecl(node);
                    if (constDecl != null)
                        constants.Add(constDecl);
                    break;

                case "TypedefDecl":
                    var typedefDecl = ParseTypedefDecl(node);
                    if (typedefDecl != null)
                        typedefs.Add(typedefDecl);
                    break;
            }
        }

        // Pass 2: Merge categories onto their owning classes
        foreach (var (className, methods, properties) in categories)
        {
            var owningClass = classes.FirstOrDefault(c => c.Name == className);
            if (owningClass != null)
            {
                var idx = classes.IndexOf(owningClass);
                classes[idx] = owningClass with
                {
                    Methods = [.. owningClass.Methods, .. methods],
                    Properties = [.. owningClass.Properties, .. properties]
                };
            }
            // If class not found (forward-declared in another framework), skip category
        }

        return new ObjCModule
        {
            ModuleName = moduleName,
            FrameworkPath = frameworkHeadersPath,
            Classes = classes,
            Protocols = protocols,
            Enums = enums,
            Structs = structs,
            Functions = functions,
            Constants = constants,
            Typedefs = typedefs
        };
    }

    // ──────────────────────────────────────────────
    // Top-level declaration parsers
    // ──────────────────────────────────────────────

    private static ObjCClassDecl? ParseClassDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var superclass = GetOptionalString(element, "super");
        // Extract superclass name: could be nested under "name"
        if (superclass == null && element.TryGetProperty("super", out var superProp) &&
            superProp.ValueKind == JsonValueKind.Object)
        {
            superclass = GetOptionalString(superProp, "name");
        }

        var protocols = new List<string>();
        if (element.TryGetProperty("protocols", out var protocolsArr))
        {
            foreach (var p in protocolsArr.EnumerateArray())
            {
                var pName = GetOptionalString(p, "name");
                if (pName != null)
                    protocols.Add(pName);
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();
        var availability = new List<ObjCAvailability>();

        ParseContainerChildren(element, methods, properties, availability, isProtocol: false);

        return new ObjCClassDecl
        {
            Name = name,
            SuperclassName = superclass,
            ProtocolNames = protocols,
            Methods = methods,
            Properties = properties,
            Availability = availability
        };
    }

    private static ObjCProtocolDecl? ParseProtocolDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var inherited = new List<string>();
        if (element.TryGetProperty("protocols", out var protocolsArr))
        {
            foreach (var p in protocolsArr.EnumerateArray())
            {
                var pName = GetOptionalString(p, "name");
                if (pName != null)
                    inherited.Add(pName);
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();
        var availability = new List<ObjCAvailability>();

        ParseContainerChildren(element, methods, properties, availability, isProtocol: true);

        return new ObjCProtocolDecl
        {
            Name = name,
            InheritedProtocolNames = inherited,
            Methods = methods,
            Properties = properties,
            Availability = availability
        };
    }

    private static (string ClassName, List<ObjCMethodDecl> Methods, List<ObjCPropertyDecl> Properties)? ParseCategoryDecl(JsonElement element)
    {
        // In clang AST, the owning class is in "interface.name", not "name".
        // "name" is the category name (e.g., "NSCoderMethods" in NSObject(NSCoderMethods)).
        string? className = null;
        if (element.TryGetProperty("interface", out var iface) &&
            iface.ValueKind == JsonValueKind.Object)
        {
            className = GetOptionalString(iface, "name");
        }
        if (className == null) return null;

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();
        var availability = new List<ObjCAvailability>();

        ParseContainerChildren(element, methods, properties, availability, isProtocol: false);

        return (className, methods, properties);
    }

    private static ObjCEnumDecl? ParseEnumDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var isOptions = false;
        var cases = new List<ObjCEnumCaseDecl>();
        var availability = new List<ObjCAvailability>();
        ObjCTypeRef? underlyingType = null;

        // Check for fixed underlying type
        if (element.TryGetProperty("fixedUnderlyingType", out var fixedType))
        {
            var qualType = GetOptionalString(fixedType, "qualType");
            if (qualType != null)
                underlyingType = ObjCTypeRefParser.Parse(qualType);
        }

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "EnumConstantDecl":
                        var caseName = GetName(child);
                        if (caseName != null)
                        {
                            long? value = null;
                            // Try to extract value from inner ConstantExpr or IntegerLiteral
                            if (child.TryGetProperty("inner", out var caseInner))
                            {
                                value = TryExtractEnumValue(caseInner);
                            }
                            cases.Add(new ObjCEnumCaseDecl { Name = caseName, Value = value });
                        }
                        break;

                    case "FlagEnumAttr":
                        isOptions = true;
                        break;

                    case "AvailabilityAttr":
                        var avail = ParseAvailability(child);
                        if (avail != null)
                            availability.Add(avail);
                        break;
                }
            }
        }

        return new ObjCEnumDecl
        {
            Name = name,
            IsOptions = isOptions,
            UnderlyingType = underlyingType,
            Cases = cases,
            Availability = availability
        };
    }

    private static ObjCStructDecl? ParseStructDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var fields = new List<ObjCStructField>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "FieldDecl")
                {
                    var fieldName = GetName(child);
                    var fieldType = GetQualType(child);
                    if (fieldName != null && fieldType != null)
                    {
                        fields.Add(new ObjCStructField
                        {
                            Name = fieldName,
                            Type = ObjCTypeRefParser.Parse(fieldType)
                        });
                    }
                }
            }
        }

        return new ObjCStructDecl { Name = name, Fields = fields };
    }

    private static ObjCFunctionDecl? ParseFunctionDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var returnType = GetQualType(element);
        if (returnType == null) return null;

        var parameters = new List<ObjCParameterDecl>();
        var availability = new List<ObjCAvailability>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "ParmVarDecl":
                        var param = ParseParameter(child);
                        if (param != null)
                            parameters.Add(param);
                        break;

                    case "AvailabilityAttr":
                        var avail = ParseAvailability(child);
                        if (avail != null)
                            availability.Add(avail);
                        break;
                }
            }
        }

        // Parse the return type from the function type signature
        var funcReturnType = ParseFunctionReturnType(returnType);

        return new ObjCFunctionDecl
        {
            Name = name,
            ReturnType = ObjCTypeRefParser.Parse(funcReturnType),
            Parameters = parameters,
            Availability = availability
        };
    }

    private static ObjCConstantDecl? ParseConstantDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var qualType = GetQualType(element);
        if (qualType == null) return null;

        var isExtern = false;
        if (element.TryGetProperty("storageClass", out var sc))
        {
            isExtern = sc.GetString() == "extern";
        }

        return new ObjCConstantDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsExtern = isExtern
        };
    }

    private static ObjCTypedefDecl? ParseTypedefDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        // Get the underlying type from inner or type
        string? underlyingQualType = null;
        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                if (childKind is "BuiltinType" or "RecordType" or "ElaboratedType"
                    or "ObjCObjectPointerType" or "TypedefType" or "PointerType"
                    or "BlockPointerType" or "EnumType")
                {
                    underlyingQualType = GetOptionalString(child, "qualType")
                        ?? GetQualType(child);
                    break;
                }
            }
        }

        // Fall back to the type property
        underlyingQualType ??= GetQualType(element);
        if (underlyingQualType == null) return null;

        return new ObjCTypedefDecl
        {
            Name = name,
            UnderlyingType = ObjCTypeRefParser.Parse(underlyingQualType)
        };
    }

    // ──────────────────────────────────────────────
    // Container children parsing (class/protocol/category)
    // ──────────────────────────────────────────────

    private static void ParseContainerChildren(
        JsonElement element,
        List<ObjCMethodDecl> methods,
        List<ObjCPropertyDecl> properties,
        List<ObjCAvailability> availability,
        bool isProtocol)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return;

        // For protocols, build a set of source lines that fall in @optional sections
        // by reading the header file. Clang JSON marks properties with control:"optional"
        // but does NOT mark methods — we need source-level section parsing.
        HashSet<int>? optionalLines = null;
        if (isProtocol)
            optionalLines = BuildOptionalLineSet(element);

        foreach (var child in inner.EnumerateArray())
        {
            var childKind = GetOptionalString(child, "kind");
            switch (childKind)
            {
                case "ObjCMethodDecl":
                    // Skip implicit accessor methods generated for properties
                    if (child.TryGetProperty("isImplicit", out var implProp) && implProp.GetBoolean())
                        break;
                    var method = ParseMethodDecl(child, IsInOptionalSection(child, optionalLines));
                    if (method != null)
                        methods.Add(method);
                    break;

                case "ObjCPropertyDecl":
                    var prop = ParsePropertyDecl(child, optionalLines);
                    if (prop != null)
                        properties.Add(prop);
                    break;

                case "AvailabilityAttr":
                    var avail = ParseAvailability(child);
                    if (avail != null)
                        availability.Add(avail);
                    break;
            }
        }
    }

    /// <summary>
    /// Builds a set of source line numbers that fall within @optional sections
    /// of a protocol, by reading the header file and finding @optional/@required markers.
    /// Returns null if the source file can't be read.
    /// </summary>
    private static HashSet<int>? BuildOptionalLineSet(JsonElement protocolElement)
    {
        // Resolve the source file from the protocol's loc
        var filePath = ResolveLocFile(protocolElement);
        if (filePath == null || !File.Exists(filePath))
            return null;

        // Get the protocol's line range from the AST
        int startLine = GetLocLine(protocolElement);
        int endLine = GetRangeEndLine(protocolElement);
        if (startLine <= 0) return null;
        if (endLine <= 0) endLine = int.MaxValue;

        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
        catch { return null; }

        var optionalLines = new HashSet<int>();
        var inOptional = false;

        for (int i = startLine - 1; i < Math.Min(lines.Length, endLine); i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "@optional")
                inOptional = true;
            else if (trimmed == "@required")
                inOptional = false;

            if (inOptional)
                optionalLines.Add(i + 1); // 1-based line numbers
        }

        return optionalLines.Count > 0 ? optionalLines : null;
    }

    private static bool IsInOptionalSection(JsonElement child, HashSet<int>? optionalLines)
    {
        if (optionalLines == null) return false;
        int line = GetLocLine(child);
        return line > 0 && optionalLines.Contains(line);
    }

    private static string? ResolveLocFile(JsonElement element)
    {
        if (!element.TryGetProperty("loc", out var loc))
            return null;
        if (TryGetLocFile(loc, "file", out var f)) return f;
        if (loc.TryGetProperty("expansionLoc", out var exp) && TryGetLocFile(exp, "file", out f)) return f;
        if (loc.TryGetProperty("spellingLoc", out var sp) && TryGetLocFile(sp, "file", out f)) return f;
        return null;
    }

    private static int GetLocLine(JsonElement element)
    {
        if (element.TryGetProperty("loc", out var loc))
        {
            if (loc.TryGetProperty("line", out var lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
            if (loc.TryGetProperty("expansionLoc", out var exp) &&
                exp.TryGetProperty("line", out lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
            if (loc.TryGetProperty("spellingLoc", out var sp) &&
                sp.TryGetProperty("line", out lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
        }
        return 0;
    }

    private static int GetRangeEndLine(JsonElement element)
    {
        if (element.TryGetProperty("range", out var range) &&
            range.TryGetProperty("end", out var end) &&
            end.TryGetProperty("line", out var lineProp) && lineProp.ValueKind == JsonValueKind.Number)
            return lineProp.GetInt32();
        return 0;
    }

    private static ObjCMethodDecl? ParseMethodDecl(JsonElement element, bool isOptional)
    {
        var name = GetName(element);
        if (name == null) return null;

        var isInstance = true;
        if (element.TryGetProperty("instance", out var instanceProp))
        {
            isInstance = instanceProp.GetBoolean();
        }

        var returnQualType = GetReturnType(element) ?? "void";

        var parameters = new List<ObjCParameterDecl>();
        var methodAvailability = new List<ObjCAvailability>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "ParmVarDecl":
                        var param = ParseParameter(child);
                        if (param != null)
                            parameters.Add(param);
                        break;

                    case "AvailabilityAttr":
                        var avail = ParseAvailability(child);
                        if (avail != null)
                            methodAvailability.Add(avail);
                        break;
                }
            }
        }

        return new ObjCMethodDecl
        {
            Selector = name,
            ReturnType = ObjCTypeRefParser.Parse(returnQualType),
            Parameters = parameters,
            IsInstanceMethod = isInstance,
            IsOptional = isOptional,
            Availability = methodAvailability
        };
    }

    private static ObjCPropertyDecl? ParsePropertyDecl(JsonElement element, HashSet<int>? optionalLines)
    {
        var name = GetName(element);
        if (name == null) return null;

        var qualType = GetQualType(element) ?? "id";

        var isReadonly = false;
        if (element.TryGetProperty("readonly", out var roProp))
        {
            isReadonly = roProp.GetBoolean();
        }

        var isClass = false;
        if (element.TryGetProperty("class", out var classProp))
        {
            isClass = classProp.GetBoolean();
        }

        string? getter = null;
        if (element.TryGetProperty("getter", out var getterProp))
        {
            getter = getterProp.ValueKind == JsonValueKind.Object
                ? GetOptionalString(getterProp, "name")
                : getterProp.GetString();
        }

        string? setter = null;
        if (element.TryGetProperty("setter", out var setterProp))
        {
            setter = setterProp.ValueKind == JsonValueKind.Object
                ? GetOptionalString(setterProp, "name")
                : setterProp.GetString();
        }

        var propAvailability = new List<ObjCAvailability>();
        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "AvailabilityAttr")
                {
                    var avail = ParseAvailability(child);
                    if (avail != null)
                        propAvailability.Add(avail);
                }
            }
        }

        // Properties have control:"optional" in clang JSON;
        // also check source-level section for consistency
        var isOptional = false;
        var control = GetOptionalString(element, "control");
        if (control == "optional")
            isOptional = true;
        else if (control == null)
            isOptional = IsInOptionalSection(element, optionalLines);

        return new ObjCPropertyDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsReadonly = isReadonly,
            IsClass = isClass,
            IsOptional = isOptional,
            GetterSelector = getter,
            SetterSelector = setter,
            Availability = propAvailability
        };
    }

    private static ObjCParameterDecl? ParseParameter(JsonElement element)
    {
        var name = GetName(element) ?? "";
        var qualType = GetQualType(element);
        if (qualType == null) return null;

        return new ObjCParameterDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType)
        };
    }

    private static ObjCAvailability? ParseAvailability(JsonElement element)
    {
        var platform = GetOptionalString(element, "platform");
        if (platform == null) return null;

        return new ObjCAvailability
        {
            Platform = platform,
            IntroducedVersion = GetOptionalString(element, "introduced"),
            DeprecatedVersion = GetOptionalString(element, "deprecated"),
            ObsoletedVersion = GetOptionalString(element, "obsoleted"),
            IsUnavailable = element.TryGetProperty("unavailable", out var u) && u.GetBoolean(),
            Message = GetOptionalString(element, "message")
        };
    }

    // ──────────────────────────────────────────────
    // Location filtering
    // ──────────────────────────────────────────────

    /// <summary>
    /// Determines if a declaration is from the framework's public headers.
    /// Also updates currentFile tracking, since clang omits loc.file when the
    /// file hasn't changed from the previous declaration.
    /// </summary>
    internal static bool IsPublicDeclaration(JsonElement decl, string frameworkHeadersPath, ref string? currentFile)
    {
        if (!decl.TryGetProperty("loc", out var loc))
            return false;

        // Extract any file path from the loc fields and update tracking
        string? resolvedFile = null;

        // 1. loc.file (direct source location — updates current file)
        if (TryGetLocFile(loc, "file", out var f))
        {
            currentFile = f;
            resolvedFile = f;
        }

        // 2. loc.expansionLoc.file (macro-expanded declarations)
        if (resolvedFile == null && loc.TryGetProperty("expansionLoc", out var expLoc))
        {
            if (TryGetLocFile(expLoc, "file", out f))
            {
                currentFile = f;
                resolvedFile = f;
            }
        }

        // 3. loc.spellingLoc.file (spelling location for macro args)
        if (resolvedFile == null && loc.TryGetProperty("spellingLoc", out var spLoc))
        {
            if (TryGetLocFile(spLoc, "file", out f))
            {
                currentFile = f;
                resolvedFile = f;
            }
        }

        // 4. loc.includedFrom.file (the file that #imported this header)
        if (loc.TryGetProperty("includedFrom", out var incFrom) &&
            TryGetLocFile(incFrom, "file", out f))
        {
            if (IsUnderPath(f, frameworkHeadersPath))
                return true;
        }

        // 5. If no file field at all, inherit from previous declaration's file
        resolvedFile ??= currentFile;

        if (resolvedFile != null && IsUnderPath(resolvedFile, frameworkHeadersPath))
            return true;

        return false;
    }

    private static bool TryGetLocFile(JsonElement parent, string key, out string value)
    {
        value = "";
        if (parent.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return !string.IsNullOrEmpty(value);
        }
        return false;
    }

    private static bool IsUnderPath(string filePath, string basePath)
    {
        return filePath.StartsWith(basePath, StringComparison.Ordinal);
    }

    internal static bool IsForwardDeclaration(JsonElement element)
    {
        // Forward declarations have no inner array, or empty inner, and no super
        if (element.TryGetProperty("inner", out var inner) && inner.GetArrayLength() > 0)
            return false;

        // If it has a superclass, it's a real definition even without inner
        if (element.TryGetProperty("super", out _))
            return false;

        return true;
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static string? GetName(JsonElement element)
    {
        return GetOptionalString(element, "name");
    }

    private static string? GetOptionalString(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string? GetQualType(JsonElement element)
    {
        if (element.TryGetProperty("type", out var typeProp) &&
            typeProp.TryGetProperty("qualType", out var qt))
        {
            return qt.GetString();
        }
        return null;
    }

    private static string? GetReturnType(JsonElement element)
    {
        if (element.TryGetProperty("returnType", out var rt) &&
            rt.TryGetProperty("qualType", out var qt))
        {
            return qt.GetString();
        }
        return null;
    }

    private static string ParseFunctionReturnType(string funcTypeStr)
    {
        // Function types in clang AST look like "void (int, float)"
        // Extract the return type (everything before the first '(')
        var parenIdx = funcTypeStr.IndexOf('(');
        if (parenIdx > 0)
            return funcTypeStr[..parenIdx].Trim();
        return funcTypeStr;
    }

    private static long? TryExtractEnumValue(JsonElement innerArray)
    {
        foreach (var child in innerArray.EnumerateArray())
        {
            var kind = GetOptionalString(child, "kind");

            // ConstantExpr wraps the value
            if (kind == "ConstantExpr")
            {
                if (child.TryGetProperty("value", out var valProp))
                {
                    var valStr = valProp.GetString();
                    if (valStr != null && long.TryParse(valStr, out var val))
                        return val;
                }
                // Recurse into ConstantExpr's inner
                if (child.TryGetProperty("inner", out var ceInner))
                    return TryExtractEnumValue(ceInner);
            }

            if (kind == "IntegerLiteral")
            {
                if (child.TryGetProperty("value", out var valProp))
                {
                    var valStr = valProp.GetString();
                    if (valStr != null && long.TryParse(valStr, out var val))
                        return val;
                }
            }
        }
        return null;
    }
}
