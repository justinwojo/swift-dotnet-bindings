// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for identifier sanitization: backtick stripping, emoji/non-ASCII character handling,
/// and SanitizeIdentifierChars.
/// </summary>
public class NameProviderSanitizationTests
{
    #region ToPascalCase — Backtick Stripping

    [Fact]
    public void ToPascalCase_BacktickWrappedKeyword_StripsBackticks()
    {
        // Swift uses `default` to escape keywords — backticks should be stripped
        var result = NameProvider.ToPascalCase("`default`");
        Assert.Equal("Default", result);
    }

    [Fact]
    public void ToPascalCase_BacktickWrappedSubscript_StripsBackticks()
    {
        // `subscript` used as an enum case name — backticks must be stripped
        var result = NameProvider.ToPascalCase("`subscript`");
        Assert.Equal("Subscript", result);
    }

    [Fact]
    public void ToPascalCase_BacktickWrappedClass_StripsBackticks()
    {
        var result = NameProvider.ToPascalCase("`class`");
        Assert.Equal("Class", result);
    }

    [Fact]
    public void ToPascalCase_NoBackticks_Unchanged()
    {
        var result = NameProvider.ToPascalCase("normalName");
        Assert.Equal("NormalName", result);
    }

    #endregion

    #region ToPascalCase — Emoji Sanitization

    [Fact]
    public void ToPascalCase_EmojiCharacter_ReplacedWithUnderscore()
    {
        // 🚫 in enum case names (emoji is 2 UTF-16 chars → 2 underscores)
        var result = NameProvider.ToPascalCase("couldNot🚫");
        Assert.Equal("CouldNot__", result);
    }

    [Fact]
    public void ToPascalCase_EmojiOnly_ReturnsUnderscores()
    {
        // 🚫 is 2 UTF-16 code units → 2 underscores
        var result = NameProvider.ToPascalCase("🚫");
        Assert.Equal("__", result);
    }

    [Fact]
    public void ToPascalCase_EmojiInMiddle_ReplacedWithUnderscore()
    {
        var result = NameProvider.ToPascalCase("no🚫Access");
        Assert.Equal("No__Access", result);
    }

    #endregion

    #region SanitizeIdentifierChars

