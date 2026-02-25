// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// Parses a .swiftinterface file to extract access levels for declarations
/// that are ambiguous in the ABI JSON.
///
/// Problem: @inlinable internal declarations with explicit access control
/// (declAttributes: [AccessControl, Inlinable]) are indistinguishable from
/// @inlinable public declarations in the ABI JSON. The swiftinterface is
/// the only reliable source for the actual access level.
///
/// This parser extracts a set of "TypeName.printedName" keys for all
/// internal members, which can then be cross-referenced during ABI parsing
/// to correctly mark these declarations as module-internal.
///
/// Limitation: keys are unqualified ("AES.encrypt(block:)"), not module-qualified.
/// This is safe because a single swiftinterface covers one module, and the ABI
/// parser also processes one module at a time with unqualified parentDecl.Name.
/// </summary>
public static class SwiftInterfaceAccessParser
{
    // Regex for type declarations: matches class/struct/enum/actor/protocol
    // with optional attributes, access modifiers, and 'final' keyword.
    private static readonly Regex TypeDeclRegex = new(
        @"(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for extension declarations: matches "extension Module.Type" and
    // extracts the full qualified name. The unqualified type name is extracted
    // separately by taking the last dot-component.
    // Handles: extension Mod.Type {, extension Mod.Type : Proto {, extension Mod.Type where ... {
    private static readonly Regex ExtensionDeclRegex = new(
        @"extension\s+([\w.]+)",
        RegexOptions.Compiled);

    // Regex for internal func declarations
    private static readonly Regex InternalFuncRegex = new(
        @"internal\s+(?:final\s+)?(?:static\s+)?func\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    // Regex for internal var/let declarations
    private static readonly Regex InternalVarRegex = new(
        @"internal\s+(?:final\s+)?(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for internal init declarations
    private static readonly Regex InternalInitRegex = new(
        @"internal\s+(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    // Regex for public/open type declarations (excludes internal)
    private static readonly Regex PublicTypeDeclRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for @MainActor annotation (fully-qualified or bare)
    private static readonly Regex MainActorAnnotationRegex = new(
        @"@(?:_Concurrency\.)?MainActor",
        RegexOptions.Compiled);

    // Regex for actor declarations: "public actor Name" or "open actor Name"
    private static readonly Regex ActorDeclRegex = new(
        @"(?:public|open)\s+actor\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for nonisolated member declarations
    private static readonly Regex NonisolatedRegex = new(
        @"nonisolated\s+(?:public|open|final|var|let|func|static|class)",
        RegexOptions.Compiled);

    // Regex for public/open func declarations (for member-level actor isolation detection)
    private static readonly Regex PublicFuncRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for public/open var/let declarations (for member-level actor isolation detection)
    private static readonly Regex PublicVarRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for public/open init declarations
    private static readonly Regex PublicInitRegex = new(
        @"(?:public|open)\s+(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a set of dot-qualified type paths
    /// declared as public or open (e.g., "OrderContainer.Status" for nested types,
    /// "ConstraintMaker" for top-level types).
    /// Types NOT in this set are internal to the module.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Set of public type names, or empty set if parsing fails.</returns>
    public static HashSet<string> GetPublicTypeNames(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for public/open type declarations
            bool pushedScope = false;
            var publicTypeMatch = PublicTypeDeclRegex.Match(trimmed);
            if (publicTypeMatch.Success && openBraces > 0)
            {
                var typeName = publicTypeMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                // Build dot-qualified path from the type stack
                var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                result.Add(qualifiedPath);
            }

            // Also track non-public type declarations (internal types that open a scope)
            // so we can properly track brace depth and nesting
            if (!pushedScope)
            {
                var anyTypeMatch = TypeDeclRegex.Match(trimmed);
                if (anyTypeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((anyTypeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            // Track extensions — but do NOT add extension targets to the public type set.
            // Extensions are for external module types (e.g., "extension Swift.Int : ...")
            // and should not be treated as types defined in this module.
            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a set of type names annotated with @MainActor / @_Concurrency.MainActor.
    /// Does NOT include custom actor declarations (those need different wrapper treatment).
    /// Type names use dot-qualified paths (e.g., "Outer.Inner" for nested types).
    /// </summary>
    public static HashSet<string> GetMainActorTypes(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        bool pendingMainActor = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for @MainActor annotation on this line or pending from previous line
            bool hasMainActor = pendingMainActor || MainActorAnnotationRegex.IsMatch(trimmed);
            pendingMainActor = false;

            // If this line has @MainActor but no declaration, it's a pending annotation
            if (hasMainActor && !TypeDeclRegex.IsMatch(trimmed) && openBraces == 0)
            {
                pendingMainActor = true;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Check for type declarations
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                var typeName = typeMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                // If this type has @MainActor and is NOT an actor keyword declaration
                if (hasMainActor && !ActorDeclRegex.IsMatch(trimmed))
                {
                    var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                    result.Add(qualifiedPath);
                }
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Returns a set of type names declared with the 'actor' keyword (custom actors).
    /// Custom actors have implicit isolation to their own executor, NOT MainActor.
    /// </summary>
    public static HashSet<string> GetCustomActorTypes(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            bool pushedScope = false;
            var actorMatch = ActorDeclRegex.Match(trimmed);
            if (actorMatch.Success && openBraces > 0)
            {
                var typeName = actorMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                result.Add(qualifiedPath);
            }

            if (!pushedScope)
            {
                var typeMatch = TypeDeclRegex.Match(trimmed);
                if (typeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Returns a set of "QualifiedType.printedName" keys for members that are individually
    /// @MainActor-annotated (when the containing type is NOT globally @MainActor).
    /// Function keys use printed name format (e.g., "Outer.Inner.foo(_:bar:)") to distinguish overloads.
    /// Property keys use "QualifiedType.propName".
    /// Uses qualified type paths from the type stack to avoid nested-type name collisions.
    /// Handles multi-line function signatures via continuation buffer.
    /// </summary>
    public static HashSet<string> GetActorIsolatedMembers(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        bool pendingMainActor = false;
        // Multi-line continuation: (accumulated line, wasMainActor)
        (string Line, bool IsMainActor)? continuation = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuation != null)
            {
                var accumulated = continuation.Value.Line + " " + trimmed;
                if (!HasUnmatchedOpenParen(accumulated))
                {
                    // Signature complete — process the full line
                    var wasMainActor = continuation.Value.IsMainActor;
                    continuation = null;
                    if (wasMainActor && typeStack.Count > 0)
                        ProcessActorIsolatedMember(accumulated, typeStack, result);
                }
                else
                {
                    continuation = (accumulated, continuation.Value.IsMainActor);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for @MainActor annotation
            bool hasMainActor = pendingMainActor || MainActorAnnotationRegex.IsMatch(trimmed);
            pendingMainActor = false;

            // Check for pending annotation (attribute on its own line)
            if (hasMainActor && !TypeDeclRegex.IsMatch(trimmed) &&
                !PublicFuncRegex.IsMatch(trimmed) && !PublicVarRegex.IsMatch(trimmed) &&
                !PublicInitRegex.IsMatch(trimmed) && openBraces == 0)
            {
                pendingMainActor = true;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Track type context
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for member-level @MainActor (only within a type context)
            if (hasMainActor && typeStack.Count > 0 && !pushedScope)
            {
                // Check for multi-line signature
                if ((PublicFuncRegex.IsMatch(trimmed) || PublicInitRegex.IsMatch(trimmed)) &&
                    HasUnmatchedOpenParen(trimmed))
                {
                    continuation = (trimmed, true);
                }
                else
                {
                    ProcessActorIsolatedMember(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Processes a single line for actor-isolated member detection and adds the key to the result set.
    /// </summary>
    private static void ProcessActorIsolatedMember(
        string line, Stack<(string Name, int Depth)> typeStack, HashSet<string> result)
    {
        var qualifiedType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        var funcMatch = PublicFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        var varMatch = PublicVarRegex.Match(line);
        if (varMatch.Success)
        {
            result.Add($"{qualifiedType}.{varMatch.Groups[1].Value}");
            return;
        }

        if (PublicInitRegex.IsMatch(line))
        {
            var printedName = ExtractPrintedName(line, "init");
            result.Add($"{qualifiedType}.{printedName}");
        }
    }

    /// <summary>
    /// Returns a set of "QualifiedType.printedName" keys for members declared as nonisolated.
    /// These members opt out of their containing type's actor isolation.
    /// Function keys use printed name format (e.g., "Outer.Inner.foo(_:bar:)") to distinguish overloads.
    /// Property keys use "QualifiedType.propName".
    /// Uses qualified type paths from the type stack to avoid nested-type name collisions.
    /// Handles multi-line function signatures via continuation buffer.
    /// </summary>
    public static HashSet<string> GetNonisolatedMembers(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessNonisolatedMember(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for nonisolated members (only within a type context)
            if (typeStack.Count > 0 && NonisolatedRegex.IsMatch(trimmed))
            {
                // Check for multi-line signature
                if ((AnyFuncRegex.IsMatch(trimmed) || AnyInitRegex.IsMatch(trimmed)) &&
                    HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessNonisolatedMember(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Processes a single line for nonisolated member detection and adds the key to the result set.
    /// </summary>
    private static void ProcessNonisolatedMember(
        string line, Stack<(string Name, int Depth)> typeStack, HashSet<string> result)
    {
        var qualifiedType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        if (AnyInitRegex.IsMatch(line))
        {
            var printedName = ExtractPrintedName(line, "init");
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        // Try var/let match
        var varMatch = Regex.Match(line, @"nonisolated\s+(?:public\s+|open\s+)?(?:final\s+)?(?:var|let)\s+(\w+)");
        if (varMatch.Success)
            result.Add($"{qualifiedType}.{varMatch.Groups[1].Value}");
    }

    // Regex for conformance extension: "extension Module.Type : Module.Protocol {"
    private static readonly Regex ConformanceExtensionRegex = new(
        @"extension\s+([\w.]+)\s*:\s*([\w.,\s]+)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping protocol names
    /// to their conforming type names, as declared in extension conformance blocks.
    /// Only includes conformances from empty extension bodies (the conforming type
    /// adds no new members — a signal of a marker protocol conformance).
    /// Keys are unqualified protocol names (e.g., "ConstraintOffsetTarget").
    /// Values are lists of fully-qualified Swift type names (e.g., "Swift.Int").
    /// </summary>
    public static Dictionary<string, List<string>> GetMarkerProtocolConformances(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Pass 1: Collect conformances from "extension Type : Protocol { }" blocks
        // We look for extensions with an empty body (open+close on same line or next line is })
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var trimmed = lines[lineIdx].TrimStart();
            var match = ConformanceExtensionRegex.Match(trimmed);
            if (!match.Success)
                continue;

            var conformingType = match.Groups[1].Value;
            var protocolList = match.Groups[2].Value;

            // Check for empty body: either "{ }" on same line or next non-empty line is "}"
            var (openBraces, closeBraces) = CountBraces(lines[lineIdx]);
            bool isEmptyBody = openBraces > 0 && closeBraces > 0; // "{ }" on same line

            if (!isEmptyBody && openBraces > 0)
            {
                // Check if next non-whitespace line is "}"
                for (int nextIdx = lineIdx + 1; nextIdx < lines.Length; nextIdx++)
                {
                    var nextTrimmed = lines[nextIdx].TrimStart();
                    if (string.IsNullOrWhiteSpace(nextTrimmed))
                        continue;
                    if (nextTrimmed == "}")
                        isEmptyBody = true;
                    break;
                }
            }

            if (!isEmptyBody)
                continue;

            // Parse protocol list (handles "Proto1, Proto2")
            var protocols = protocolList.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p));

            foreach (var proto in protocols)
            {
                // Use unqualified protocol name as key
                var dotIdx = proto.LastIndexOf('.');
                var unqualifiedName = dotIdx >= 0 ? proto.Substring(dotIdx + 1) : proto;

                if (!result.ContainsKey(unqualifiedName))
                    result[unqualifiedName] = new List<string>();

                if (!result[unqualifiedName].Contains(conformingType))
                    result[unqualifiedName].Add(conformingType);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a set of member keys that are
    /// declared as internal. Keys are formatted as "TypeName.printedName"
    /// (e.g., "AES.encrypt(block:)").
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Set of internal member keys, or empty set if parsing fails.</returns>
    public static HashSet<string> GetInternalMembers(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Track type context using a stack with associated brace depths
        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Count braces on this line (outside of string literals)
            var (openBraces, closeBraces) = CountBraces(line);

            // Check for type declarations before updating brace depth.
            // Both nominal types (class/struct/enum) and extensions push a scope.
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            // Check for extension declarations (e.g., "extension CryptoSwift.AES {")
            // Extensions can contain internal members that belong to the extended type.
            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Extract unqualified type name: "CryptoSwift.AES" → "AES"
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for internal member declarations (only within a type context)
            if (typeStack.Count > 0 && trimmed.Contains("internal "))
            {
                var currentType = typeStack.Peek().Name;

                // Check for internal func
                var funcMatch = InternalFuncRegex.Match(trimmed);
                if (funcMatch.Success)
                {
                    var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
                    result.Add($"{currentType}.{printedName}");
                }

                // Check for internal var/let
                var varMatch = InternalVarRegex.Match(trimmed);
                if (varMatch.Success)
                {
                    result.Add($"{currentType}.{varMatch.Groups[1].Value}");
                }

                // Check for internal init
                var initMatch = InternalInitRegex.Match(trimmed);
                if (initMatch.Success)
                {
                    var printedName = ExtractPrintedName(line, "init");
                    result.Add($"{currentType}.{printedName}");
                }
            }

            // Update brace depth
            braceDepth += openBraces - closeBraces;

            // Pop types whose scope has closed
            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Counts opening and closing braces in a line, ignoring those inside string literals.
    /// </summary>
    private static (int Open, int Close) CountBraces(string line)
    {
        int open = 0, close = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString)
            {
                if (c == '{') open++;
                if (c == '}') close++;
            }
        }

        return (open, close);
    }

    /// <summary>
    /// Extracts a printed name in ABI format (e.g., "encrypt(block:)") from a Swift function
    /// declaration line. Parses parameter labels from the function signature.
    /// </summary>
    private static string ExtractPrintedName(string line, string funcName)
    {
        // Find the opening parenthesis after the function name
        var funcNameIdx = line.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName} (", StringComparison.Ordinal);
        // Handle generic funcs: "func name<T>("
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return $"{funcName}()";

        var parenStart = line.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return $"{funcName}()";

        // Find matching close paren, handling nested parens
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
        if (string.IsNullOrWhiteSpace(paramStr))
            return $"{funcName}()";

        // Extract external labels from parameter list
        var labels = new List<string>();
        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            // Pattern: "externalLabel internalName: Type" or "_ internalName: Type" or "name: Type"
            var colonIdx = trimPart.IndexOf(':');
            if (colonIdx < 0) continue;
            var beforeColon = trimPart.Substring(0, colonIdx).Trim();
            // Split by whitespace — first token is the external label
            var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                labels.Add(words[0]);
            }
        }

        if (labels.Count == 0)
            return $"{funcName}()";

        return $"{funcName}({string.Join(":", labels)}:)";
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

    // Regex for enum case declarations with associated values
    // Matches: case caseName(label: Type) or case caseName(Type) or case caseName(label: Type, label2: Type2)
    private static readonly Regex EnumCaseRegex = new(
        @"case\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.caseName" keys to lists of parameter labels.
    /// Labels are null for unlabeled parameters (e.g., "case point(FrozenPoint)").
    ///
    /// For example, for:
    ///   case circle(radius: Swift.Double)
    ///   case point(SwiftBindingsTestLib.FrozenPoint)
    /// This produces:
    ///   { "Shape.circle": ["radius"], "Shape.point": [null] }
    /// </summary>
    public static Dictionary<string, List<string?>> GetEnumCaseLabels(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string?>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line case continuation (rare but possible with many associated values)
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessEnumCaseLine(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as other methods)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for enum case declarations with parentheses
            // Also handle "indirect case" which appears in recursive enums
            var caseLine = trimmed;
            if (caseLine.StartsWith("indirect "))
                caseLine = caseLine.Substring("indirect ".Length);
            if (caseLine.StartsWith("case ") && caseLine.Contains("("))
            {
                if (HasUnmatchedOpenParen(caseLine))
                {
                    continuationLine = caseLine;
                }
                else
                {
                    ProcessEnumCaseLine(caseLine, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a complete enum case line to extract parameter labels.
    /// </summary>
    private static void ProcessEnumCaseLine(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, List<string?>> result)
    {
        if (typeStack.Count == 0)
            return;

        var caseMatch = EnumCaseRegex.Match(line);
        if (!caseMatch.Success)
            return;

        var caseName = caseMatch.Groups[1].Value;

        // Build fully-qualified type path from the type stack to disambiguate
        // nested enums with the same local name (e.g., OrderContainer.Status vs PaymentContainer.Status)
        var currentType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        // Find the opening parenthesis
        var parenStart = line.IndexOf('(', caseMatch.Index);
        if (parenStart < 0)
            return;

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
        if (string.IsNullOrWhiteSpace(paramStr))
            return;

        var labels = new List<string?>();
        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            var colonIdx = trimPart.IndexOf(':');
            if (colonIdx < 0)
            {
                // No colon — unlabeled parameter (e.g., "SwiftBindingsTestLib.FrozenPoint")
                labels.Add(null);
            }
            else
            {
                var beforeColon = trimPart.Substring(0, colonIdx).Trim();
                // The label is the text before the colon (e.g., "radius" from "radius: Swift.Double")
                // For "_" labels, treat as unlabeled
                if (beforeColon == "_")
                    labels.Add(null);
                else
                    labels.Add(beforeColon);
            }
        }

        if (labels.Count > 0)
        {
            result[$"{currentType}.{caseName}"] = labels;
        }
    }

    // Regex for typed throws: captures the error type from "throws(Module.Type)"
    private static readonly Regex TypedThrowsRegex = new(
        @"throws\(([^)]+)\)",
        RegexOptions.Compiled);

    // Regex for any func declaration (public, open, or no access modifier in extension scope)
    // Captures the function name. Handles static, class, final, mutating modifiers.
    private static readonly Regex AnyFuncRegex = new(
        @"(?:(?:public|open|internal)\s+)?(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for init declarations
    private static readonly Regex AnyInitRegex = new(
        @"(?:(?:public|open|internal)\s+)?(?:convenience\s+)?init\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.printedName" keys to lists of internal parameter names.
    /// For module-level free functions, the key is just "printedName".
    ///
    /// For example, for:
    ///   public func sumTwo(_ a: Int, _ b: Int) -> Int
    /// This produces: { "sumTwo(_:_:)": ["a", "b"] }
    ///
    /// Multi-line signatures are handled by detecting unmatched parentheses.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Dictionary of parameter name lists keyed by "TypeName.printedName" or "printedName".</returns>
    public static Dictionary<string, List<string>> GetParameterNames(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                // Check if parentheses are now balanced
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessFuncLineForParamNames(completeLine, typeStack, result);
                }
                // Don't process brace depth for continuation lines within signatures
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as GetInternalMembers)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for func/init declarations
            if (IsFuncOrInitLine(trimmed))
            {
                // Check for multi-line signature (unmatched open paren)
                if (HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessFuncLineForParamNames(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.printedName" keys to the fully-qualified error type string
    /// from typed throws declarations (e.g., "throws(Module.ErrorType)").
    /// For module-level free functions, the key is just "printedName".
    ///
    /// For example, for:
    ///   public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
    /// This produces: { "parseNumber(_:)": "SwiftBindingsTestLib.ParseError" }
    ///
    /// Only functions with typed throws are included; untyped "throws" and non-throwing
    /// functions are not present in the result.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Dictionary mapping method keys to fully-qualified error type strings.</returns>
    public static Dictionary<string, string> GetTypedThrowsErrors(string swiftInterfacePath)
    {
        var result = new Dictionary<string, string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessFuncLineForTypedThrows(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as GetInternalMembers/GetParameterNames)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for func/init declarations with typed throws
            if (IsFuncOrInitLine(trimmed))
            {
                if (HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessFuncLineForTypedThrows(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a complete function/init line to extract typed throws error type and add to result.
    /// </summary>
    private static void ProcessFuncLineForTypedThrows(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, string> result)
    {
        // Check if this line has a typed throws pattern
        var throwsMatch = TypedThrowsRegex.Match(line);
        if (!throwsMatch.Success)
            return;

        var errorType = throwsMatch.Groups[1].Value.Trim();

        // Try func match
        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            var printedName = ExtractPrintedName(line, funcName);
            var key = typeStack.Count > 0
                ? $"{typeStack.Peek().Name}.{printedName}"
                : printedName;
            result[key] = errorType;
            return;
        }

        // Try init match
        var initMatch = AnyInitRegex.Match(line);
        if (initMatch.Success)
        {
            var printedName = ExtractPrintedName(line, "init");
            var key = typeStack.Count > 0
                ? $"{typeStack.Peek().Name}.{printedName}"
                : printedName;
            result[key] = errorType;
        }
    }

    /// <summary>
    /// Checks if a line contains a func or init declaration.
    /// </summary>
    private static bool IsFuncOrInitLine(string trimmed)
    {
        return AnyFuncRegex.IsMatch(trimmed) || AnyInitRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// Checks if a line has unmatched open parentheses (multi-line signature).
    /// </summary>
    private static bool HasUnmatchedOpenParen(string line)
    {
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')') depth--;
        }
        return depth > 0;
    }

    /// <summary>
    /// Processes a complete function/init line to extract parameter names and add to result.
    /// </summary>
    private static void ProcessFuncLineForParamNames(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, List<string>> result)
    {
        // Try func match
        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            var (printedName, internalNames) = ExtractParamNamesFromLine(line, funcName);

            if (internalNames.Count > 0)
            {
                var key = typeStack.Count > 0
                    ? $"{typeStack.Peek().Name}.{printedName}"
                    : printedName;
                result[key] = internalNames;
            }
            return;
        }

        // Try init match
        var initMatch = AnyInitRegex.Match(line);
        if (initMatch.Success)
        {
            var (printedName, internalNames) = ExtractParamNamesFromLine(line, "init");

            if (internalNames.Count > 0)
            {
                var key = typeStack.Count > 0
                    ? $"{typeStack.Peek().Name}.{printedName}"
                    : printedName;
                result[key] = internalNames;
            }
        }
    }

    /// <summary>
    /// Extracts both the printed name (ABI format) and internal parameter names from a function line.
    /// For "func sumTwo(_ a: Int, _ b: Int) -> Int", returns:
    ///   printedName = "sumTwo(_:_:)"
    ///   internalNames = ["a", "b"]
    /// </summary>
    private static (string PrintedName, List<string> InternalNames) ExtractParamNamesFromLine(string line, string funcName)
    {
        var printedName = ExtractPrintedName(line, funcName);
        var internalNames = new List<string>();

        // Find the opening parenthesis after the function name
        var funcNameIdx = line.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName} (", StringComparison.Ordinal);
        // Also handle beginning of line (no space prefix)
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($"{funcName}(", StringComparison.Ordinal);
        // Also handle generic params: "func name<T>("
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return (printedName, internalNames);

        var parenStart = line.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return (printedName, internalNames);

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
        if (string.IsNullOrWhiteSpace(paramStr))
            return (printedName, internalNames);

        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            var colonIdx = trimPart.IndexOf(':');
            if (colonIdx < 0) continue;
            var beforeColon = trimPart.Substring(0, colonIdx).Trim();
            var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length >= 2)
            {
                // "externalLabel internalName" -> internal name is second word
                internalNames.Add(words[1]);
            }
            else if (words.Length == 1)
            {
                // "name:" -> same name is both external and internal
                internalNames.Add(words[0]);
            }
        }

        return (printedName, internalNames);
    }
}
