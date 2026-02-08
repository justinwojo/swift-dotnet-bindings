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
}