    [Fact]
    public void SanitizeIdentifierChars_ValidIdentifier_Unchanged()
    {
        var result = NameProvider.SanitizeIdentifierChars("validName123");
        Assert.Equal("validName123", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_Underscore_Preserved()
    {
        var result = NameProvider.SanitizeIdentifierChars("name_with_underscores");
        Assert.Equal("name_with_underscores", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_Emoji_ReplacedWithUnderscore()
    {
        // Emoji are multi-char UTF-16 sequences, each char becomes an underscore
        var result = NameProvider.SanitizeIdentifierChars("emoji🚫here");
        Assert.Equal("emoji__here", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_MultipleInvalidChars_AllReplaced()
    {
        var result = NameProvider.SanitizeIdentifierChars("a!b@c#d");
        Assert.Equal("a_b_c_d", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_StartsWithDigitAfterSanitization_PrefixedWithUnderscore()
    {
        // Digit-starts-with only prefixes when sanitization was triggered
        var result = NameProvider.SanitizeIdentifierChars("!123abc");
        Assert.Equal("_123abc", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_PureDigits_NoPrefix()
    {
        // No invalid chars → no sanitization → no digit prefix
        // (ToPascalCase handles this separately)
        var result = NameProvider.SanitizeIdentifierChars("123abc");
        Assert.Equal("123abc", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_UnicodeLetters_Preserved()
    {
        // C# allows unicode letters in identifiers
        var result = NameProvider.SanitizeIdentifierChars("名前");
        Assert.Equal("名前", result);
    }

    [Fact]
    public void SanitizeIdentifierChars_DollarSign_Replaced()
    {
        var result = NameProvider.SanitizeIdentifierChars("$projected");
        Assert.Equal("_projected", result);
    }

    #endregion

    #region ExtractUniqueName — Backtick Stripping (tested via ToPascalCase pipeline)

    [Fact]
    public void ToPascalCase_BacktickEscapedReturn_StripsAndCapitalizes()
    {
        // Swift keyword 'return' escaped with backticks
        var result = NameProvider.ToPascalCase("`return`");
        Assert.Equal("Return", result);
    }

    [Fact]
    public void ToPascalCase_BacktickEscapedSelf_StripsAndCapitalizes()
    {
        var result = NameProvider.ToPascalCase("`self`");
        Assert.Equal("Self", result);
    }

    [Fact]
    public void ComputeCaseNameMap_EmojiSanitizedCollision_Deduplicates()
    {
        // Two different emoji both sanitize to "__" → PascalCase produces same name → collision
        // error🚫 → "error__" → "Error__", error🔶 → "error__" → "Error__"
        // The collision map should append numeric suffix to the second occurrence
        var cases = MakeCases("error\U0001F6AB", "error\U0001F536");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.NotNull(map);
        // First gets Error__, second gets Error__2
        var firstName = map["error\U0001F6AB"];
        var secondName = map["error\U0001F536"];
        Assert.NotEqual(firstName, secondName);
        Assert.Equal("Error__", firstName);
        Assert.Equal("Error__2", secondName);
    }

    [Fact]
    public void ToPascalCase_MultipleEmoji_AllReplacedWithUnderscores()
    {
        // 🚫🔶 = 4 UTF-16 chars → 4 underscores
        var result = NameProvider.ToPascalCase("test\U0001F6AB\U0001F536end");
        Assert.Equal("Test____end", result);
    }

    #endregion

    #region EscapeSwiftKeyword

    [Fact]
    public void EscapeSwiftKeyword_Subscript_WrappedInBackticks()
    {
        var result = NameProvider.EscapeSwiftKeyword("subscript");
        Assert.Equal("`subscript`", result);
    }

    [Fact]
    public void EscapeSwiftKeyword_Default_WrappedInBackticks()
    {
        var result = NameProvider.EscapeSwiftKeyword("default");
        Assert.Equal("`default`", result);
    }

    [Fact]
    public void EscapeSwiftKeyword_NonKeyword_Unchanged()
    {
        var result = NameProvider.EscapeSwiftKeyword("normalName");
        Assert.Equal("normalName", result);
    }

    [Fact]
    public void EscapeSwiftKeyword_UnderscorePrefixed_NotStripped()
    {
        // EscapeSwiftKeyword does NOT strip parser prefixes — that's ParserNameToSwift's job.
        // A genuine Swift identifier like "_default" passes through unchanged.
        var result = NameProvider.EscapeSwiftKeyword("_default");
        Assert.Equal("_default", result);
    }

    [Fact]
    public void EscapeSwiftKeyword_UnderscoreClass_NotStripped()
    {
        // A genuine Swift property named "_class" — EscapeSwiftKeyword must NOT strip
        // the underscore. This is the accessor-derived property name case:
        // accessor "_class_Get" → base name "_class" → EscapeSwiftKeyword → "_class"
        var result = NameProvider.EscapeSwiftKeyword("_class");
        Assert.Equal("_class", result);
    }

    #endregion

    #region ParserNameToSwift — Declaration-Based (Provenance-Aware)

    private static MethodDecl MakeMethodDecl(string name, string? originalSwiftName = null) =>
        new MethodDecl
        {
            Name = name,
            OriginalSwiftName = originalSwiftName,
            MangledName = "$s_test",
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false
        };

    [Fact]
    public void ParserNameToSwift_Decl_ParserEscapedDefault_UsesOriginal()
    {
        // Parser stored OriginalSwiftName = "default" when it prefixed to "_default".
        var decl = MakeMethodDecl("_default", originalSwiftName: "default");
        var result = NameProvider.ParserNameToSwift(decl);
        Assert.Equal("`default`", result);
    }

    [Fact]
    public void ParserNameToSwift_Decl_GenuineUnderscoreClass_PreservedExactly()
    {
        // A genuine Swift method named "_class" — no OriginalSwiftName set.
        var decl = MakeMethodDecl("_class", originalSwiftName: null);
        var result = NameProvider.ParserNameToSwift(decl);
        // "_class" is NOT a Swift keyword, so no backtick escaping.
        Assert.Equal("_class", result);
    }

    [Fact]
    public void ParserNameToSwift_Decl_ParserEscapedClass_UsesOriginal()
    {
        // Parser stored OriginalSwiftName = "class" when it prefixed to "_class".
        var decl = MakeMethodDecl("_class", originalSwiftName: "class");
        var result = NameProvider.ParserNameToSwift(decl);
        Assert.Equal("`class`", result);
    }

    [Fact]
    public void ParserNameToSwift_Decl_NonPrefixed_SwiftKeyword()
    {
        // Method named "subscript" — no parser escaping needed (not a C# keyword).
        var decl = MakeMethodDecl("subscript");
        var result = NameProvider.ParserNameToSwift(decl);
        Assert.Equal("`subscript`", result);
    }

    [Fact]
    public void ParserNameToSwift_Decl_NonPrefixed_NonKeyword()
    {
        var decl = MakeMethodDecl("normalName");
        var result = NameProvider.ParserNameToSwift(decl);
        Assert.Equal("normalName", result);
    }

    [Fact]
    public void ParserNameToSwift_Decl_ParserEscapedValue_StrippedNotBackticked()
    {
        // "value" is a C# keyword but NOT a Swift keyword.
        var decl = MakeMethodDecl("_value", originalSwiftName: "value");
        var result = NameProvider.ParserNameToSwift(decl);
        Assert.Equal("value", result);
    }

    #endregion

    #region ParserNameToSwift — String Fallback (for derived names, obsolete)

#pragma warning disable CS0618 // Obsolete — testing the fallback deliberately
    [Fact]
    public void ParserNameToSwift_String_EscapedDefault_StrippedAndBackticked()
    {
        // String fallback for accessor-derived names like "default_Get" → "default".
        var result = NameProvider.ParserNameToSwift("_default");
        Assert.Equal("`default`", result);
    }

    [Fact]
    public void ParserNameToSwift_String_NonKeyword_Unchanged()
    {
        var result = NameProvider.ParserNameToSwift("normalName");
        Assert.Equal("normalName", result);
    }

    [Fact]
    public void ParserNameToSwift_String_SwiftKeyword_Backticked()
    {
        var result = NameProvider.ParserNameToSwift("subscript");
        Assert.Equal("`subscript`", result);
    }
#pragma warning restore CS0618

    #endregion

    #region ComputeCaseNameMap — Case-Insensitive Collision Avoidance

    private static List<EnumCaseDecl> MakeCases(params string[] names)
        => names.Select(n => new EnumCaseDecl { Name = n, MangledName = $"$s_mangled_{n}", ParentDecl = null, ModuleDecl = null }).ToList();

    [Fact]
    public void ComputeCaseNameMap_NoCaseCollisions_ReturnsNull()
    {
        var cases = MakeCases("north", "south", "east", "west");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.Null(map);
    }

    [Fact]
    public void ComputeCaseNameMap_CaseSensitiveCollision_AppendsNumericSuffix()
    {
        // SVG path segment pattern: M (absolute) vs m (relative)
        var cases = MakeCases("M", "m");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.NotNull(map);
        Assert.Equal("M", map["M"]);
        Assert.Equal("M2", map["m"]);
    }

    [Fact]
    public void ComputeCaseNameMap_MultipleCollisions_IncrementsSuffix()
    {
        // Three cases that all collapse to "Foo"
        var cases = MakeCases("Foo", "foo", "FOO");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.NotNull(map);
        Assert.Equal("Foo", map["Foo"]);
        Assert.Equal("Foo2", map["foo"]);
        Assert.Equal("Foo3", map["FOO"]);
    }

    [Fact]
    public void ComputeCaseNameMap_SVGPathSegmentPattern_AllCasesUnique()
    {
        // Full SVG path segment type: uppercase (absolute) and lowercase (relative)
        var cases = MakeCases("M", "L", "C", "Q", "A", "Z", "H", "V", "S", "T",
                              "m", "l", "c", "q", "a", "h", "v", "s", "t", "e", "E");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.NotNull(map);

        // Verify all mapped names are unique (case-insensitive)
        var allNames = map.Values.ToList();
        var uniqueNames = new HashSet<string>(allNames, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(allNames.Count, uniqueNames.Count);

        // First occurrences keep their original PascalCase name
        Assert.Equal("M", map["M"]);
        Assert.Equal("L", map["L"]);
        // Lowercase variants get numeric suffixes
        Assert.Equal("M2", map["m"]);
        Assert.Equal("L2", map["l"]);
    }

    [Fact]
    public void GetCaseName_WithNullMap_FallsBackToToPascalCase()
    {
        var result = NameProvider.GetCaseName("north", null);
        Assert.Equal("North", result);
    }

    [Fact]
    public void GetCaseName_WithMap_ReturnsMappedName()
    {
        var cases = MakeCases("M", "m");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.Equal("M", NameProvider.GetCaseName("M", map));
        Assert.Equal("M2", NameProvider.GetCaseName("m", map));
    }

    [Fact]
    public void ComputeCaseNameMap_MixedCollisionsAndNonCollisions_HandlesCorrectly()
    {
        // Only some cases collide
        var cases = MakeCases("open", "Open", "close", "reset");
        var map = NameProvider.ComputeCaseNameMap(cases);
        Assert.NotNull(map);
        Assert.Equal("Open", map["open"]);
        Assert.Equal("Open2", map["Open"]);
        Assert.Equal("Close", map["close"]);
        Assert.Equal("Reset", map["reset"]);
    }

    #endregion
}
