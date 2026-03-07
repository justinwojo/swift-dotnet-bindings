// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

/// <summary>
/// Parses Clang qualType strings (e.g., "NSString * _Nonnull") into ObjCTypeRef.
/// </summary>
public static class ObjCTypeRefParser
{
    public static ObjCTypeRef Parse(string qualType)
    {
        var raw = qualType;
        var s = qualType.Trim();

        // 0. Strip __attribute__((...)) decorations and ObjC macros
        s = StripAttributes(s);
        s = StripObjCMacros(s);

        // 1. Detect anonymous union/struct types from clang: "union (unnamed union at ...)"
        if (s.StartsWith("union (", StringComparison.Ordinal) ||
            s.StartsWith("struct (", StringComparison.Ordinal))
        {
            return new ObjCTypeRef
            {
                Name = "AnonymousRecord",
                IsAnonymousRecord = true,
                RawQualType = raw
            };
        }

        // 2. Strip and record nullability annotations
        var nullability = ObjCNullability.Unspecified;
        s = StripNullability(s, ref nullability);

        // 3. Detect C function pointers after nullability stripping.
        // Matches: void (*)(int), BOOL (* _Nullable)(...), etc.
        // After stripping, these all normalize to contain "(*)" or "(* )".
        if (IsFunctionPointer(s))
        {
            return new ObjCTypeRef
            {
                Name = "FunctionPointer",
                IsFunctionPointer = true,
                RawQualType = raw
            };
        }

        // 4. Detect block types: void (^)(NSString *)
        if (TryParseBlock(s, nullability, raw, out var blockRef))
            return blockRef;

        // 5. Detect id<Protocol>
        if (TryParseIdProtocol(s, nullability, raw, out var idRef))
            return idRef;

        // 6. Detect double pointer: NSError ** or NSError * * (space between stars after nullability stripping)
        if (s.EndsWith("**") || s.EndsWith("* *"))
        {
            var inner = s.TrimEnd(' ', '*').Trim();
            return new ObjCTypeRef
            {
                Name = inner,
                IsPointer = true,
                Nullability = nullability,
                PointeeType = new ObjCTypeRef
                {
                    Name = inner,
                    IsPointer = true,
                    RawQualType = $"{inner} *"
                },
                RawQualType = raw
            };
        }

        // 5. Detect generics: NSArray<NSString *> *
        if (TryParseGeneric(s, nullability, raw, out var genRef))
            return genRef;

        // 6. Detect single pointer: strip trailing " *"
        var isPointer = false;
        if (s.EndsWith("*"))
        {
            s = s[..^1].Trim();
            isPointer = true;
        }

        // 7. Strip C type specifiers (clang qualType includes "enum Foo", "struct Bar")
        if (s.StartsWith("enum ", StringComparison.Ordinal))
            s = s[5..];
        else if (s.StartsWith("struct ", StringComparison.Ordinal))
            s = s[7..];

        // 8. Detect C constant array types (e.g., "uint8_t [4]", "NSString *[4]")
        var bracketIdx = s.IndexOf('[');
        if (bracketIdx > 0 && s.EndsWith(']'))
        {
            var elementStr = s[..bracketIdx].Trim();
            var sizeStr = s[(bracketIdx + 1)..^1].Trim();
            if (int.TryParse(sizeStr, out var arraySize))
            {
                // Parse the element type to handle pointers, type specifiers, etc.
                var elementRef = Parse(elementStr);
                return new ObjCTypeRef
                {
                    Name = elementRef.Name,
                    IsPointer = elementRef.IsPointer || isPointer,
                    Nullability = nullability,
                    FixedArraySize = arraySize,
                    RawQualType = raw
                };
            }
        }

        return new ObjCTypeRef
        {
            Name = s,
            IsPointer = isPointer,
            Nullability = nullability,
            RawQualType = raw
        };
    }

    private static string StripAttributes(string s)
    {
        // Remove __attribute__((...)) including nested parens
        while (true)
        {
            var idx = s.IndexOf("__attribute__((", StringComparison.Ordinal);
            if (idx < 0) break;

            // Find matching )) — must handle nested parens
            var depth = 0;
            var end = idx + 15; // skip "__attribute__(("
            for (; end < s.Length; end++)
            {
                if (s[end] == '(') depth++;
                else if (s[end] == ')')
                {
                    if (depth == 0)
                    {
                        // Found the first ), look for the second )
                        if (end + 1 < s.Length && s[end + 1] == ')')
                        {
                            end += 2;
                            break;
                        }
                    }
                    else
                        depth--;
                }
            }

            s = (s[..idx] + s[end..]).Trim();
        }

        // Collapse multiple spaces
        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        return s.Trim();
    }

