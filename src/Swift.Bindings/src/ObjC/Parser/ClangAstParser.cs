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
        var systemTypedefs = new List<ObjCTypedefDecl>();
        var categories = new List<ObjCCategoryDecl>();
        var appleSdkTypeNames = new HashSet<string>();

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

        // Pre-scan: collect ObjC class names that declare lightweight generic type parameters.
        // These are used by ObjCTypeRefParser to distinguish generic containers (RLMResults<ObjectType>)
        // from protocol-qualified types (NSObject<NSCopying>) when both use simple identifier args.
        var astGenericContainers = ScanGenericContainerNames(inner);
        ObjCTypeRefParser.SetAdditionalGenericContainers(
            astGenericContainers.Count > 0 ? astGenericContainers : null);
        try
        {

        // Track the "current file" — clang omits loc.file when it's the same as
        // the previous declaration, so we must carry it forward.
        string? currentFile = null;

        // Track the last anonymous RecordDecl (struct with fields but no name)
        // to promote when a typedef follows it.
        List<ObjCStructField>? lastAnonymousStructFields = null;
        bool lastAnonymousHasUnsafeLayout = false;
        string? lastAnonymousUnsafeReason = null;

        // Pass 1: Parse all top-level declarations
        foreach (var node in inner.EnumerateArray())
        {
            if (!node.TryGetProperty("kind", out var kindProp))
                continue;

            var kind = kindProp.GetString();
            if (kind == null)
                continue;

            // Update current file tracking and filter by framework headers path.
            // IsPublicDeclaration always updates currentFile tracking (side-effect),
            // even when returning false, so file tracking stays accurate.
            var isFrameworkLocal = IsPublicDeclaration(node, frameworkHeadersPath, ref currentFile, out var nodeResolvedFile);


            // Non-framework-local declarations: parse TypedefDecl for typedef resolution,
            // and collect class/protocol names from Apple SDK headers for ApiDefinition
            // type resolvability (these types are available via .NET iOS framework bindings).
            if (!isFrameworkLocal)
            {
                if (kind == "TypedefDecl")
                {
                    // Fall through to switch below
                }
                else if ((kind is "ObjCInterfaceDecl" or "ObjCProtocolDecl") && IsAppleSdkPath(nodeResolvedFile))
                {
                    var name = GetName(node);
                    if (name != null)
                        appleSdkTypeNames.Add(name);
                    continue;
                }
                else
                {
                    continue;
                }
            }

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
                        categories.Add(category);
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
                    else
                    {
                        // Anonymous struct — remember its fields and layout info for potential typedef promotion
                        var (anonFields, hasUnsafe, unsafeReason) = ParseStructFieldsWithLayout(node);
                        lastAnonymousStructFields = anonFields.Count > 0 || hasUnsafe ? anonFields : null;
                        lastAnonymousHasUnsafeLayout = hasUnsafe;
                        lastAnonymousUnsafeReason = unsafeReason;
                    }
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
                    // Only framework-local typedefs can consume anonymous struct fields.
                    // A system-header typedef must NOT steal pending fields from a
                    // framework-local anonymous RecordDecl that precedes it.
                    var (typedefDecl, promotedStruct) = ParseTypedefDecl(node,
                        isFrameworkLocal ? lastAnonymousStructFields : null,
                        isFrameworkLocal ? lastAnonymousHasUnsafeLayout : false,
                        isFrameworkLocal ? lastAnonymousUnsafeReason : null);
                    if (isFrameworkLocal)
                    {
                        lastAnonymousStructFields = null; // consumed by framework-local typedef
                        lastAnonymousHasUnsafeLayout = false;
                        lastAnonymousUnsafeReason = null;
                    }
                    if (typedefDecl != null)
                    {
                        if (isFrameworkLocal)
                            typedefs.Add(typedefDecl);
                        else
                            systemTypedefs.Add(typedefDecl);
                    }
                    if (promotedStruct != null && isFrameworkLocal)
                        structs.Add(promotedStruct);
                    break;
            }
        }

        // Pass 2: Merge categories onto their owning classes.
        // Merge onto ALL matching duplicates so Pass 3 dedup doesn't discard category members.
        // Also merge category-adopted protocols onto the class's ProtocolNames.
        foreach (var cat in categories)
        {
            var taggedMethods = cat.Methods.Select(m => m with { IsFromCategory = true, CategoryName = cat.CategoryName }).ToList();
            var taggedProperties = cat.Properties.Select(p => p with { IsFromCategory = true, CategoryName = cat.CategoryName }).ToList();
            for (int i = 0; i < classes.Count; i++)
            {
                if (classes[i].Name == cat.ClassName)
                {
                    var mergedProtocols = classes[i].ProtocolNames;
                    if (cat.ProtocolNames.Count > 0)
                    {
                        var allProtos = new HashSet<string>(classes[i].ProtocolNames);
                        foreach (var p in cat.ProtocolNames) allProtos.Add(p);
                        mergedProtocols = allProtos.ToList();
                    }
                    classes[i] = classes[i] with
                    {
                        Methods = [.. classes[i].Methods, .. taggedMethods],
                        Properties = [.. classes[i].Properties, .. taggedProperties],
                        ProtocolNames = mergedProtocols
                    };
                }
            }
            // If class not found (forward-declared in another framework), skip category
        }

        // Pass 3: Deduplicate declarations by name.
        // The same type can appear in multiple headers (public + internal, or multiple umbrella includes).
        // Enums/structs: keep richest (most cases/fields) since empty forward-like decls precede full defs.
        // Classes/protocols: merge metadata (superclass, protocols, availability, generic params)
        //   from all duplicates onto the richest (most methods+properties).
        // Functions/constants/typedefs: keep first (no richness variation).
        enums = DeduplicateByRichest(enums, e => e.Name, e => e.Cases.Count);
        structs = DeduplicateByRichest(structs, s => s.Name, s => s.Fields.Count);
        classes = MergeClasses(classes);
        protocols = MergeProtocols(protocols);
        functions = DeduplicateByFirst(functions, f => f.Name);
        constants = DeduplicateByFirst(constants, c => c.Name);
        typedefs = DeduplicateByFirst(typedefs, t => t.Name);

        // Pass 4: Deduplicate categories by (ClassName, CategoryName).
        // Same category can appear through umbrella + public header.
        var dedupedCategories = MergeCategories(categories);

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
            Typedefs = typedefs,
            // System typedefs first, framework-local second — BuildResolvedTypedefMap uses
            // last-write-wins dict assignment, so framework-local definitions take precedence
            // when a system header defines the same alias name.
            ResolutionTypedefs = [.. systemTypedefs, .. typedefs],
            Categories = dedupedCategories,
            AppleSdkTypeNames = appleSdkTypeNames.Count > 0 ? appleSdkTypeNames : null
        };

        } // try
        finally
        {
            ObjCTypeRefParser.SetAdditionalGenericContainers(null);
        }
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

        // Extract ObjC lightweight generic type parameters (e.g., RLMObjectType in RLMResults<RLMObjectType>)
        var genericTypeParamNames = new List<string>();
        if (element.TryGetProperty("inner", out var innerForParams))
        {
            foreach (var child in innerForParams.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "ObjCTypeParamDecl")
                {
                    var paramName = GetName(child);
                    if (paramName != null)
                        genericTypeParamNames.Add(paramName);
                }
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();
        var availability = new List<ObjCAvailability>();

        ParseContainerChildren(element, methods, properties, availability, isProtocol: false);

        var swiftName = ExtractSwiftName(element);
        var (docComment, _) = ExtractDocComment(element);

        return new ObjCClassDecl
        {
            Name = name,
            SuperclassName = superclass,
            ProtocolNames = protocols,
            GenericTypeParamNames = genericTypeParamNames,
            Methods = methods,
            Properties = properties,
            Availability = availability,
            SwiftName = swiftName,
            DocComment = docComment
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

        var (docComment, _) = ExtractDocComment(element);

        return new ObjCProtocolDecl
        {
            Name = name,
            InheritedProtocolNames = inherited,
            Methods = methods,
            Properties = properties,
            Availability = availability,
            DocComment = docComment
        };
    }

    private static ObjCCategoryDecl? ParseCategoryDecl(JsonElement element)
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

        // Category name: null from AST means unnamed category (class extension) → normalize to ""
        var categoryName = GetName(element) ?? "";

        // Extract protocols adopted by this category
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

        return new ObjCCategoryDecl
        {
            CategoryName = categoryName,
            ClassName = className,
            ProtocolNames = protocols,
            Methods = methods,
            Properties = properties,
            Availability = availability
        };
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

        var swiftName = ExtractSwiftName(element);
        var (docComment, _) = ExtractDocComment(element);

        return new ObjCEnumDecl
        {
            Name = name,
            IsOptions = isOptions,
            UnderlyingType = underlyingType,
            Cases = cases,
            Availability = availability,
            SwiftName = swiftName,
            DocComment = docComment
        };
    }

    private static ObjCStructDecl? ParseStructDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var (fields, hasUnsafeLayout, unsafeReason) = ParseStructFieldsWithLayout(element);
        return new ObjCStructDecl { Name = name, Fields = fields, HasUnsafeLayout = hasUnsafeLayout, UnsafeLayoutReason = unsafeReason };
    }

    private static List<ObjCStructField> ParseStructFields(JsonElement element)
    {
        var (fields, _, _) = ParseStructFieldsWithLayout(element);
        return fields;
    }

    private static (List<ObjCStructField> fields, bool hasUnsafeLayout, string? unsafeReason) ParseStructFieldsWithLayout(JsonElement element)
    {
        var fields = new List<ObjCStructField>();
        var unsafeReasons = new List<string>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var kind = GetOptionalString(child, "kind");

                if (kind == "FieldDecl")
                {
                    // Detect bitfield: clang AST emits "isBitfield": true on FieldDecl
                    if (child.TryGetProperty("isBitfield", out var isBitfield) && isBitfield.GetBoolean())
                    {
                        unsafeReasons.Add("contains bitfield");
                        continue;
                    }

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
                else if (kind == "RecordDecl")
                {
                    // Anonymous union/struct inside the struct
                    var memberName = GetName(child);
                    if (memberName == null)
                        unsafeReasons.Add("contains anonymous union/struct");
                }
            }
        }

        var hasUnsafe = unsafeReasons.Count > 0;
        var reason = hasUnsafe ? string.Join(", ", unsafeReasons.Distinct()) : null;
        return (fields, hasUnsafe, reason);
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

        var availability = new List<ObjCAvailability>();
        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "AvailabilityAttr")
                {
                    var avail = ParseAvailability(child);
                    if (avail != null)
                        availability.Add(avail);
                }
            }
        }

        return new ObjCConstantDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsExtern = isExtern,
            Availability = availability
        };
    }

    private static (ObjCTypedefDecl?, ObjCStructDecl?) ParseTypedefDecl(JsonElement element, List<ObjCStructField>? precedingAnonymousFields = null, bool precedingHasUnsafeLayout = false, string? precedingUnsafeReason = null)
    {
        var name = GetName(element);
        if (name == null) return (null, null);

        // Get the underlying type from inner or type
        string? underlyingQualType = null;
        ObjCStructDecl? promotedStruct = null;

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");

                // Check for anonymous struct (RecordDecl with fields) inside typedef's inner
                if (childKind == "RecordDecl")
                {
                    var (fields, hasUnsafe, unsafeReason) = ParseStructFieldsWithLayout(child);
                    if (fields.Count > 0 || hasUnsafe)
                        promotedStruct = new ObjCStructDecl { Name = name, Fields = fields, HasUnsafeLayout = hasUnsafe, UnsafeLayoutReason = unsafeReason };
                }

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

        // Promote anonymous struct from preceding sibling RecordDecl
        // (clang emits anonymous struct as top-level sibling, then typedef referencing it)
        if (promotedStruct == null && (precedingAnonymousFields is { Count: > 0 } || precedingHasUnsafeLayout))
        {
            var qualType = GetQualType(element);
            if (qualType != null && qualType.StartsWith("struct ", StringComparison.Ordinal))
                promotedStruct = new ObjCStructDecl { Name = name, Fields = precedingAnonymousFields ?? [], HasUnsafeLayout = precedingHasUnsafeLayout, UnsafeLayoutReason = precedingUnsafeReason };
        }

        // Fall back to the type property
        underlyingQualType ??= GetQualType(element);
        if (underlyingQualType == null) return (null, promotedStruct);

        var typedefDecl = new ObjCTypedefDecl
        {
            Name = name,
            UnderlyingType = ObjCTypeRefParser.Parse(underlyingQualType)
        };
        return (typedefDecl, promotedStruct);
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

        var swiftName = ExtractSwiftName(element);
        var isRefined = HasSwiftPrivateAttr(element);
        var (docComment, docParams) = ExtractDocComment(element);

        return new ObjCMethodDecl
        {
            Selector = name,
            ReturnType = ObjCTypeRefParser.Parse(returnQualType),
            Parameters = parameters,
            IsInstanceMethod = isInstance,
            IsOptional = isOptional,
            Availability = methodAvailability,
            SwiftName = swiftName,
            IsRefinedForSwift = isRefined,
            DocComment = docComment,
            DocParams = docParams
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

        var swiftName = ExtractSwiftName(element);
        var isRefined = HasSwiftPrivateAttr(element);
        var (docComment, _) = ExtractDocComment(element);

        return new ObjCPropertyDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsReadonly = isReadonly,
            IsClass = isClass,
            IsOptional = isOptional,
            GetterSelector = getter,
            SetterSelector = setter,
            Availability = propAvailability,
            SwiftName = swiftName,
            IsRefinedForSwift = isRefined,
            DocComment = docComment
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

    /// <summary>
    /// Extracts doc comments from a FullComment node in a declaration's inner nodes.
    /// Clang includes FullComment > ParagraphComment > TextComment for description text,
    /// and ParamCommandComment > ParagraphComment > TextComment for @param docs.
    /// </summary>
    private static (string? summary, List<ObjCDocParam> docParams) ExtractDocComment(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return (null, []);

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") != "FullComment")
                continue;
            if (!child.TryGetProperty("inner", out var commentInner))
                continue;

            var summaryParts = new List<string>();
            var docParams = new List<ObjCDocParam>();

            foreach (var commentChild in commentInner.EnumerateArray())
            {
                var kind = GetOptionalString(commentChild, "kind");
                switch (kind)
                {
                    case "ParagraphComment":
                        var text = ExtractParagraphText(commentChild);
                        if (!string.IsNullOrWhiteSpace(text))
                            summaryParts.Add(text.Trim());
                        break;

                    case "BlockCommandComment":
                        // @return / @brief etc — treat as summary text
                        if (commentChild.TryGetProperty("inner", out var blockInner))
                        {
                            foreach (var blockChild in blockInner.EnumerateArray())
                            {
                                if (GetOptionalString(blockChild, "kind") == "ParagraphComment")
                                {
                                    var blockText = ExtractParagraphText(blockChild);
                                    if (!string.IsNullOrWhiteSpace(blockText))
                                        summaryParts.Add(blockText.Trim());
                                }
                            }
                        }
                        break;

                    case "ParamCommandComment":
                        var paramName = GetOptionalString(commentChild, "param");
                        if (paramName != null && commentChild.TryGetProperty("inner", out var paramInner))
                        {
                            foreach (var paramChild in paramInner.EnumerateArray())
                            {
                                if (GetOptionalString(paramChild, "kind") == "ParagraphComment")
                                {
                                    var paramText = ExtractParagraphText(paramChild);
                                    if (!string.IsNullOrWhiteSpace(paramText))
                                        docParams.Add(new ObjCDocParam { Name = paramName, Description = paramText.Trim() });
                                }
                            }
                        }
                        break;
                }
            }

            var summary = summaryParts.Count > 0 ? string.Join(" ", summaryParts) : null;
            return (summary, docParams);
        }

        return (null, []);
    }

    private static string ExtractParagraphText(JsonElement paragraph)
    {
        if (!paragraph.TryGetProperty("inner", out var inner))
            return "";

        var parts = new List<string>();
        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "TextComment")
            {
                var text = GetOptionalString(child, "text");
                if (text != null)
                    parts.Add(text);
            }
        }
        return string.Join("", parts);
    }

    /// <summary>
    /// Extracts the NS_SWIFT_NAME value from a declaration's inner nodes.
    /// Clang represents this as a SwiftNameAttr node with a "name" property.
    /// </summary>
    private static string? ExtractSwiftName(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return null;

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "SwiftNameAttr")
                return GetOptionalString(child, "name");
        }
        return null;
    }

    /// <summary>
    /// Checks if a declaration has the NS_REFINED_FOR_SWIFT attribute.
    /// Clang represents this as a SwiftPrivateAttr inner node.
    /// </summary>
    private static bool HasSwiftPrivateAttr(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return false;

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "SwiftPrivateAttr")
                return true;
        }
        return false;
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
        => IsPublicDeclaration(decl, frameworkHeadersPath, ref currentFile, out _);

    internal static bool IsPublicDeclaration(JsonElement decl, string frameworkHeadersPath, ref string? currentFile, out string? resolvedFilePath)
    {
        resolvedFilePath = null;
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
        // includedFrom identifies the INCLUDING file, not the declaration's source.
        // We use it as a heuristic: if BOTH the includer AND the current file chain
        // point to framework headers, the declaration is from a sub-header (e.g.,
        // CBCentralManager.h included by CoreBluetooth.h). We additionally require
        // currentFile to be framework-local (or null) to avoid false positives when
        // a framework header #imports an SDK header — the SDK declarations get
        // includedFrom pointing to the framework header but currentFile points to
        // the SDK header (set by the first declaration in that file via step 1).
        bool hasIncludedFrom = loc.TryGetProperty("includedFrom", out var inclFrom);
        if (resolvedFile == null && hasIncludedFrom)
        {
            if (TryGetLocFile(inclFrom, "file", out f) && IsUnderPath(f, frameworkHeadersPath)
                && (currentFile == null || IsUnderPath(currentFile, frameworkHeadersPath)))
            {
                resolvedFile = f;
            }
            // If includedFrom points outside our framework, resolvedFile stays null
            // and we do NOT fall through to currentFile inheritance below.
        }

        // 5. If no file field at all and no includedFrom, inherit from previous declaration.
        // (Clang omits loc.file when consecutive declarations are in the same file.)
        if (resolvedFile == null && !hasIncludedFrom)
            resolvedFile = currentFile;

        resolvedFilePath = resolvedFile ?? currentFile;

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

    /// <summary>
    /// Checks whether a header path is from an Apple SDK (Xcode SDKs, system includes).
    /// Types declared in Apple SDK headers are available in .NET iOS via framework bindings.
    /// </summary>
    internal static bool IsAppleSdkPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        return filePath.Contains("/SDKs/", StringComparison.Ordinal)
            || filePath.Contains("/usr/include/", StringComparison.Ordinal)
            || filePath.Contains("/Platforms/", StringComparison.Ordinal);
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

    /// <summary>
    /// Quick pre-scan of the AST to find ObjC class names that declare lightweight generic
    /// type parameters (ObjCTypeParamDecl). These names supplement the static
    /// KnownGenericContainers set so the type ref parser can distinguish custom generic
    /// containers (RLMResults&lt;ObjectType&gt;) from protocol-qualified types (NSObject&lt;NSCopying&gt;).
    /// </summary>
    private static HashSet<string> ScanGenericContainerNames(JsonElement inner)
    {
        var result = new HashSet<string>();
        foreach (var node in inner.EnumerateArray())
        {
            if (GetOptionalString(node, "kind") != "ObjCInterfaceDecl")
                continue;
            if (!node.TryGetProperty("inner", out var children))
                continue;
            foreach (var child in children.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "ObjCTypeParamDecl")
                {
                    var className = GetName(node);
                    if (className != null)
                        result.Add(className);
                    break; // One type param is enough to know it's generic
                }
            }
        }
        return result;
    }

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

    private static List<T> DeduplicateByRichest<T>(
        List<T> items, Func<T, string> nameSelector, Func<T, int> richnessSelector)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector)
            .Select(g => g.OrderByDescending(richnessSelector).First())
            .ToList();
    }

    /// <summary>
    /// Deduplicates classes by name, merging metadata from all duplicates onto the richest instance.
    /// Metadata includes: SuperclassName, ProtocolNames, GenericTypeParamNames, Availability.
    /// NOTE: Methods/properties are NOT merged across duplicates — only the richest instance's
    /// members are kept. In practice, duplicate declarations of the same class come from the same
    /// header definition (re-included via umbrella headers), so they have identical members.
    /// Disjoint members only arise from categories, which are handled in Pass 2 before dedup.
    /// </summary>
    private static List<ObjCClassDecl> MergeClasses(List<ObjCClassDecl> classes)
    {
        if (classes.Count <= 1) return classes;
        return classes.GroupBy(c => c.Name)
            .Select(g =>
            {
                var richest = g.OrderByDescending(c => c.Methods.Count + c.Properties.Count).First();
                if (g.Count() == 1) return richest;

                // Merge metadata from all duplicates
                string? superclass = richest.SuperclassName;
                var allProtocols = new HashSet<string>(richest.ProtocolNames);
                var allGenericParams = new HashSet<string>(richest.GenericTypeParamNames);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    superclass ??= dup.SuperclassName;
                    foreach (var p in dup.ProtocolNames) allProtocols.Add(p);
                    foreach (var gp in dup.GenericTypeParamNames) allGenericParams.Add(gp);
                    foreach (var a in dup.Availability)
                    {
                        if (!allAvailability.Any(existing =>
                            existing.Platform == a.Platform && existing.IntroducedVersion == a.IntroducedVersion
                            && existing.DeprecatedVersion == a.DeprecatedVersion))
                            allAvailability.Add(a);
                    }
                }

                return richest with
                {
                    SuperclassName = superclass,
                    ProtocolNames = allProtocols.ToList(),
                    GenericTypeParamNames = allGenericParams.ToList(),
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    /// <summary>
    /// Deduplicates protocols by name, merging metadata from all duplicates onto the richest instance.
    /// Metadata includes: InheritedProtocolNames, Availability.
    /// NOTE: Methods/properties are NOT merged — same rationale as MergeClasses.
    /// </summary>
    private static List<ObjCProtocolDecl> MergeProtocols(List<ObjCProtocolDecl> protocols)
    {
        if (protocols.Count <= 1) return protocols;
        return protocols.GroupBy(p => p.Name)
            .Select(g =>
            {
                var richest = g.OrderByDescending(p => p.Methods.Count + p.Properties.Count).First();
                if (g.Count() == 1) return richest;

                var allInherited = new HashSet<string>(richest.InheritedProtocolNames);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    foreach (var ip in dup.InheritedProtocolNames) allInherited.Add(ip);
                    foreach (var a in dup.Availability)
                    {
                        if (!allAvailability.Any(existing =>
                            existing.Platform == a.Platform && existing.IntroducedVersion == a.IntroducedVersion
                            && existing.DeprecatedVersion == a.DeprecatedVersion))
                            allAvailability.Add(a);
                    }
                }

                return richest with
                {
                    InheritedProtocolNames = allInherited.ToList(),
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    /// <summary>
    /// Deduplicates categories by (ClassName, CategoryName), merging members from all duplicates
    /// onto the richest instance (most methods+properties). Same pattern as MergeClasses.
    /// </summary>
    private static List<ObjCCategoryDecl> MergeCategories(List<ObjCCategoryDecl> categories)
    {
        if (categories.Count <= 1) return categories;
        return categories.GroupBy(c => (c.ClassName, c.CategoryName))
            .Select(g =>
            {
                var richest = g.OrderByDescending(c => c.Methods.Count + c.Properties.Count).First();
                if (g.Count() == 1) return richest;

                var allProtocols = new HashSet<string>(richest.ProtocolNames);
                var allMethodSelectors = new HashSet<string>(richest.Methods.Select(m => m.Selector));
                var allMethods = new List<ObjCMethodDecl>(richest.Methods);
                var allPropertyNames = new HashSet<string>(richest.Properties.Select(p => p.Name));
                var allProperties = new List<ObjCPropertyDecl>(richest.Properties);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    foreach (var p in dup.ProtocolNames) allProtocols.Add(p);
                    foreach (var m in dup.Methods)
                    {
                        if (allMethodSelectors.Add(m.Selector))
                            allMethods.Add(m);
                    }
                    foreach (var p in dup.Properties)
                    {
                        if (allPropertyNames.Add(p.Name))
                            allProperties.Add(p);
                    }
                    foreach (var a in dup.Availability)
                    {
                        if (!allAvailability.Any(existing =>
                            existing.Platform == a.Platform && existing.IntroducedVersion == a.IntroducedVersion
                            && existing.DeprecatedVersion == a.DeprecatedVersion))
                            allAvailability.Add(a);
                    }
                }

                return richest with
                {
                    ProtocolNames = allProtocols.ToList(),
                    Methods = allMethods,
                    Properties = allProperties,
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    private static List<T> DeduplicateByFirst<T>(
        List<T> items, Func<T, string> nameSelector)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector).Select(g => g.First()).ToList();
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
