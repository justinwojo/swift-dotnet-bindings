// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration;

/// <summary>
/// Reusable context tracker for .swiftinterface parsing.
/// Extracts the boilerplate duplicated across multiple methods in SwiftInterfaceAccessParser:
/// type stack, brace depth, extension scope tracking, annotation accumulation, multi-line continuation.
/// Used by GetAvailabilityAnnotations (Session 3) and GetDefaultParameterValues (Session 4).
/// </summary>
internal sealed class SwiftInterfaceContextTracker
{
    // Regex for type declarations: matches class/struct/enum/actor/protocol
    private static readonly Regex TypeDeclRegex = new(
        @"(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for extension declarations
    private static readonly Regex ExtensionDeclRegex = new(
        @"extension\s+([\w.]+)",
        RegexOptions.Compiled);

    // Regex for public/open func declarations
    private static readonly Regex PublicFuncRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for public/open var/let declarations
    private static readonly Regex PublicVarRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for public/open init declarations
    private static readonly Regex PublicInitRegex = new(
        @"(?:public|open)\s+(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    private readonly Stack<(string Name, int Depth, bool IsExtension)> _typeStack = new();
    private int _braceDepth;
    private readonly List<string> _pendingAnnotationLines = new();
    private List<string>? _extensionScopeAnnotations;

    // Multi-line continuation state
    private string? _continuationLine;
    private List<string>? _continuationPendingAnnotations;

    public string QualifiedTypePath =>
        _typeStack.Count > 0
            ? string.Join(".", _typeStack.Reverse().Select(t => t.Name))
            : string.Empty;

    public IReadOnlyList<string> PendingAnnotationLines => _pendingAnnotationLines;

    public bool IsInsideExtension => _typeStack.Any(t => t.IsExtension);

    public int TypeDepth => _typeStack.Count;

    public IReadOnlyList<string>? ExtensionScopeAnnotations => _extensionScopeAnnotations;

    public enum LineKind
    {
        TypeDeclaration,
        ExtensionDeclaration,
        AnnotationOnly,
        MemberLine,
        FreeFunctionLine,
        Continuation,
        Other
    }

    /// <summary>
    /// Processes a single line from a .swiftinterface file.
    /// Updates type stack, brace depth, pending annotations, and extension scope.
    /// Returns the kind of line detected.
    /// </summary>
    public LineKind ProcessLine(string trimmedLine, string rawLine)
    {
        // Handle multi-line continuation
        if (_continuationLine != null)
        {
            _continuationLine += " " + trimmedLine;
            if (!HasUnmatchedOpenParen(_continuationLine))
            {
                // Signature complete — yield as member line
                var completedLine = _continuationLine;
                _continuationLine = null;
                CompletedMultiLine = completedLine;
                // Restore pending annotations from when the continuation started
                if (_continuationPendingAnnotations != null)
                {
                    _pendingAnnotationLines.Clear();
                    _pendingAnnotationLines.AddRange(_continuationPendingAnnotations);
                    _continuationPendingAnnotations = null;
                }
                // Re-process the completed line as a member
                return ClassifyCompletedLine(completedLine, rawLine);
            }
            return LineKind.Continuation;
        }

        // Clear CompletedMultiLine for non-continuation lines
        CompletedMultiLine = null;

        var (openBraces, closeBraces) = SwiftInterfaceAccessParser.CountBraces(rawLine);

        // Check if this is a pure annotation line (starts with @, no declaration)
        if (trimmedLine.StartsWith("@") && !IsDeclarationLine(trimmedLine) && openBraces == 0)
        {
            _pendingAnnotationLines.Add(trimmedLine);
            _braceDepth += openBraces - closeBraces;
            PopTypeStackIfNeeded();
            return LineKind.AnnotationOnly;
        }

        // Check for type declaration
        var typeMatch = TypeDeclRegex.Match(trimmedLine);
        if (typeMatch.Success && openBraces > 0)
        {
            var typeName = typeMatch.Groups[1].Value;
            _typeStack.Push((typeName, _braceDepth, false));

            _braceDepth += openBraces - closeBraces;
            PopTypeStackIfNeeded();
            var kind = LineKind.TypeDeclaration;
            return kind;
        }

        // Check for extension declaration
        var extMatch = ExtensionDeclRegex.Match(trimmedLine);
        if (extMatch.Success && openBraces > 0)
        {
            var qualifiedName = extMatch.Groups[1].Value;
            // Strip the module prefix (first dot-component) and preserve all remaining
            // nested type components. e.g., "Module.Outer.Inner" → "Outer.Inner"
            var dotIdx = qualifiedName.IndexOf('.');
            var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
            // Capture pending annotations as extension-scope annotations
            if (_pendingAnnotationLines.Count > 0)
            {
                _extensionScopeAnnotations = new List<string>(_pendingAnnotationLines);
                _pendingAnnotationLines.Clear();
            }
            // Also check for inline annotations on the extension line itself
            else
            {
                var inlineAnnotations = ExtractAnnotationsFromLine(trimmedLine);
                if (inlineAnnotations.Count > 0)
                    _extensionScopeAnnotations = inlineAnnotations;
            }
            _typeStack.Push((typeName, _braceDepth, true));

            _braceDepth += openBraces - closeBraces;
            PopTypeStackIfNeeded();
            return LineKind.ExtensionDeclaration;
        }

        // Check for member declaration (func, var, init, subscript)
        if (_typeStack.Count > 0 && IsMemberLine(trimmedLine))
        {
            // Check for multi-line signature
            if (HasUnmatchedOpenParen(trimmedLine))
            {
                _continuationLine = trimmedLine;
                _continuationPendingAnnotations = new List<string>(_pendingAnnotationLines);
                _braceDepth += openBraces - closeBraces;
                PopTypeStackIfNeeded();
                return LineKind.Continuation;
            }

            _braceDepth += openBraces - closeBraces;
            PopTypeStackIfNeeded();
            return LineKind.MemberLine;
        }

        // Check for free function at module level (not inside a type) that may span multiple lines.
        // Same multi-line continuation logic as type members above, but for top-level functions.
        if (_typeStack.Count == 0 && IsMemberLine(trimmedLine))
        {
            if (HasUnmatchedOpenParen(trimmedLine))
            {
                _continuationLine = trimmedLine;
                _continuationPendingAnnotations = new List<string>(_pendingAnnotationLines);
                _braceDepth += openBraces - closeBraces;
                PopTypeStackIfNeeded();
                return LineKind.Continuation;
            }

            _braceDepth += openBraces - closeBraces;
            PopTypeStackIfNeeded();
            return LineKind.FreeFunctionLine;
        }

        _braceDepth += openBraces - closeBraces;
        PopTypeStackIfNeeded();

        // Clear pending annotations if we hit a non-annotation, non-member line
        if (!trimmedLine.StartsWith("@"))
            _pendingAnnotationLines.Clear();

        return LineKind.Other;
    }

    /// <summary>
    /// Builds a "QualifiedType.printedName" key for member correlation with ABI parser.
    /// </summary>
    public string BuildMemberKey(string printedName)
    {
        var typePath = QualifiedTypePath;
        return string.IsNullOrEmpty(typePath) ? printedName : $"{typePath}.{printedName}";
    }

    /// <summary>
    /// Resets the pending annotations buffer after the caller has consumed them.
    /// </summary>
    public void ConsumePendingAnnotations()
    {
        _pendingAnnotationLines.Clear();
    }

    /// <summary>
    /// Extracts the printed name (e.g., "funcName(_:bar:)") from a member declaration line.
    /// </summary>
    public static string? ExtractMemberPrintedName(string line)
    {
        var funcMatch = PublicFuncRegex.Match(line);
        if (funcMatch.Success)
            return SwiftInterfaceAccessParser.ExtractPrintedName(line, funcMatch.Groups[1].Value);

        if (PublicInitRegex.IsMatch(line))
            return SwiftInterfaceAccessParser.ExtractPrintedName(line, "init");

        var varMatch = PublicVarRegex.Match(line);
        if (varMatch.Success)
            return varMatch.Groups[1].Value;

        // subscript
        if (Regex.IsMatch(line, @"(?:public|open)\s+(?:static\s+)?subscript\s*[\(<]"))
            return ExtractSubscriptPrintedName(line);

        return null;
    }

    private static string ExtractSubscriptPrintedName(string line)
    {
        // subscript(key: KeyType) → "subscript(_:)" style
        var parenStart = line.IndexOf('(');
        if (parenStart < 0) return "subscript()";
        int depth = 0, parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')') { depth--; if (depth == 0) { parenEnd = i; break; } }
        }
        if (parenEnd == parenStart)
            return "subscript()";
        var paramContent = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
        var labels = new List<string>();
        foreach (var param in SplitParameters(paramContent))
        {
            var trimParam = param.Trim();
            var colonIdx = trimParam.IndexOf(':');
            if (colonIdx > 0)
            {
                var label = trimParam.Substring(0, colonIdx).Trim().Split(' ')[0];
                labels.Add(label == "_" ? "_:" : $"{label}:");
            }
            else
                labels.Add("_:");
        }
        return $"subscript({string.Join("", labels)})";
    }

