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