    private static string StripObjCMacros(string s)
    {
        // Strip NS_REFINED_FOR_SWIFT
        s = s.Replace("NS_REFINED_FOR_SWIFT", "");

        // Strip availability macros (with optional parenthesized arguments).
        // Pattern-based: strip any NS_/API_/__ prefixed macro token.
        s = StripPrefixedMacros(s);

        // Strip C const qualifier (no C# equivalent in binding context)
        if (s.StartsWith("const ", StringComparison.Ordinal))
            s = s[6..];
        s = s.Replace("* const", "*").Replace("*const", "*");

        // Strip ObjC type qualifiers
        if (s.StartsWith("__kindof ", StringComparison.Ordinal))
            s = s[9..];

        // Collapse multiple spaces
        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        return s.Trim();
    }

    private static readonly string[] MacroPrefixes = ["NS_", "API_", "__API_", "__TVOS_", "__IOS_", "__WATCHOS_", "UI_", "OS_", "CF_"];

    private static string StripPrefixedMacros(string s)
    {
        // Strip any token starting with NS_/API_/__API_ (availability, deprecation, etc.)
        // with optional parenthesized arguments. Handles all variants:
        // NS_AVAILABLE, NS_DEPRECATED_MAC, API_AVAILABLE(ios(14.0)),
        // API_DEPRECATED_WITH_REPLACEMENT("...", ios(14.0)), etc.
        bool changed;
        do
        {
            changed = false;
            foreach (var prefix in MacroPrefixes)
            {
                var idx = s.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0) continue;

                // Find end of the macro name (uppercase letters, digits, underscores)
                var end = idx + prefix.Length;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_'))
                    end++;

                // If followed by '(', strip the parenthesized argument too
                if (end < s.Length && s[end] == '(')
                {
                    var depth = 0;
                    for (; end < s.Length; end++)
                    {
                        if (s[end] == '(') depth++;
                        else if (s[end] == ')')
                        {
                            depth--;
                            if (depth == 0) { end++; break; }
                        }
                    }
                }

                s = (s[..idx] + s[end..]).Trim();
                changed = true;
                break; // Restart scan from beginning
            }
        } while (changed);