    private static List<string> SplitParameters(string paramContent)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        bool inString = false;
        for (int i = 0; i < paramContent.Length; i++)
        {
            var c = paramContent[i];
            // Track string literals — skip commas inside "..."
            if (c == '"' && (i == 0 || paramContent[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (c == '(' || c == '<' || c == '[') depth++;
            else if (c == ')' || c == '>' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(paramContent.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(paramContent.Substring(start));
        return result;
    }

    private LineKind ClassifyCompletedLine(string completedLine, string rawLine)
    {
        // A completed multi-line continuation is a member line if inside a type,
        // or a free function line if at module level.
        return _typeStack.Count > 0 ? LineKind.MemberLine : LineKind.FreeFunctionLine;
    }

    /// <summary>
    /// The completed multi-line content (available when ProcessLine returns MemberLine after a Continuation).
    /// </summary>
    public string? CompletedMultiLine { get; private set; }

    private void PopTypeStackIfNeeded()
    {
        while (_typeStack.Count > 0 && _braceDepth <= _typeStack.Peek().Depth)
        {
            var popped = _typeStack.Pop();
            // Clear extension scope annotations when an extension scope pops
            if (popped.IsExtension)
                _extensionScopeAnnotations = null;
        }
    }

    private static bool IsDeclarationLine(string trimmedLine)
    {
        // Check if line contains a type or member declaration (not just annotations)
        return TypeDeclRegex.IsMatch(trimmedLine) ||
               ExtensionDeclRegex.IsMatch(trimmedLine) ||
               PublicFuncRegex.IsMatch(trimmedLine) ||
               PublicVarRegex.IsMatch(trimmedLine) ||
               PublicInitRegex.IsMatch(trimmedLine);
    }

    private static bool IsMemberLine(string trimmedLine)
    {
        return PublicFuncRegex.IsMatch(trimmedLine) ||
               PublicVarRegex.IsMatch(trimmedLine) ||
               PublicInitRegex.IsMatch(trimmedLine) ||
               Regex.IsMatch(trimmedLine, @"(?:public|open)\s+(?:static\s+)?subscript\s*[\(<]");
    }

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

    private static List<string> ExtractAnnotationsFromLine(string line)
    {
        var annotations = new List<string>();
        int idx = 0;
        while (idx < line.Length)
        {
            int atIdx = line.IndexOf("@available(", idx, StringComparison.Ordinal);
            if (atIdx < 0) break;
            int openParen = atIdx + "@available".Length;
            int depth = 1, i = openParen + 1;
            while (i < line.Length && depth > 0)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')') depth--;
                i++;
            }
            if (depth == 0)
                annotations.Add(line.Substring(atIdx, i - atIdx));
            idx = i;
        }
        return annotations;
    }
}
