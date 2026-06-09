// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Post-generation ABI contract checker.
// Validates generated C# P/Invoke declarations against ABI safety rules.
//
// Checks implemented:
//   SWIFTBIND090 (CC-001): SafeHandle/non-blittable param in CallConvSwift
//   SWIFTBIND091 (CC-002): Non-blittable return type in CallConvSwift
//   SWIFTBIND092 (Tj):     Cross-module Tj dispatch thunk targeting wrong library
//   SWIFTBIND093 (CC-003): @_cdecl wrapper entry point targeting original library
//   SWIFTBIND094 (CC-004): CallConvCdecl targeting mangled Swift symbol
//
// Precision refinements (reaching ~83% precision, 100% recall):
//   1. De-duplicate findings by (RuleId, MethodName)
//   2. Exclude primitive type names from float struct heuristic
//   3. Require positional adjacency for closure context classification
//   4. Use _async in entry point for async detection (not param name heuristic)

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Post-generation ABI contract validation results.
/// </summary>
public sealed record AbiCheckResult
{
    /// <summary>All detected violations.</summary>
    public required ImmutableArray<AbiCheckViolation> Violations { get; init; }

    /// <summary>Number of P/Invokes analyzed.</summary>
    public int PInvokeCount { get; init; }

    /// <summary>True if no fatal violations were found.</summary>
    public bool IsClean => Violations.IsEmpty;
}

/// <summary>
/// A single ABI contract violation detected in generated output.
/// </summary>
public sealed record AbiCheckViolation
{
    /// <summary>SWIFTBIND diagnostic code (e.g., "SWIFTBIND090").</summary>
    public required string DiagnosticCode { get; init; }

    /// <summary>Short rule identifier (e.g., "CC-001").</summary>
    public required string RuleId { get; init; }

    /// <summary>The P/Invoke method name that violates the rule.</summary>
    public required string MethodName { get; init; }

    /// <summary>The entry point symbol.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Human-readable explanation.</summary>
    public required string Explanation { get; init; }