        return s;
    }

    /// <summary>
    /// Detects C function pointers after nullability stripping.
    /// Matches patterns like "void (*)(int)", "BOOL (* )(int, float)", etc.
    /// </summary>
    private static bool IsFunctionPointer(string s)
    {
        // Look for "(* " or "(*)" — the signature of a C function pointer after stripping
        int parenStar = s.IndexOf("(*", StringComparison.Ordinal);
        return parenStar >= 0;
    }

    private static string StripNullability(string s, ref ObjCNullability nullability)
    {
        if (s.Contains("_Nonnull") || s.Contains("__nonnull"))
        {
            nullability = ObjCNullability.Nonnull;
            s = s.Replace("_Nonnull", "").Replace("__nonnull", "");
        }
        else if (s.Contains("_Nullable_result"))
        {
            nullability = ObjCNullability.Nullable;
            s = s.Replace("_Nullable_result", "");
        }
        else if (s.Contains("_Nullable") || s.Contains("__nullable"))
        {
            nullability = ObjCNullability.Nullable;
            s = s.Replace("_Nullable", "").Replace("__nullable", "");
        }

        // Strip _Null_unspecified (no semantic impact, just noise)
        s = s.Replace("_Null_unspecified", "");

        // Collapse multiple spaces
        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        return s.Trim();
    }

    private static bool TryParseBlock(string s, ObjCNullability nullability, string raw, out ObjCTypeRef result)
    {
        result = null!;

        // Pattern: ReturnType (^)(ParamTypes) or ReturnType (^ _Nullable)(ParamTypes)
        var caretIdx = s.IndexOf("(^");
        if (caretIdx < 0)
            return false;

        // Find the closing ) of the caret group
        var caretClose = FindMatchingParen(s, caretIdx);
        if (caretClose < 0)
            return false;

        // Params section starts at '(' immediately after caret group close
        if (caretClose + 1 >= s.Length || s[caretClose + 1] != '(')
            return false;

        var paramsOpen = caretClose + 1;
        var paramsClose = FindMatchingParen(s, paramsOpen);
        if (paramsClose < 0)
            return false;

        // Extract return type (everything before the caret group)
        var returnTypeStr = s[..caretIdx].Trim();
        if (string.IsNullOrEmpty(returnTypeStr))
            returnTypeStr = "void";

        var paramsStr = s[(paramsOpen + 1)..paramsClose].Trim();
        var blockParams = new List<ObjCTypeRef>();

        if (!string.IsNullOrEmpty(paramsStr) && paramsStr != "void")
        {
            foreach (var param in SplitBlockParams(paramsStr))
            {
                var trimmed = param.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    blockParams.Add(Parse(trimmed));
            }
        }

        result = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            Nullability = nullability,
            BlockReturnType = Parse(returnTypeStr),
            BlockParams = blockParams,
            RawQualType = raw
        };
        return true;
    }

    private static int FindMatchingParen(string s, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitBlockParams(string paramsStr)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < paramsStr.Length; i++)
        {
            switch (paramsStr[i])
            {
                case '<' or '(':
                    depth++;
                    break;
                case '>' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(paramsStr[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(paramsStr[start..]);
        return result;
    }

    private static bool TryParseIdProtocol(string s, ObjCNullability nullability, string raw, out ObjCTypeRef result)
    {
        result = null!;

        // id<Protocol> or id<Protocol> *
        if (!s.StartsWith("id<"))
            return false;

        var closeAngle = s.IndexOf('>');
        if (closeAngle < 0)
            return false;

        var protocols = s[3..closeAngle].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        result = new ObjCTypeRef
        {
            Name = "id",
            IsPointer = true,
            Nullability = nullability,
            ProtocolQualifications = protocols,
            RawQualType = raw
        };
        return true;
    }

    // ObjC lightweight generic containers — angle brackets contain type parameters, not protocols.
    private static readonly HashSet<string> KnownGenericContainers = ["NSArray", "NSDictionary", "NSSet",
        "NSOrderedSet", "NSEnumerator", "NSMutableArray", "NSMutableDictionary", "NSMutableSet",
        "NSMutableOrderedSet", "NSCache", "NSMapTable", "NSHashTable", "NSPointerArray"];

    private static bool TryParseGeneric(string s, ObjCNullability nullability, string raw, out ObjCTypeRef result)
    {
        result = null!;

        // NSArray<NSString *> * — find the outermost < > pair
        var angleOpen = s.IndexOf('<');
        if (angleOpen < 0)
            return false;

        var baseName = s[..angleOpen].Trim();

        // Find matching close angle
        var depth = 0;
        var angleClose = -1;
        for (var i = angleOpen; i < s.Length; i++)
        {
            if (s[i] == '<') depth++;
            else if (s[i] == '>')
            {
                depth--;
                if (depth == 0) { angleClose = i; break; }
            }
        }

        if (angleClose < 0)
            return false;

        var argsStr = s[(angleOpen + 1)..angleClose].Trim();

        // Check if pointer after the generic args
        var remainder = s[(angleClose + 1)..].Trim();
        var isPointer = remainder.Contains('*');

        // Distinguish protocol qualifications from generic type parameters.
        // Protocol qualifications: NSObject<NSCopying, NSSecureCoding> *
        //   — all args are simple identifiers (no *, <, (, spaces)
        //   — base type is NOT a known generic container
        // Generic parameters: NSArray<NSString *> *, NSDictionary<NSString *, NSNumber *> *
        //   — args contain pointer/complex types
        //   — OR base type IS a known generic container
        if (!KnownGenericContainers.Contains(baseName))
        {
            var argParts = argsStr.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            var allSimpleNames = argParts.All(a => a.All(c => char.IsLetterOrDigit(c) || c == '_'));
            if (allSimpleNames && argParts.Count > 0)
            {
                result = new ObjCTypeRef
                {
                    Name = baseName,
                    IsPointer = isPointer,
                    Nullability = nullability,
                    ProtocolQualifications = argParts,
                    RawQualType = raw
                };
                return true;
            }
        }

        var genericArgs = new List<ObjCTypeRef>();
        foreach (var arg in SplitBlockParams(argsStr))
        {
            var trimmed = arg.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                genericArgs.Add(Parse(trimmed));
        }

        result = new ObjCTypeRef
        {
            Name = baseName,
            IsPointer = isPointer,
            Nullability = nullability,
            GenericArgs = genericArgs,
            RawQualType = raw
        };
        return true;
    }
}
