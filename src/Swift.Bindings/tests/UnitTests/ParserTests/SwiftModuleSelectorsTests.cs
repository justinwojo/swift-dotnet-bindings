// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SwiftModuleSelectors"/>: Swift 6.4 interface text renders as the
/// dotted spelling Swift 6.2 interfaces and every ABI JSON use. Inputs are spelled as the
/// Xcode 27.2 compiler prints them.
/// </summary>
public class SwiftModuleSelectorsTests
{
    [Theory]
    [InlineData("extension M::S : M::_P {", "extension M.S : M._P {")]
    // Every component of a nested name carries the declaring module; only the leading one is a prefix.
    [InlineData("public func f(_ s: M::S.M::Inner)", "public func f(_ s: M.S.Inner)")]
    [InlineData("Translation::LanguageAvailability.Translation::Status", "Translation.LanguageAvailability.Status")]
    // A nested type another module declares keeps the extended type's module as its prefix.
    [InlineData("Foundation::AttributeScopes.Translation::TranslationAttributes", "Foundation.AttributeScopes.TranslationAttributes")]
    [InlineData("[Swift::String : Swift::Int]", "[Swift.String : Swift.Int]")]
    [InlineData("Swift::KeyPath<Foundation::AttributeScopes, T> where T : Foundation::AttributedStringKey",
                "Swift.KeyPath<Foundation.AttributeScopes, T> where T : Foundation.AttributedStringKey")]
    [InlineData("any AppIntents::_IntentValue.Type", "any AppIntents._IntentValue.Type")]
    [InlineData("extension `Kit`::Thing {}", "extension `Kit`.Thing {}")]
    [InlineData("Self.Specification", "Self.Specification")]
    // `...` is a range operator, not a member access: the selector after it leads a name.
    [InlineData("0 ... Swift::Int.max", "0 ... Swift.Int.max")]
    [InlineData("a..<Swift::Int.max", "a..<Swift.Int.max")]
    // A comment between the dot and the member does not hide the member access.
    [InlineData("Kit::Outer./* note */Kit::Inner", "Kit.Outer./* note */Inner")]
    [InlineData("Kit::Outer<T>.Kit::Inner", "Kit.Outer<T>.Inner")]
    [InlineData("x?.Kit::member", "x?.member")]
    // The last dot of a custom dot operator is part of the operator.
    [InlineData("1 .+. Swift::Int.max", "1 .+. Swift.Int.max")]
    [InlineData("Swift::Int/* a. */.Swift::max", "Swift.Int/* a. */.max")]
    public void ToDotted_RendersSelectorsAsDots(string interfaceText, string expected)
        => Assert.Equal(expected, SwiftModuleSelectors.ToDotted(interfaceText));

    [Theory]
    [InlineData("@available(*, deprecated, message: \"use Kit::Thing\") public func f()")]
    [InlineData("// Kit::Thing in a comment\npublic func f()")]
    [InlineData("/* Kit::Thing */ public func f()")]
    [InlineData("let s = \"\"\"\nKit::Thing\n\"\"\"")]
    [InlineData("let s = \"a\\\"Kit::Thing\"")]
    // Swift block comments nest; raw strings close only on a quote with their own `#` count.
    [InlineData("/* outer /* inner */ Kit::Thing */ public func f()")]
    [InlineData("let s = #\"use \"Kit::Thing\" now\"#")]
    [InlineData("let s = ##\"a \"# Kit::Thing\"##")]
    // An interpolation may hold string literals of its own.
    [InlineData("let s = \"\\(String(describing: \"Kit::Thing\"))\"")]
    [InlineData("let s = #\"\\#(f(\")\")) Kit::Thing\"#")]
    // `\#"` escapes a quote inside `#"…"#`.
    [InlineData("let s = #\"a \\#\"# Kit::Thing\"#")]
    [InlineData("let s = #\"\"\"\n\\#\"\"\"# Kit::Thing\n\"\"\"#")]
    [InlineData("let s = #\"\"\"\nKit::Thing \"\"\" Kit::Other\n\"\"\"#")]
    [InlineData("let s = \"\"\"\n\\\"\"\" Kit::Thing\n\"\"\"")]
    public void ToDotted_LeavesLiteralsAndCommentsAlone(string interfaceText)
        => Assert.Equal(interfaceText, SwiftModuleSelectors.ToDotted(interfaceText));

    [Fact]
    public void ToDotted_SelectorFreeText_IsReturnedUnchanged()
    {
        // Swift 6.2 interfaces carry no selectors: readers must see exactly the text they did.
        var text = "extension AppIntents.EntityIdentifier : AppIntents._IntentValue {\n}\n";
        Assert.Same(text, SwiftModuleSelectors.ToDotted(text));
    }

    [Fact]
    public void ToDotted_TextAfterALiteral_IsStillRendered()
    {
        var text = "@available(*, message: \"Kit::A\") extension Kit::B : Kit::C {}";
        Assert.Equal("@available(*, message: \"Kit::A\") extension Kit.B : Kit.C {}", SwiftModuleSelectors.ToDotted(text));
    }

    [Theory]
    [InlineData("/* a /* b */ c */ extension Kit::B {}", "/* a /* b */ c */ extension Kit.B {}")]
    [InlineData("let s = #\"Kit::A\"# ; extension Kit::B {}", "let s = #\"Kit::A\"# ; extension Kit.B {}")]
    [InlineData("#if compiler(>=5.3)\nextension Kit::B {}\n#endif", "#if compiler(>=5.3)\nextension Kit.B {}\n#endif")]
    public void ToDotted_TextAfterANestedCommentOrRawString_IsStillRendered(string interfaceText, string expected)
        => Assert.Equal(expected, SwiftModuleSelectors.ToDotted(interfaceText));
}
