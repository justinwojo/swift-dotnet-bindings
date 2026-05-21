// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Foundation;

namespace RuntimeTestsApp.AppleSupplement;

/// <summary>
/// End-to-end fixture for the hand-rolled <see cref="AttributedString"/>
/// partial layered on top of the generated Foundation.AttributedString
/// shell. The generated half supplies storage + ISwiftObject; the
/// hand-rolled half (Sources/Foundation/AttributedString.cs in
/// Swift.Bindings.Apple) wraps the SBW_AttributedString_* @_cdecl shims
/// exported from <c>SwiftBindingsAppleSupplement.xcframework</c> and
/// exposes a public string constructor, an override of ToString(), and
/// a LanguageIdentifier property that round-trips the
/// @dynamicMemberLookup-keyed Foundation attribute.
///
/// These assertions are the durable contract for that surface — they
/// exercise both directions across the @_cdecl boundary, the heap
/// ownership model that lets COW mutations apply in place, and the
/// Optional&lt;String&gt; behaviour of the attribute getter when no
/// attribute has been applied.
/// </summary>
public class AttributedStringTests : TestBase
{
    public AttributedStringTests(TestResults results) : base(results) { }

    public void TestCtor_FromString_PreservesCharacters()
    {
        using var attr = new AttributedString("Hello, world!");
        AssertEqual("Hello, world!", attr.ToString(), "ToString() round-trip");
    }

    public void TestCtor_FromEmptyString_PreservesEmpty()
    {
        using var attr = new AttributedString("");
        AssertEqual(string.Empty, attr.ToString(), "Empty input round-trips to empty");
    }

    public void TestCtor_FromUnicodeString_PreservesCodepoints()
    {
        // Mix of BMP, surrogate-pair emoji, and non-Latin scripts. Verifies
        // UTF-8 round-trip is lossless across the @_cdecl boundary.
        const string sample = "héllo 🌍 こんにちは";
        using var attr = new AttributedString(sample);
        AssertEqual(sample, attr.ToString(), "Unicode round-trip");
    }

    public void TestLanguageIdentifier_DefaultIsNull()
    {
        using var attr = new AttributedString("Bonjour");
        AssertNull(attr.LanguageIdentifier, "Fresh AttributedString has no language attribute");
    }

    public void TestLanguageIdentifier_SetAndGetRoundTrips()
    {
        using var attr = new AttributedString("Bonjour");
        attr.LanguageIdentifier = "fr";
        AssertEqual("fr", attr.LanguageIdentifier, "Get reads back the value set");
    }

    public void TestLanguageIdentifier_AssignNullClearsAttribute()
    {
        using var attr = new AttributedString("Bonjour");
        attr.LanguageIdentifier = "fr";
        AssertEqual("fr", attr.LanguageIdentifier, "Sanity: language was set");
        attr.LanguageIdentifier = null;
        AssertNull(attr.LanguageIdentifier, "Assigning null removes the attribute");
    }

    public void TestLanguageIdentifier_Reassignment_Wins()
    {
        using var attr = new AttributedString("Bonjour");
        attr.LanguageIdentifier = "fr";
        attr.LanguageIdentifier = "en-US";
        AssertEqual("en-US", attr.LanguageIdentifier, "Latest assignment is observed");
    }

    public void TestToString_AfterAttributeMutation_DropsAttributesButKeepsText()
    {
        // Mutating the languageIdentifier must not corrupt the underlying
        // character storage — String(attrStr.characters) should still
        // return the original text after attribute changes.
        using var attr = new AttributedString("The quick brown fox");
        attr.LanguageIdentifier = "en";
        AssertEqual("The quick brown fox", attr.ToString(),
            "Characters are stable under attribute mutation");
        attr.LanguageIdentifier = null;
        AssertEqual("The quick brown fox", attr.ToString(),
            "Characters are stable when attributes are cleared");
    }
}