    /// <summary>Affected parameter/return type names.</summary>
    public ImmutableArray<string> AffectedElements { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>
/// Validates generated C# output against ABI safety contracts.
/// Runs after code generation but before file write.
/// </summary>
public static class AbiContractChecker
{
    // ── Known blittable types (safe for CallConvSwift) ──

    private static readonly HashSet<string> BlittableTypes = new(StringComparer.Ordinal)
    {
        // Primitives
        "int", "uint", "long", "ulong", "short", "ushort",
        "byte", "sbyte", "nint", "nuint", "float", "double",
        "bool", "IntPtr", "UIntPtr", "void*",
        // Swift interop register types
        "SwiftIndirectResult", "SwiftError",
        // Blittable structs from runtime
        "ExistentialContainer0", "ExistentialContainer1", "ExistentialContainer2",
        "ExistentialContainer3", "ExistentialContainer4", "ExistentialContainer5",
        "ExistentialContainer6", "ExistentialContainer7", "ExistentialContainer8",
        "SwiftClosureData", "SwiftHandle", "TypeMetadata",
        "BlittableOptionalInt32",
    };

    private static readonly HashSet<string> NonBlittableTypes = new(StringComparer.Ordinal)
    {
        "SafeHandle", "SwiftSafeHandle", "SwiftClassHandle",
        "SwiftOptional", "SwiftString", "string",
        "PayloadBuffer",
    };

    // Refinement 2: Primitive type names that should NOT trigger float struct heuristic
    private static readonly HashSet<string> PrimitiveTypeExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "double", "float", "Double", "Float", "int", "Int32",
        "nint", "byte", "long", "ulong", "short", "ushort",
        "sbyte", "uint", "nuint", "bool", "IntPtr", "UIntPtr",
    };

    // ── Regex patterns for P/Invoke extraction ──

    // Matches both unqualified and global:: qualified UnmanagedCallConv attributes.
    // Captures the calling convention type name (e.g., "CallConvSwift" or "CallConvCdecl").
    private static readonly Regex CallingConvRegex = new(
        @"\[(?:global::System\.Runtime\.InteropServices\.)?UnmanagedCallConv\(CallConvs\s*=\s*new\s+(?:global::System\.)?Type\[\]\s*\{\s*typeof\((?:global::System\.Runtime\.CompilerServices\.)?(CallConv\w+)\)\s*\}\)\]",
        RegexOptions.Compiled);

    // Matches both unqualified and global:: qualified LibraryImport attributes.
    // Captures library name and entry point.
    private static readonly Regex LibraryImportRegex = new(
        @"\[(?:global::System\.Runtime\.InteropServices\.)?LibraryImport\(""([^""]+)""\s*,\s*EntryPoint\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    // Matches P/Invoke signature with any visibility (private/internal/public),
    // optional new/unsafe modifiers. Captures return type, method name, and params.
    // Params may be empty or multiline (handled separately).
    private static readonly Regex PInvokeSignatureStartRegex = new(
        @"(?:private|internal|public)\s+static\s+(?:new\s+)?(?:unsafe\s+)?partial\s+(\S+)\s+(\w+)\(",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclRegex = new(
        @"(?:public|internal)\s+(?:sealed\s+)?partial\s+(?:class|struct)\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Validate generated C# output against ABI contracts.
    /// </summary>
    /// <param name="csOutput">The generated C# source text.</param>
    /// <param name="moduleName">The Swift module name.</param>
    /// <param name="logger">Logger for emitting SWIFTBIND warnings.</param>
    /// <returns>Validation result with any detected violations.</returns>
    public static AbiCheckResult Validate(string csOutput, string moduleName, ILogger logger)
    {
        var pinvokes = ExtractPInvokes(csOutput, moduleName);
        var violations = new List<AbiCheckViolation>();

        foreach (var pinvoke in pinvokes)
        {
            violations.AddRange(CheckCC001_NonBlittableParams(pinvoke));
            violations.AddRange(CheckCC002_NonBlittableReturn(pinvoke));
            violations.AddRange(CheckCC003_CdeclTargetsWrongLib(pinvoke));
            violations.AddRange(CheckCC004_CdeclMangledSymbol(pinvoke));
        }

        // Tj thunk cross-module detection (operates on the full set)
        violations.AddRange(CheckTjThunkCrossModule(pinvokes, moduleName));

        // Refinement 1: De-duplicate by (RuleId, MethodName)
        var deduplicated = violations
            .GroupBy(v => (v.RuleId, v.MethodName))
            .Select(g => g.First())
            .ToImmutableArray();

        // Log warnings for each violation
        foreach (var violation in deduplicated)
        {
            var elements = violation.AffectedElements.IsEmpty
                ? ""
                : $" [{string.Join(", ", violation.AffectedElements)}]";
            logger.LogWarning("{Code}: {Explanation}{Elements}",
                violation.DiagnosticCode, violation.Explanation, elements);
        }

        return new AbiCheckResult
        {
            Violations = deduplicated,
            PInvokeCount = pinvokes.Length,
        };
    }

    // ── Check implementations ──

    /// <summary>
    /// CC-001: CallConvSwift P/Invoke has non-blittable parameter(s).
    /// Both Mono and NativeAOT throw InvalidProgramException.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC001_NonBlittableParams(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvSwift")
            return ImmutableArray<AbiCheckViolation>.Empty;

        var nonBlittable = pinvoke.Parameters
            .Where(p => IsNonBlittable(p.CSharpType)
                && !p.IsSwiftSelf
                && !p.IsSwiftIndirectResult
                && !p.IsInfrastructure
                // Refinement 3: Exclude closure context (IntPtr adjacent to funcPtr)
                && !p.IsClosureContext)
            .ToList();

        if (nonBlittable.Count == 0)
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND090",
            RuleId = "CC-001",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"Internal validation detected an issue with the generated binding for '{pinvoke.MethodName}' " +
                $"({nonBlittable.Count} incompatible parameter(s)). " +
                $"This method may not work correctly at runtime. Please file an issue with your xcframework.",
            AffectedElements = nonBlittable
                .Select(p => $"{p.CSharpType} {p.Name}")
                .ToImmutableArray(),
        });
    }

    /// <summary>
    /// CC-002: CallConvSwift P/Invoke has non-blittable return type.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC002_NonBlittableReturn(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvSwift")
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (pinvoke.ReturnType == "void" || !IsNonBlittable(pinvoke.ReturnType))
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND091",
            RuleId = "CC-002",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"Internal validation detected an issue with the generated binding for '{pinvoke.MethodName}' " +
                $"(incompatible return type). " +
                $"This method may not work correctly at runtime. Please file an issue with your xcframework.",
            AffectedElements = ImmutableArray.Create($"return: {pinvoke.ReturnType}"),
        });
    }

    /// <summary>
    /// CC-003: @_cdecl wrapper P/Invoke (SBW_ entry point) targeting original library instead of wrapper.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC003_CdeclTargetsWrongLib(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvCdecl")
            return ImmutableArray<AbiCheckViolation>.Empty;

        // SBW_ entry point should target wrapper library, not original
        if (!pinvoke.EntryPoint.StartsWith("SBW_"))
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (pinvoke.TargetLibrary != TargetLibraryKind.OriginalLibrary)
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND093",
            RuleId = "CC-003",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"Internal validation detected an issue with the generated binding for '{pinvoke.MethodName}' " +
                $"(incorrect library target). " +
                $"This method may not work correctly at runtime. Please file an issue with your xcframework.",
        });
    }

    /// <summary>
    /// CC-004: CallConvCdecl targeting a mangled Swift symbol ($s...).
    /// C calling convention + Swift symbol = register mismatch.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckCC004_CdeclMangledSymbol(PInvokeInfo pinvoke)
    {
        if (pinvoke.CallingConvention != "CallConvCdecl")
            return ImmutableArray<AbiCheckViolation>.Empty;

        if (!pinvoke.EntryPoint.StartsWith("$s"))
            return ImmutableArray<AbiCheckViolation>.Empty;

        return ImmutableArray.Create(new AbiCheckViolation
        {
            DiagnosticCode = "SWIFTBIND094",
            RuleId = "CC-004",
            MethodName = pinvoke.MethodName,
            EntryPoint = pinvoke.EntryPoint,
            Explanation = $"Internal validation detected an issue with the generated binding for '{pinvoke.MethodName}' " +
                $"(calling convention mismatch). " +
                $"This method may not work correctly at runtime. Please file an issue with your xcframework.",
        });
    }

    /// <summary>
    /// Cross-module Tj thunk detection: P/Invoke targets a Tj dispatch thunk
    /// whose mangled symbol encodes a different module than the target library.
    /// </summary>
    internal static ImmutableArray<AbiCheckViolation> CheckTjThunkCrossModule(
        ImmutableArray<PInvokeInfo> pinvokes, string moduleName)
    {
        var violations = new List<AbiCheckViolation>();

        foreach (var pinvoke in pinvokes)
        {
            // Only check mangled symbols targeting original library
            if (!pinvoke.EntryPoint.StartsWith("$s"))
                continue;
            if (pinvoke.TargetLibrary != TargetLibraryKind.OriginalLibrary)
                continue;

            // Must be a Tj dispatch thunk
            if (!pinvoke.EntryPoint.EndsWith("Tj"))
                continue;

            // Extract module name from mangled symbol
            var extractedModule = ExtractModuleFromMangledSymbol(pinvoke.EntryPoint);
            if (extractedModule == null)
                continue;

            // Cross-module mismatch: symbol encodes module A but targets library B
            if (extractedModule != moduleName)
            {
                violations.Add(new AbiCheckViolation
                {
                    DiagnosticCode = "SWIFTBIND092",
                    RuleId = "Tj-XM",
                    MethodName = pinvoke.MethodName,
                    EntryPoint = pinvoke.EntryPoint,
                    Explanation = $"Internal validation detected an issue with the generated binding for '{pinvoke.MethodName}' " +
                        $"(cross-module reference mismatch). " +
                        $"This method may not work correctly at runtime. Please file an issue with your xcframework.",
                    AffectedElements = ImmutableArray.Create(
                        $"symbol module: {extractedModule}",
                        $"target library: {moduleName}"),
                });
            }
        }

        return violations.ToImmutableArray();
    }

    // ── P/Invoke text extraction ──

    /// <summary>
    /// Extract P/Invoke declarations from generated C# source text.
    /// Anchors on [LibraryImport] (always present), looks backwards for calling
    /// convention (optional), and forward for signature (possibly multiline).
    /// Handles both unqualified and global:: qualified attribute forms, and
    /// private/internal/public visibility modifiers.
    /// </summary>
    internal static ImmutableArray<PInvokeInfo> ExtractPInvokes(string sourceText, string moduleName)
    {
        var results = new List<PInvokeInfo>();
        var lines = sourceText.Split('\n');
        string? currentClass = null;
        var wrapperLibName = moduleName + "SwiftBindings";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();

            // Track current class context
            var classMatch = ClassDeclRegex.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                continue;
            }

            // Anchor on [LibraryImport] — always present in every P/Invoke
            var libMatch = LibraryImportRegex.Match(line);
            if (!libMatch.Success)
                continue;

            var libraryName = libMatch.Groups[1].Value;
            var entryPoint = libMatch.Groups[2].Value;

            // Look backwards (up to 3 lines) for [UnmanagedCallConv]
            string? callingConvention = null;
            for (int k = Math.Max(0, i - 3); k < i; k++)
            {
                var prevLine = lines[k].TrimStart();
                var callConvMatch = CallingConvRegex.Match(prevLine);
                if (callConvMatch.Success)
                {
                    callingConvention = callConvMatch.Groups[1].Value;
                    break;
                }
            }

            // If no [UnmanagedCallConv] found, the runtime uses the platform default
            // (C calling convention), not Swift. All real generator paths that emit $s...
            // symbols also emit [UnmanagedCallConv(CallConvSwift)]. The no-attribute
            // emitters (EnumHandler, SBW_Free helpers) are always wrapper/C ABI paths.
            callingConvention ??= "CallConvCdecl";

            // Look forward for the signature — may be on the next line or span multiple lines
            string? returnType = null;
            string? methodName = null;
            string? paramsStr = null;

            for (int j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                var scanLine = lines[j].TrimStart();

                // Skip attribute lines (e.g., [return: MarshalAs(...)])
                if (scanLine.StartsWith("["))
                    continue;

                var sigMatch = PInvokeSignatureStartRegex.Match(scanLine);
                if (sigMatch.Success)
                {
                    returnType = sigMatch.Groups[1].Value;
                    methodName = sigMatch.Groups[2].Value;

                    // Extract parameter string — may be single-line or multiline
                    var afterParen = scanLine.Substring(sigMatch.Index + sigMatch.Length);
                    paramsStr = ExtractParameterString(afterParen, lines, j);
                    break;
                }
            }

            if (returnType == null || methodName == null)
                continue;

            // Classify target library
            var targetLibrary = ClassifyLibrary(libraryName, entryPoint, wrapperLibName);

            // Parse parameters with refinements
            var parameters = ParseParameters(paramsStr ?? "");

            // Refinement 4: Detect async from _async in entry point, not param names
            bool isAsync = entryPoint.Contains("_async");

            results.Add(new PInvokeInfo
            {
                MethodName = methodName,
                EntryPoint = entryPoint,
                CallingConvention = callingConvention,
                TargetLibrary = targetLibrary,
                LibraryName = libraryName,
                ReturnType = returnType,
                Parameters = parameters,
                IsAsync = isAsync,
                ContainingClass = currentClass,
            });
        }

        return results.ToImmutableArray();
    }

    /// <summary>
    /// Extract the parameter string from the portion after the opening paren,
    /// handling multiline signatures by accumulating until ");".
    /// </summary>
    private static string ExtractParameterString(string afterParen, string[] lines, int currentLine)
    {
        // Check if the closing ");' is on the same line
        var closeIdx = FindClosingParen(afterParen);
        if (closeIdx >= 0)
            return afterParen.Substring(0, closeIdx).Trim();

        // Multiline: accumulate until we find ");" across subsequent lines
        var accumulated = new System.Text.StringBuilder(afterParen.TrimEnd());
        for (int j = currentLine + 1; j < Math.Min(currentLine + 20, lines.Length); j++)
        {
            var nextLine = lines[j].Trim();
            var closeInNext = FindClosingParen(nextLine);
            if (closeInNext >= 0)
            {
                accumulated.Append(' ');
                accumulated.Append(nextLine.Substring(0, closeInNext).Trim());
                break;
            }
            accumulated.Append(' ');
            accumulated.Append(nextLine);
        }

        return accumulated.ToString().Trim();
    }

    /// <summary>
    /// Find the index of the closing ");" in a string, respecting nesting of parens
    /// (for delegate* types like delegate* unmanaged[Cdecl]&lt;int, void&gt;).
    /// Returns -1 if not found.
    /// </summary>
    private static int FindClosingParen(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': depth++; break;
                case ')':
                    if (depth > 0)
                        depth--;
                    else
                        return i; // This is the closing paren of the signature
                    break;
            }
        }
        return -1;
    }

    // ── Classification helpers ──

    private static TargetLibraryKind ClassifyLibrary(string libraryName, string entryPoint, string wrapperLibName)
    {
        if (libraryName.Contains("libswiftCore") || libraryName.Contains("SwiftCore"))
            return TargetLibraryKind.SwiftCore;

        // Classify based on library name, NOT entry point.
        // CC-003 catches the case where entry point is SBW_ but library name is wrong.
        if (libraryName == "SwiftBindings" ||
            libraryName.EndsWith("SwiftBindings") || libraryName == wrapperLibName)
            return TargetLibraryKind.WrapperLibrary;

        return TargetLibraryKind.OriginalLibrary;
    }

    private static bool IsNonBlittable(string csharpType)
    {
        var baseType = StripTypeModifiers(csharpType);

        // Explicit blittable check first
        if (BlittableTypes.Contains(baseType))
            return false;

        // Generic SwiftSelf<T> is blittable
        if (baseType.StartsWith("SwiftSelf"))
            return false;

        // Function pointers are blittable
        if (baseType.Contains("delegate*"))
            return false;

        // SwiftString.Buffer (PayloadBuffer) is a blittable 16-byte struct —
        // must check before the substring match which would match "SwiftString"
        if (baseType.Contains("SwiftString.Buffer") || baseType == "Buffer")
            return false;

        // Check known non-blittable types (substring match for generics like SwiftOptional<T>)
        foreach (var nbt in NonBlittableTypes)
        {
            if (baseType.Contains(nbt))
                return true;
        }

        // Refinement 2: Don't flag primitive type names
        if (PrimitiveTypeExclusions.Contains(baseType))
            return false;

        // Default: assume blittable (custom structs, enums)
        return false;
    }

    private static string StripTypeModifiers(string type)
    {
        var result = type.Trim();
        if (result.StartsWith("ref ")) result = result.Substring(4);
        if (result.StartsWith("out ")) result = result.Substring(4);
        if (result.StartsWith("in ")) result = result.Substring(3);
        // Strip generic suffix for base type check
        var angleIdx = result.IndexOf('<');
        if (angleIdx > 0)
        {
            var basePart = result.Substring(0, angleIdx);
            // Check base part against non-blittable (e.g., SwiftOptional<T>)
            return basePart;
        }
        return result;
    }

    private static ImmutableArray<PInvokeParamInfo> ParseParameters(string paramsStr)
    {
        if (string.IsNullOrWhiteSpace(paramsStr))
            return ImmutableArray<PInvokeParamInfo>.Empty;

        var results = new List<PInvokeParamInfo>();
        var paramParts = SplitParameters(paramsStr);

        for (int idx = 0; idx < paramParts.Count; idx++)
        {
            var trimmed = paramParts[idx].Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Parse "Type name" or "ref Type name"
            var typeAndName = trimmed;
            if (typeAndName.StartsWith("ref ") || typeAndName.StartsWith("out "))
                typeAndName = typeAndName.Substring(4).TrimStart();

            var lastSpace = typeAndName.LastIndexOf(' ');
            if (lastSpace < 0) continue;

            var csType = typeAndName.Substring(0, lastSpace).Trim();
            var name = typeAndName.Substring(lastSpace + 1).Trim();

            bool isSwiftSelf = csType.StartsWith("SwiftSelf");
            bool isSwiftIndirectResult = csType == "SwiftIndirectResult";
            bool isInfrastructure =
                name.EndsWith("Metadata") || name.Contains("TMetadata") ||
                name.EndsWith("PWT") || name.Contains("ProtocolWitnessTable");

            // Refinement 3: Closure context requires IntPtr type AND adjacency to funcPtr
            bool isClosureContext = false;
            if ((name.Contains("Context") || name.Contains("context")) && csType == "IntPtr")
            {
                // Check if previous parameter is a function pointer (closure funcPtr)
                if (idx > 0)
                {
                    var prevTrimmed = paramParts[idx - 1].Trim();
                    if (prevTrimmed.Contains("delegate*") || prevTrimmed.Contains("FuncPtr") || prevTrimmed.Contains("funcPtr"))
                        isClosureContext = true;
                }
            }

            results.Add(new PInvokeParamInfo
            {
                CSharpType = csType,
                Name = name,
                IsSwiftSelf = isSwiftSelf,
                IsSwiftIndirectResult = isSwiftIndirectResult,
                IsInfrastructure = isInfrastructure,
                IsClosureContext = isClosureContext,
            });
        }

        return results.ToImmutableArray();
    }

    private static List<string> SplitParameters(string paramsStr)
    {
        var results = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < paramsStr.Length; i++)
        {
            switch (paramsStr[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case '(' when paramsStr[i..].StartsWith("("):
                    depth++; break;
                case ')': if (depth > 0) depth--; break;
                case ',' when depth == 0:
                    results.Add(paramsStr.Substring(start, i - start));
                    start = i + 1;
                    break;
            }
        }

        if (start < paramsStr.Length)
            results.Add(paramsStr.Substring(start));

        return results;
    }

    /// <summary>
    /// Extract the module name from a mangled Swift symbol.
    /// Format: $s{length}{moduleName}... where {length} is the decimal character count.
    /// </summary>
    internal static string? ExtractModuleFromMangledSymbol(string entryPoint)
    {
        if (!entryPoint.StartsWith("$s"))
            return null;

        int i = 2; // skip "$s"
        int length = 0;
        while (i < entryPoint.Length && char.IsDigit(entryPoint[i]))
        {
            length = length * 10 + (entryPoint[i] - '0');
            i++;
        }

        if (length == 0 || i + length > entryPoint.Length)
            return null;

        return entryPoint.Substring(i, length);
    }

    private static string TruncateSymbol(string symbol)
    {
        return symbol.Length > 60 ? symbol.Substring(0, 57) + "..." : symbol;
    }

    // ── Internal types ──

    internal enum TargetLibraryKind
    {
        OriginalLibrary,
        WrapperLibrary,
        SwiftCore,
    }

    internal sealed record PInvokeInfo
    {
        public required string MethodName { get; init; }
        public required string EntryPoint { get; init; }
        public required string CallingConvention { get; init; }
        public required TargetLibraryKind TargetLibrary { get; init; }
        public required string LibraryName { get; init; }
        public required string ReturnType { get; init; }
        public required ImmutableArray<PInvokeParamInfo> Parameters { get; init; }
        public bool IsAsync { get; init; }
        public string? ContainingClass { get; init; }
    }

    internal sealed record PInvokeParamInfo
    {
        public required string CSharpType { get; init; }
        public required string Name { get; init; }
        public bool IsSwiftSelf { get; init; }
        public bool IsSwiftIndirectResult { get; init; }
        public bool IsInfrastructure { get; init; }
        public bool IsClosureContext { get; init; }
    }
}
