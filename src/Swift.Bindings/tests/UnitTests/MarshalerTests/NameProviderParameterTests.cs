// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NameProvider.GetCSharpParameterName(), IsGeneratedArgName(),
/// StripCSharpKeywordPrefix(), and SanitizeForCSharp().
/// </summary>
public class NameProviderParameterTests
{
    private static ArgumentDecl MakeArg(string name, string privateName = "")
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = privateName,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #region GetCSharpParameterName Tests

    [Fact]
    public void GetCSharpParameterName_PrefersPrivateName()
    {
        var arg = MakeArg("arg0", "count");
        Assert.Equal("count", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_DerivesFromType_ForGeneratedArgs()
    {
        // arg0 with Swift.Int should derive "value" from the type
        var arg = MakeArg("arg0");
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_KeywordEscapedName_UsesVerbatimPrefix()
    {
        // _for → @for (verbatim identifier, valid in C# signatures and references)
        var arg = MakeArg("_for");
        Assert.Equal("@for", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameKeywordSanitized()
    {
        // If the internal Swift name is a C# keyword, use @ verbatim prefix
        var arg = MakeArg("arg0", "class");
        Assert.Equal("@class", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_RegularNamePassthrough()
    {
        var arg = MakeArg("name");
        Assert.Equal("name", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameStartingWithDigit()
    {
        var arg = MakeArg("arg0", "3dPoint");
        Assert.Equal("_3dPoint", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameOverridesKeywordEscaped()
    {
        // Even when Name is keyword-escaped, PrivateName takes precedence
        var arg = MakeArg("_for", "range");
        Assert.Equal("range", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_Underscore_DerivesFromType()
    {
        var arg = MakeArg("_");
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_Underscore_PrivateNameWins()
    {
        var arg = MakeArg("_", "count");
        Assert.Equal("count", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_Event_UsesVerbatimPrefix()
    {
        // Mixpanel: Track(string? @event) — not _event
        var arg = MakeArg("_event");
        Assert.Equal("@event", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_Value_ContextualKeyword()
    {
        // "value" is a contextual keyword — safe as parameter name
        var arg = MakeArg("_value");
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void DeduplicateParameterNamesForParameterList_MultipleUnderscore_GeneratesUniqueNames()
    {
        var parameters = new List<ArgumentDecl>
        {
            MakeArgWithType("_", "Swift.Int"),
            MakeArgWithType("_", "Swift.String"),
            MakeArgWithType("_", "Swift.Int")
        };

        NameProvider.DeduplicateParameterNamesForParameterList(parameters);

        Assert.Equal("value", parameters[0].CSharpName);
        Assert.Equal("value2", parameters[1].CSharpName);
        Assert.Equal("value3", parameters[2].CSharpName);
    }

    #endregion

    #region IsGeneratedArgName Tests

    [Fact]
    public void IsGeneratedArgName_Arg0_ReturnsTrue()
    {
        Assert.True(NameProvider.IsGeneratedArgName("arg0"));
    }

    [Fact]
    public void IsGeneratedArgName_Arg12_ReturnsTrue()
    {
        Assert.True(NameProvider.IsGeneratedArgName("arg12"));
    }

    [Fact]
    public void IsGeneratedArgName_ArgOnly_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName("arg"));
    }

    [Fact]
    public void IsGeneratedArgName_RegularName_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName("value"));
    }

    [Fact]
    public void IsGeneratedArgName_Null_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName(null));
    }

    [Fact]
    public void IsGeneratedArgName_Empty_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName(""));
    }

    #endregion

    #region StripCSharpKeywordPrefix Tests

    [Fact]
    public void StripCSharpKeywordPrefix_ForKeyword()
    {
        Assert.Equal("for", NameProvider.StripCSharpKeywordPrefix("_for"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_UsingKeyword()
    {
        Assert.Equal("using", NameProvider.StripCSharpKeywordPrefix("_using"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_NonKeywordWithUnderscore()
    {
        // _data is NOT a keyword-escaped name since "data" is not a C# keyword
        Assert.Equal("_data", NameProvider.StripCSharpKeywordPrefix("_data"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_NoPrefix()
    {
        Assert.Equal("name", NameProvider.StripCSharpKeywordPrefix("name"));
    }

    #endregion

    #region Type-Derived Parameter Name Tests (WU4)

    private static ArgumentDecl MakeArgWithType(string name, string swiftType, string privateName = "")
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = privateName,
            SwiftTypeSpec = new NamedTypeSpec(swiftType),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    [Fact]
    public void Arg0_UIImage_BecomesImage()
    {
        var arg = MakeArgWithType("arg0", "UIKit.UIImage");
        Assert.Equal("image", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void Arg0_String_BecomesValue()
    {
        var arg = MakeArgWithType("arg0", "Swift.String");
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void Arg0_ImageRequest_BecomesImageRequest()
    {
        var arg = MakeArgWithType("arg0", "Nuke.ImageRequest");
        Assert.Equal("imageRequest", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void Arg0_NSUrl_BecomesUrl()
    {
        var arg = MakeArgWithType("arg0", "Foundation.NSURL");
        // NSURL → strip NS → URL → camelCase → uRL... hmm, actually it should be "nsurl"
        // NSURL has third char 'U' uppercase, so strip NS → URL → url
        var result = NameProvider.GetCSharpParameterName(arg);
        Assert.Equal("url", result);
    }

    [Fact]
    public void Arg1_Int_BecomesValue1()
    {
        // Second generated arg gets numeric suffix
        var arg = MakeArgWithType("arg1", "Swift.Int");
        Assert.Equal("value1", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void Arg0_Bool_BecomesFlag()
    {
        var arg = MakeArgWithType("arg0", "Swift.Bool");
        Assert.Equal("flag", NameProvider.GetCSharpParameterName(arg));
    }

    [Theory]
    // Repro for gap-0.10.0-underscore-argument-labels-leak-as-parameter-names.md.
    // Apple numeric typealiases (CGFloat, TimeInterval, NSInteger, NSUInteger,
    // NSTimeInterval) are semantically primitive doubles/ints. Pre-fix, the
    // emitter's DeriveParameterNameFromType camelcased the typedef name into
    // nonsense parameter names like `cGFloat` / `nSInteger` (Lottie:6249,
    // SDK-0.10.0-BLOCKERS Round 4 / M-8). Post-fix, all five collapse to the
    // generic "value" — matching the existing `Swift.Double` / `Swift.Int` shape.
    [InlineData("CGFloat")]
    [InlineData("TimeInterval")]
    [InlineData("NSTimeInterval")]
    [InlineData("NSInteger")]
    [InlineData("NSUInteger")]
    public void Arg0_AppleNumericAlias_BecomesValue(string typeName)
    {
        var arg = MakeArgWithType("arg0", typeName);
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Theory]
    [InlineData("CGFloat")]
    [InlineData("TimeInterval")]
    public void Underscore_AppleNumericAlias_BecomesValue(string typeName)
    {
        // Same fix on the literal underscore-argument-name path.
        var arg = MakeArgWithType("_", typeName);
        Assert.Equal("value", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void PrivateName_Preferred_OverTypeDerivation()
    {
        // PrivateName always wins even for arg0
        var arg = MakeArgWithType("arg0", "UIKit.UIImage", "photo");
        Assert.Equal("photo", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void RegularName_Unchanged()
    {
        // Non-generated names pass through unchanged
        var arg = MakeArgWithType("request", "Nuke.ImageRequest");
        Assert.Equal("request", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void Arg0_CSharpKeyword_Sanitized()
    {
        // Type that would derive a C# keyword (e.g., "Object" → "object" → "@object")
        var arg = MakeArgWithType("arg0", "Swift.Object");
        Assert.Equal("@object", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_Unchanged_ForNonGenerated()
    {
        // Verify the original function is unaffected for non-generated names
        var arg = MakeArg("name");
        Assert.Equal("name", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_NestedType_UsesLeafName()
    {
        // Nested types like "Nuke.ImageRequest.ThumbnailOptions" must produce
        // "thumbnailOptions" (leaf name only), not "imageRequest.ThumbnailOptions"
        // (which contains a dot and is an invalid C# identifier).
        var arg = MakeArgWithType("arg0", "Nuke.ImageRequest.ThumbnailOptions");
        var result = NameProvider.GetCSharpParameterName(arg);
        Assert.Equal("thumbnailOptions", result);
        Assert.DoesNotContain(".", result);
    }

    [Fact]
    public void GetCSharpParameterName_DeeplyNestedType_UsesLeafName()
    {
        // Even deeper nesting should still use only the leaf type name.
        var arg = MakeArgWithType("arg0", "Module.Outer.Middle.Inner");
        var result = NameProvider.GetCSharpParameterName(arg);
        Assert.Equal("inner", result);
        Assert.DoesNotContain(".", result);
    }

    #endregion

    #region EscapeForCSharpSignature Tests

    [Theory]
    [InlineData("event", "@event")]
    [InlineData("for", "@for")]
    [InlineData("class", "@class")]
    [InlineData("object", "@object")]
    [InlineData("string", "@string")]
    [InlineData("decimal", "@decimal")]
    [InlineData("char", "@char")]
    [InlineData("byte", "@byte")]
    [InlineData("uint", "@uint")]
    [InlineData("lock", "@lock")]
    public void EscapeForCSharpSignature_Keyword_AddsVerbatimPrefix(string input, string expected)
    {
        Assert.Equal(expected, NameProvider.EscapeForCSharpSignature(input));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("image")]
    public void EscapeForCSharpSignature_NonKeyword_PassesThrough(string input)
    {
        Assert.Equal(input, NameProvider.EscapeForCSharpSignature(input));
    }

    [Fact]
    public void EscapeForCSharpSignature_EndToEnd_EventParam()
    {
        // Full pipeline: _event → GetCSharpParameterName → "event" → EscapeForCSharpSignature → "@event"
        var arg = MakeArg("_event");
        var bare = NameProvider.GetCSharpParameterName(arg);
        var escaped = NameProvider.EscapeForCSharpSignature(bare);
        Assert.Equal("@event", escaped);
    }

    #endregion

    #region Swift Keyword Escaping

    [Theory]
    [InlineData("protocol")]
    [InlineData("class")]
    [InlineData("func")]
    [InlineData("import")]
    [InlineData("let")]
    [InlineData("var")]
    [InlineData("self")]
    [InlineData("return")]
    [InlineData("in")]
    [InlineData("where")]
    public void IsSwiftKeyword_ReservedWord_ReturnsTrue(string keyword)
    {
        Assert.True(NameProvider.IsSwiftKeyword(keyword));
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("class")]
    [InlineData("self")]
    [InlineData("in")]
    public void EscapeSwiftKeyword_ReservedWord_AddBackticks(string keyword)
    {
        Assert.Equal($"`{keyword}`", NameProvider.EscapeSwiftKeyword(keyword));
    }

    [Theory]
    [InlineData("value")]
    [InlineData("name")]
    [InlineData("count")]
    [InlineData("Protocol")]  // PascalCase is NOT a keyword
    [InlineData("classes")]
    public void EscapeSwiftKeyword_NonKeyword_PassesThrough(string name)
    {
        Assert.Equal(name, NameProvider.EscapeSwiftKeyword(name));
    }

    [Fact]
    public void IsSwiftKeyword_Empty_ReturnsFalse()
    {
        Assert.False(NameProvider.IsSwiftKeyword(""));
    }

    #endregion

    #region StripVerbatimPrefix Tests

    [Theory]
    [InlineData("@in", "in")]
    [InlineData("@for", "for")]
    [InlineData("@class", "class")]
    [InlineData("@operator", "operator")]
    [InlineData("@event", "event")]
    [InlineData("@object", "object")]
    public void StripVerbatimPrefix_KeywordPrefixed_StripsAt(string input, string expected)
    {
        Assert.Equal(expected, NameProvider.StripVerbatimPrefix(input));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("count")]
    [InlineData("value")]
    public void StripVerbatimPrefix_NonPrefixed_PassesThrough(string input)
    {
        Assert.Equal(input, NameProvider.StripVerbatimPrefix(input));
    }

    [Fact]
    public void StripVerbatimPrefix_CompoundNamePattern_ProducesValidIdentifier()
    {
        // Verifies the fix for S1: compound variable names with keyword params.
        // @in → stripped to "in", then compound "__in" is valid C#.
        // Without fix: "__@in" (@ mid-identifier) is invalid C#.
        var name = "@in";
        var bareName = NameProvider.StripVerbatimPrefix(name);
        var compoundName = $"__{bareName}";

        Assert.Equal("__in", compoundName);
        Assert.DoesNotContain("@", compoundName);
    }

    #endregion

    #region ToPascalCase — SCREAMING_CASE conversion (WU3)

    [Theory]
    [InlineData("HIDDEN", "Hidden")]
    [InlineData("CAMERA_DIRECTION", "CameraDirection")]
    [InlineData("EASING_MODE", "EasingMode")]
    [InlineData("MARKER_ANCHOR", "MarkerAnchor")]
    [InlineData("MP3", "Mp3")]                        // all-caps with digit
    [InlineData("hidden", "Hidden")]                   // existing camelCase preserved
    [InlineData("CameraDirection", "CameraDirection")] // already PascalCase preserved
    [InlineData("A", "A")]                             // single char, not SCREAMING_CASE
    [InlineData("", "")]                               // empty preserved
    [InlineData("URL", "Url")]                         // all-caps 2+ chars is SCREAMING_CASE
    public void ToPascalCase_ScreamingCase_ConvertedCorrectly(string input, string expected)
    {
        Assert.Equal(expected, NameProvider.ToPascalCase(input));
    }

    #endregion

    #region ToPascalCaseForTypeName — type-level casing (underscore abbreviation patterns)

    [Theory]
    [InlineData("CAMERA_DIRECTION", "CameraDirection")] // true SCREAMING_CASE → PascalCase
    [InlineData("THING_KEY", "ThingKey")]               // true SCREAMING_CASE → PascalCase
    [InlineData("EASING_MODE", "EasingMode")]           // true SCREAMING_CASE → PascalCase
    [InlineData("F0_S1", "F0_S1")]                      // abbreviation pattern (single-letter segments) → unchanged
    [InlineData("F10_S4", "F10_S4")]                    // abbreviation pattern with multi-digit → unchanged
    [InlineData("F2_S2_S0_S0", "F2_S2_S0_S0")]         // deeply nested abbreviation → unchanged
    [InlineData("NF3_S0", "Nf3S0")]                      // NF has 2 consecutive letters → converts
    [InlineData("E1_S2", "E1_S2")]                      // enum test pattern → unchanged
    [InlineData("URL", "URL")]                          // all-caps no underscore → unchanged
    [InlineData("F9S1", "F9S1")]                        // all-caps abbreviation → unchanged
    [InlineData("pixelFormat", "PixelFormat")]           // camelCase → PascalCase
    [InlineData("ImageRequest", "ImageRequest")]         // already PascalCase → unchanged
    [InlineData("", "")]                                 // empty → empty
    public void ToPascalCaseForTypeName_ConvertedCorrectly(string input, string expected)
    {
        Assert.Equal(expected, NameProvider.ToPascalCaseForTypeName(input));
    }

    #endregion
}
