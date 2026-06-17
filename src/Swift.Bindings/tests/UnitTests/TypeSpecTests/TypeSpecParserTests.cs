// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class TypeSpecParserTests : IClassFixture<TypeSpecParserTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public TypeSpecParserTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }

        private static void InitializeResources()
        {
        }
    }

    [Fact]
    public static void TestNamedBasicName()
    {
        var ts = TypeSpecParser.Parse("thisIsAName");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("thisIsAName", ns.Name);
    }

    [Fact]
    public static void TestNamedGeneric()
    {
        var ts = TypeSpecParser.Parse("thisIsAName<a, b, c>");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("thisIsAName", ns.Name);
        Assert.Equal(3, ns.GenericParameters.Count);
        var ns1 = ns.GenericParameters[0] as NamedTypeSpec;
        Assert.NotNull(ns1);
        Assert.Equal("a", ns1.Name);
        ns1 = ns.GenericParameters[1] as NamedTypeSpec;
        Assert.NotNull(ns1);
        Assert.Equal("b", ns1.Name);
        ns1 = ns.GenericParameters[2] as NamedTypeSpec;
        Assert.NotNull(ns1);
        Assert.Equal("c", ns1.Name);
    }

    [Fact]
    public static void TestEmptyTuple()
    {
        var tuple = TypeSpecParser.Parse("()") as TupleTypeSpec;
        Assert.NotNull(tuple);
        Assert.Empty(tuple.Elements);
    }

    [Fact]
    public static void TestSingleTuple()
    {
        var ns = TypeSpecParser.Parse("Swift.Int") as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
    }

    [Fact]
    public static void TestDoubleTuple()
    {
        var tuple = TypeSpecParser.Parse("(Swift.Int, Swift.Float)") as TupleTypeSpec;
        Assert.NotNull(tuple);
        Assert.Equal(2, tuple.Elements.Count);
        var ns = tuple.Elements[0] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
        ns = tuple.Elements[1] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Float", ns.Name);
    }

    [Fact]
    public static void TestNestedTuple()
    {
        var tuple = TypeSpecParser.Parse("(Swift.Int, (Swift.Int, Swift.Int))") as TupleTypeSpec;
        Assert.NotNull(tuple);
        Assert.Equal(2, tuple.Elements.Count);
        var ns = tuple.Elements[0] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
        tuple = tuple.Elements[1] as TupleTypeSpec;
        Assert.NotNull(tuple);
        Assert.Equal(2, tuple.Elements.Count);
        ns = tuple.Elements[0] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
        ns = tuple.Elements[1] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
    }

    [Fact]
    public static void TestFuncIntInt()
    {
        var close = TypeSpecParser.Parse("Swift.Int -> Swift.Int") as ClosureTypeSpec;
        Assert.NotNull(close);
        var ns = close.Arguments as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
        ns = close.ReturnType as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
    }


    [Fact]
    public static void TestFuncVoidVoid()
    {
        var close = TypeSpecParser.Parse("() -> ()") as ClosureTypeSpec;
        Assert.NotNull(close);
        var ts = close.Arguments as TupleTypeSpec;
        Assert.NotNull(ts);
        Assert.Empty(ts.Elements);
        ts = close.ReturnType as TupleTypeSpec;
        Assert.NotNull(ts);
        Assert.Empty(ts.Elements);
    }

    [Fact]
    public static void TestArrayOfInt()
    {
        var ns = TypeSpecParser.Parse("Swift.Array<Swift.Int>") as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Array", ns.Name);
        Assert.True(ns.ContainsGenericParameters);
        Assert.Single(ns.GenericParameters);
        ns = ns.GenericParameters[0] as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Int", ns.Name);
    }

    [Fact]
    public static void TestDictionaryOfIntString()
    {
        var ns = TypeSpecParser.Parse("Swift.Dictionary<Swift.Int, Swift.String>") as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Dictionary", ns.Name);
        Assert.True(ns.ContainsGenericParameters);
        Assert.Equal(2, ns.GenericParameters.Count);
        var ns1 = ns.GenericParameters[0] as NamedTypeSpec;
        Assert.NotNull(ns1);
        Assert.Equal("Swift.Int", ns1.Name);
        ns1 = ns.GenericParameters[1] as NamedTypeSpec;
        Assert.NotNull(ns1);
        Assert.Equal("Swift.String", ns1.Name);
    }

    [Fact]
    public static void TestWithAttributes()
    {
        var tupled = TypeSpecParser.Parse("(Builtin.RawPointer, (@convention[thin] (Builtin.RawPointer, inout Builtin.UnsafeValueBuffer, inout SomeModule.Foo, @thick SomeModule.Foo.Type) -> ())?)")
            as TupleTypeSpec;
        Assert.NotNull(tupled);
        var ns = tupled.Elements[1] as NamedTypeSpec;
        Assert.True(ns.ContainsGenericParameters);
        Assert.Equal("Swift.Optional", ns.Name);
        var close = ns.GenericParameters[0] as ClosureTypeSpec;
        Assert.Single(close.Attributes);
    }

    [Fact]
    public static void TestEmbeddedClass()
    {
        var ns = TypeSpecParser.Parse("Swift.Dictionary<Swift.String, T>.Index") as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.NotNull(ns.InnerType);
        Assert.Equal("Index", ns.InnerType.Name);
        Assert.Equal("Swift.Dictionary<Swift.String, T>.Index", ns.ToString());
    }

    [Fact]
    public static void TestProtocolListAlphabetical()
    {
        var specs = new NamedTypeSpec[] {
            new NamedTypeSpec ("Cfoo"),
            new NamedTypeSpec ("Afoo"),
            new NamedTypeSpec ("Dfoo"),
            new NamedTypeSpec ("Bfoo")
        };

        var protos = new ProtocolListTypeSpec(specs);
        Assert.Equal("Afoo & Bfoo & Cfoo & Dfoo", protos.ToString());
    }

    [Fact]
    public static void TestProtocolListParseSimple()
    {
        var protocolListType = TypeSpecParser.Parse("c & b & a") as ProtocolListTypeSpec;
        Assert.NotNull(protocolListType);
        Assert.Equal(3, protocolListType.Protocols.Count);
        Assert.Equal("a & b & c", protocolListType.ToString());
    }

    [Fact]
    public static void TestProtocolListParseNoSpacesBecauseWhyNot()
    {
        var protocolListType = TypeSpecParser.Parse("c&b&a") as ProtocolListTypeSpec;
        Assert.NotNull(protocolListType);
        Assert.Equal(3, protocolListType.Protocols.Count);
        Assert.Equal("a & b & c", protocolListType.ToString());
    }

    [Fact]
    public static void TestReplaceInNameSuccess()
    {
        var inType = TypeSpecParser.Parse("Foo.Bar");
        var replaced = inType.ReplaceName("Foo.Bar", "Slarty.Bartfast") as NamedTypeSpec;
        Assert.NotNull(replaced);
        Assert.Equal("Slarty.Bartfast", replaced.Name);
    }

    [Fact]
    public static void TestReplaceInNameFail()
    {
        var inType = TypeSpecParser.Parse("Foo.Bar");
        var same = inType.ReplaceName("Blah", "Slarty.Bartfast") as NamedTypeSpec;
        Assert.Equal(same, inType);
    }

    [Fact]
    public static void TestReplaceInTupleSuccess()
    {
        var inType = TypeSpecParser.Parse("(Swift.Int, Foo.Bar, Foo.Bar)");
        var replaced = inType.ReplaceName("Foo.Bar", "Slarty.Bartfast") as TupleTypeSpec;
        Assert.NotNull(replaced);
        var name = replaced.Elements[1] as NamedTypeSpec;
        Assert.NotNull(name);
        Assert.Equal("Slarty.Bartfast", name.Name);
        name = replaced.Elements[2] as NamedTypeSpec;
        Assert.NotNull(name);
        Assert.Equal("Slarty.Bartfast", name.Name);
    }

    [Fact]
    public static void TestReplaceInTupleFail()
    {
        var inType = TypeSpecParser.Parse("(Swift.Int, Foo.Bar, Foo.Bar)");
        var same = inType.ReplaceName("Blah", "Slarty.Bartfast") as TupleTypeSpec;
        Assert.Equal(same, inType);
    }


    [Fact]
    public static void TestReplaceInClosureSuccess()
    {
        var inType = TypeSpecParser.Parse("(Swift.Int, Foo.Bar) -> Foo.Bar");
        var replaced = inType.ReplaceName("Foo.Bar", "Slarty.Bartfast") as ClosureTypeSpec;
        Assert.NotNull(replaced);
        var args = replaced.Arguments as TupleTypeSpec;
        Assert.NotNull(args);
        Assert.Equal(2, args.Elements.Count);
        var name = args.Elements[1] as NamedTypeSpec;
        Assert.Equal("Slarty.Bartfast", name.Name);
        name = replaced.ReturnType as NamedTypeSpec;
        Assert.Equal("Slarty.Bartfast", name.Name);
    }

    [Fact]
    public static void TestReplaceInClosureFail()
    {
        var inType = TypeSpecParser.Parse("(Swift.Int, Foo.Bar) -> Foo.Bar");
        var same = inType.ReplaceName("Blah", "Slarty.Bartfast") as ClosureTypeSpec;
        Assert.NotNull(same);
        Assert.Equal(same, inType);
    }

    [Fact]
    public static void TestReplaceInProtoListSuccess()
    {
        var inType = TypeSpecParser.Parse("Swift.Equatable & Foo.Bar");
        var replaced = inType.ReplaceName("Foo.Bar", "Slarty.Bartfast") as ProtocolListTypeSpec;
        Assert.NotNull(replaced);
        var name = replaced.Protocols.Keys.FirstOrDefault(n => n.Name == "Slarty.Bartfast");
        Assert.NotNull(name);
    }

    [Fact]
    public static void TestReplaceInProtoListFail()
    {
        var inType = TypeSpecParser.Parse("Swift.Equatable & Foo.Bar");
        var same = inType.ReplaceName("Blah", "Slarty.Bartfast") as ProtocolListTypeSpec;
        Assert.Equal(same, inType);
    }

    [Fact]
    public static void TestWeirdClosureIssue()
    {
        var inType = TypeSpecParser.Parse("@escaping[] (_onAnimation:Swift.Bool)->Swift.Void");
        Assert.True(inType is ClosureTypeSpec);
        var closSpec = inType as ClosureTypeSpec;
        Assert.True(closSpec.IsEscaping);
        var textRep = closSpec.ToString();
        var firstIndex = textRep.IndexOf("_onAnimation");
        var lastIndex = textRep.LastIndexOf("_onAnimation");
        Assert.True(firstIndex == lastIndex);
    }

    [Fact]
    public static void TestAsyncClosure()
    {
        var inType = TypeSpecParser.Parse("() async -> ()") as ClosureTypeSpec;
        Assert.NotNull(inType);
        Assert.True(inType.IsAsync);
        Assert.False(inType.Throws);
    }

    [Fact]
    public static void TestAsyncThrowsClosure()
    {
        var inType = TypeSpecParser.Parse("() async throws -> ()") as ClosureTypeSpec;
        Assert.NotNull(inType);
        Assert.True(inType.IsAsync);
        Assert.True(inType.Throws);
    }

    [Fact]
    public static void TestThrowsClosure()
    {
        var inType = TypeSpecParser.Parse("() throws -> ()") as ClosureTypeSpec;
        Assert.NotNull(inType);
        Assert.False(inType.IsAsync);
        Assert.True(inType.Throws);
    }

    [Fact]
    public static void TestThrowBadArrow()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("(Swift.Int)-=>(Swift.Int)"); });
    }

    [Fact]
    public static void TestIllegalNameChar()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("Swift#Int"); });
    }

    [Fact]
    public static void TestBadStartToken()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse(")"); });
    }

    [Fact]
    public static void TestBadClosureToken()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("() throws ? -> )"); });
    }

    [Fact]
    public static void TestInnerClass1()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("().Foo"); });
    }

    [Fact]
    public static void TestProtoListFail()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("Foo & ()"); });
    }

    [Fact]
    public static void TestAttributeFail()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("@&Foo"); });
    }

    [Fact]
    public static void TestListFail()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("Swift.Foo<A, &>"); });
    }

    [Fact]
    public static void TestArrayFail1()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("[&]"); });
    }

    [Fact]
    public static void TestArrayFail2()
    {
        Assert.Throws<TypeSpecParseException>(() => { TypeSpecParser.Parse("[Swift.Int : ?]"); });
    }

    [Fact]
    public static void TestGenericTypeParamName()
    {
        // Generic type parameters in Swift ABI use τ_0_0 style names
        var ts = TypeSpecParser.Parse("τ_0_0");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("τ_0_0", ns.Name);
    }

    [Fact]
    public static void TestGenericTypeParamNameT()
    {
        // Some generic type parameters have friendly names like T
        var ts = TypeSpecParser.Parse("T");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("T", ns.Name);
    }

    [Fact]
    public static void TestGenericTypeParamWithIndex()
    {
        // τ_0_1 = second generic parameter at depth 0
        var ts = TypeSpecParser.Parse("τ_0_1");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("τ_0_1", ns.Name);
    }

    [Fact]
    public static void TestGenericTypeParamNestedDepth()
    {
        // τ_1_0 = first generic parameter at depth 1 (nested generic context)
        var ts = TypeSpecParser.Parse("τ_1_0");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("τ_1_0", ns.Name);
    }

    [Fact]
    public static void TestAnySwiftError()
    {
        // Test parsing "any Swift.Error" - an existential type
        var ts = TypeSpecParser.Parse("any Swift.Error");
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Error", ns.Name);
        Assert.True(ns.IsAny);
    }

    [Fact]
    public static void TestLabeledTupleWithExistential()
    {
        // Test parsing "(error: any Swift.Error)" - a labeled tuple with existential
        var ts = TypeSpecParser.Parse("(error: any Swift.Error)");

        // Single-element tuple should be unwrapped to the inner type
        var ns = ts as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Swift.Error", ns.Name);
        Assert.True(ns.IsAny);
        Assert.Equal("error", ns.TypeLabel);
    }

    [Fact]
    public static void ParsingLabeledVoidElement_DoesNotCorruptSharedEmptyTupleSingleton()
    {
        // Regression: parsing a `Swift.Void` type used to return the shared TupleTypeSpec.Empty
        // singleton, and the per-occurrence TypeLabel was then written onto whatever instance came
        // back. A labeled void element therefore stamped its label onto the GLOBAL empty tuple, so
        // every empty tuple in the process afterwards rendered "label: ()" instead of "()" — even a
        // freshly constructed `() -> ()` closure. The void type must be a fresh instance so the
        // label stays local to the element. (Same bug class as the demangler's ConvertTuple, which
        // also stopped handing out the singleton.)
        var ts = TypeSpecParser.Parse("(first: Swift.Void, second: Swift.Int)") as TupleTypeSpec;
        Assert.NotNull(ts);
        Assert.Equal("first", ts!.Elements[0].TypeLabel);            // the local element carries the label...
        Assert.True(ts.Elements[0] is TupleTypeSpec inner && inner.IsEmptyTuple); // ...and it is an empty tuple
        Assert.Equal("()", TupleTypeSpec.Empty.ToString());          // ...but the shared singleton stays pristine
        Assert.Equal("() -> ()", new ClosureTypeSpec(null, null).ToString());
    }

    // --- Finding 49: EOF-strict canonical entry point ---

    [Theory]
    [InlineData("Swift.Int garbage")]      // the canonical example from the finding
    [InlineData("Swift.Int Swift.Float")]  // two complete types, no separator
    [InlineData("(Swift.Int) trailing")]   // trailing token after a tuple
    [InlineData("Swift.Int, Swift.Float")] // top-level comma is trailing at the entry point
    [InlineData("Swift.Array<Swift.Int> extra")]
    public static void Parse_RejectsTrailingTokens(string input)
    {
        // The canonical entry point must reject a complete type followed by anything else,
        // rather than silently returning the leading prefix.
        Assert.Throws<TypeSpecParseException>(() => TypeSpecParser.Parse(input));
    }

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Array<Swift.Int>")]
    [InlineData("(Swift.Int, Swift.Float)")]
    [InlineData("() -> ()")]
    [InlineData("c & b & a")]
    [InlineData("any Swift.Error")]
    public static void Parse_AcceptsCompleteType(string input)
    {
        // A complete, single type with no trailing tokens must parse cleanly.
        var ts = TypeSpecParser.Parse(input);
        Assert.NotNull(ts);
    }

    [Fact]
    public static void ParsePrefix_IgnoresTrailingTokens()
    {
        // The explicit non-strict variant preserves the historical lenient behavior:
        // it parses the leading type and ignores whatever follows.
        var ts = TypeSpecParser.ParsePrefix("Swift.Int garbage") as NamedTypeSpec;
        Assert.NotNull(ts);
        Assert.Equal("Swift.Int", ts.Name);
    }

    [Fact]
    public static void ParsePrefix_MatchesParse_ForCompleteType()
    {
        // For a well-formed complete type, the strict and non-strict entry points agree.
        var strict = TypeSpecParser.Parse("Swift.Array<Swift.Int>") as NamedTypeSpec;
        var prefix = TypeSpecParser.ParsePrefix("Swift.Array<Swift.Int>") as NamedTypeSpec;
        Assert.NotNull(strict);
        Assert.NotNull(prefix);
        Assert.Equal(strict.ToString(), prefix.ToString());
    }

    [Fact]
    public static void ParsePrefix_StillThrowsOnMalformedPrefix()
    {
        // Lenience is only about trailing tokens — a malformed prefix still throws.
        Assert.Throws<TypeSpecParseException>(() => TypeSpecParser.ParsePrefix("Swift#Int"));
    }

    [Theory]
    [InlineData("T where T : Swift.Equatable", "T")]
    [InlineData("Swift.Array<Element> where Element : Swift.Hashable", "Swift.Array")]
    public static void ParsePrefix_ExtractsReturnTypeBeforeWhereClause(string returnTypeSlice, string expectedName)
    {
        // The production contract for the extension-emitter return-type slices
        // (ProtocolExtensionEmitter / ForeignTypeExtensionEmitter): the slice after the
        // top-level "->" can carry a trailing method-level "where" clause. ParsePrefix must
        // extract the leading return type and ignore the where-tail, exactly as the historical
        // lenient parse did — switching those sites to the EOF-strict Parse would instead throw
        // and silently skip the method.
        var ts = TypeSpecParser.ParsePrefix(returnTypeSlice) as NamedTypeSpec;
        Assert.NotNull(ts);
        Assert.Equal(expectedName, ts.Name);
    }

    [Fact]
    public static void ParsePrefix_ExtractsClosureReturnTypeBeforeWhereClause()
    {
        // Same return-type-slice contract for a closure return type: a method like
        // "func f<T>() -> (T) -> Swift.Bool where T : Swift.Equatable" yields the slice
        // "(T) -> Swift.Bool where T : Swift.Equatable". ParsePrefix must return the closure
        // type and ignore the trailing where-clause, not throw on it.
        var ts = TypeSpecParser.ParsePrefix("(T) -> Swift.Bool where T : Swift.Equatable") as ClosureTypeSpec;
        Assert.NotNull(ts);
    }

    // --- Finding 49: ObjectiveC.* -> Foundation.* module alias (relocated out of the grammar) ---

    [Theory]
    [InlineData("ObjectiveC.NSString", "Foundation.NSString")]
    [InlineData("ObjectiveC.NSObject", "Foundation.NSObject")]
    [InlineData("Swift.Int", "Swift.Int")]            // unrelated module — unchanged
    [InlineData("ObjectiveCThing", "ObjectiveCThing")] // not the "ObjectiveC." prefix — unchanged
    public static void SwiftModuleAliases_NormalizeTypeName(string input, string expected)
    {
        Assert.Equal(expected, SwiftModuleAliases.NormalizeTypeName(input));
    }

    [Fact]
    public static void Parse_AppliesObjectiveCToFoundationAlias()
    {
        var ns = TypeSpecParser.Parse("ObjectiveC.NSString") as NamedTypeSpec;
        Assert.NotNull(ns);
        Assert.Equal("Foundation.NSString", ns.Name);
    }
}
