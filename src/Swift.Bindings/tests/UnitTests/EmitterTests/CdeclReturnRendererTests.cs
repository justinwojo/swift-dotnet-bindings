// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Golden-oracle coverage for <see cref="CdeclReturnRenderer"/> — the single source of truth that
/// replaced six hand-copied per-<see cref="CdeclReturnKind"/> return switches across the method,
/// property, and subscript wrapper emitters. Each kind's exact emitted statement(s) are pinned so
/// the consolidation cannot drift, and the three render shapes (inline <c>Write</c>/<c>Lines</c>,
/// result-bound <c>LinesBindingResult</c>, and <c>WriteErrorSentinel</c>) are checked for the
/// equivalence the refactor promised: <c>Write</c> emits exactly what <c>Lines</c> returns, and the
/// ClassPointer retain (<c>Unmanaged.passRetained(... as AnyObject).toOpaque()</c>) appears exactly
/// once per render.
/// </summary>
public class CdeclReturnRendererTests
{
    private const string Expr = "foo";

    // ----- helpers -------------------------------------------------------------------------

    /// <summary>Captures <see cref="CdeclReturnRenderer.Write"/> output as individual lines.</summary>
    private static string[] WriteLines(string valueExpr, TypeSpec spec, ITypeDatabase db,
        CdeclReturnMapping mapping, bool scalarParens)
    {
        var sw = new StringWriter();
        var w = new SwiftWriter(sw);
        CdeclReturnRenderer.Write(w, valueExpr, spec, db, mapping, scalarParens);
        return sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    private static string WriteSentinel(CdeclReturnMapping mapping)
    {
        var sw = new StringWriter();
        var w = new SwiftWriter(sw);
        CdeclReturnRenderer.WriteErrorSentinel(w, mapping);
        return sw.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static CdeclReturnMapping Map(CdeclReturnKind kind, string cdeclType = "Int")
        => new CdeclReturnMapping(cdeclType, kind);

    /// <summary>A type spec whose record either carries a raw value (RangeRepresentable enum) or not.</summary>
    private static (TypeSpec spec, ITypeDatabase db) EnumDb(string? rawValueType)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyEnum"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyEnum"),
                MetadataAccessor = "$s10TestModule6MyEnumOMa",
                Flags = TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType,
            });
        typeDatabase.AddModuleDatabase(module);
        return (new NamedTypeSpec("TestModule.MyEnum"), typeDatabase);
    }

    /// <summary>A spec/db pair for kinds that never consult the database (Bool/ClassPointer/etc.).</summary>
    private static (TypeSpec spec, ITypeDatabase db) EmptyDb()
        => (new NamedTypeSpec("Swift.Int"), new TypeDatabase());

    // ----- Bool ----------------------------------------------------------------------------

    [Fact]
    public void Bool_Inline_WrapsScalarWhenScalarParensTrue()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.Bool, "Int8");

        Assert.Equal(new[] { "return (foo) ? 1 : 0" },
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: true));
    }

    [Fact]
    public void Bool_Inline_SplicesBareWhenScalarParensFalse()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.Bool, "Int8");

        Assert.Equal(new[] { "return foo ? 1 : 0" },
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: false));
    }

    [Fact]
    public void Bool_BindingResult_BindsThenConverts()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.Bool, "Int8");

        Assert.Equal(new[] { "let result = foo", "return result ? 1 : 0" },
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Fact]
    public void Bool_Sentinel_IsZero()
        => Assert.Equal("    return 0", WriteSentinel(Map(CdeclReturnKind.Bool, "Int8")));

    // ----- SimpleEnum (raw value) ----------------------------------------------------------

    [Fact]
    public void SimpleEnum_RawValue_Inline_WrapsScalarWhenScalarParensTrue()
    {
        var (spec, db) = EnumDb("Swift.Int");
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        Assert.Equal(new[] { "return Int((foo).rawValue)" },
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: true));
    }

    [Fact]
    public void SimpleEnum_RawValue_Inline_SplicesBareWhenScalarParensFalse()
    {
        var (spec, db) = EnumDb("Swift.Int");
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        Assert.Equal(new[] { "return Int(foo.rawValue)" },
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: false));
    }

    [Fact]
    public void SimpleEnum_RawValue_BindingResult_BindsThenConverts()
    {
        var (spec, db) = EnumDb("Swift.Int");
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        Assert.Equal(new[] { "let result = foo", "return Int(result.rawValue)" },
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    // ----- SimpleEnum (tag-only, no raw value) ---------------------------------------------

    private static readonly string[] TagOnlyExpected =
    {
        "var result = foo",
        "let resultSize = MemoryLayout.size(ofValue: result)",
        "var tag: Int = 0",
        "withUnsafeMutablePointer(to: &tag) { tagPtr in withUnsafePointer(to: &result) { resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) } }",
        "return tag",
    };

    [Fact]
    public void SimpleEnum_TagOnly_Inline_EmitsWideningCopy()
    {
        var (spec, db) = EnumDb(rawValueType: null);
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        // Tag-only path ignores scalarParens — it splices the raw value expression, never (expr).
        Assert.Equal(TagOnlyExpected,
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: true));
        Assert.Equal(TagOnlyExpected,
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: false));
    }

    [Fact]
    public void SimpleEnum_TagOnly_BindingResult_EmitsWideningCopy()
    {
        var (spec, db) = EnumDb(rawValueType: null);
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        // The bind-result form passes callExpr straight to the widening copy (no separate `let result`).
        Assert.Equal(TagOnlyExpected,
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Fact]
    public void TagOnlyExpected_CrossChecksGetTagOnlyEnumReturnLines()
        // Pin the golden constant against the actual helper the renderer delegates to, so the two
        // cannot silently diverge (the renderer and the constant must agree on the tag-only shape).
        => Assert.Equal(TagOnlyExpected, WrapperEmitterHelpers.GetTagOnlyEnumReturnLines("foo", "Int"));

    [Fact]
    public void SimpleEnum_TagOnly_PassesCdeclReturnTypeSpellingThrough()
    {
        var (spec, db) = EnumDb(rawValueType: null);

        // A non-Int cdecl return type (e.g. Int8) must flow into `var tag: Int8 = 0` verbatim.
        Assert.Equal(
            WrapperEmitterHelpers.GetTagOnlyEnumReturnLines("foo", "Int8"),
            CdeclReturnRenderer.Lines(Expr, spec, db, Map(CdeclReturnKind.SimpleEnum, "Int8"), scalarParens: true));
    }

    [Fact]
    public void SimpleEnum_EmptyRawValueType_TreatedAsTagOnly()
    {
        // HasRawValue uses IsNullOrEmpty, so an empty RawValueTypeName falls to the tag-only copy
        // (not a `T(.rawValue)` conversion) — matching all six original switches.
        var (spec, db) = EnumDb(rawValueType: "");
        Assert.Equal(TagOnlyExpected,
            CdeclReturnRenderer.Lines(Expr, spec, db, Map(CdeclReturnKind.SimpleEnum, "Int"), scalarParens: true));
    }

    [Fact]
    public void SimpleEnum_Sentinel_IsZero()
        => Assert.Equal("    return 0", WriteSentinel(Map(CdeclReturnKind.SimpleEnum, "Int")));

    // ----- ClassPointer --------------------------------------------------------------------

    [Fact]
    public void ClassPointer_Inline_RetainsViaAnyObject_IgnoringScalarParens()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.ClassPointer, "UnsafeMutableRawPointer");
        var expected = new[] { "return Unmanaged.passRetained(foo as AnyObject).toOpaque()" };

        // ClassPointer never wraps the value as (expr) — same output regardless of scalarParens.
        Assert.Equal(expected, CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: true));
        Assert.Equal(expected, CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: false));
    }

    [Fact]
    public void ClassPointer_BindingResult_RetainsBoundResult()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.ClassPointer, "UnsafeMutableRawPointer");

        Assert.Equal(
            new[] { "let result = foo", "return Unmanaged.passRetained(result as AnyObject).toOpaque()" },
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClassPointer_RetainAppearsExactlyOncePerRender(bool scalarParens)
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.ClassPointer, "UnsafeMutableRawPointer");

        AssertRetainCountIsOne(CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens));
        AssertRetainCountIsOne(CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Fact]
    public void ClassPointer_Sentinel_IsNonNilBitPattern()
        => Assert.Equal("    return UnsafeMutableRawPointer(bitPattern: 1)!",
            WriteSentinel(Map(CdeclReturnKind.ClassPointer, "UnsafeMutableRawPointer")));

    // ----- OptionalClassPointer ------------------------------------------------------------

    [Fact]
    public void OptionalClassPointer_Inline_MapsRetainOverParenthesizedValue()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.OptionalClassPointer, "UnsafeMutableRawPointer?");
        var expected = new[]
        {
            "return (foo).map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }"
        };

        // Optional always parenthesizes the receiver for `.map`, regardless of scalarParens.
        Assert.Equal(expected, CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: true));
        Assert.Equal(expected, CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens: false));
    }

    [Fact]
    public void OptionalClassPointer_BindingResult_MapsBareResult()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.OptionalClassPointer, "UnsafeMutableRawPointer?");

        Assert.Equal(
            new[]
            {
                "let result = foo",
                "return result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }"
            },
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionalClassPointer_RetainAppearsExactlyOncePerRender(bool scalarParens)
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.OptionalClassPointer, "UnsafeMutableRawPointer?");

        AssertRetainCountIsOne(CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens));
        AssertRetainCountIsOne(CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Fact]
    public void OptionalClassPointer_Sentinel_IsNil()
        => Assert.Equal("    return nil",
            WriteSentinel(Map(CdeclReturnKind.OptionalClassPointer, "UnsafeMutableRawPointer?")));

    // ----- Direct --------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Direct_Inline_ReturnsValueVerbatim(bool scalarParens)
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.Direct, "Int");

        Assert.Equal(new[] { "return foo" },
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens));
    }

    [Fact]
    public void Direct_BindingResult_ReturnsCallExprWithoutBinding()
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(CdeclReturnKind.Direct, "Int");

        // Direct has no conversion, so the call expression is returned directly — no `let result`.
        Assert.Equal(new[] { "return foo" },
            CdeclReturnRenderer.LinesBindingResult(Expr, spec, db, mapping));
    }

    [Fact]
    public void Direct_Sentinel_IsZero()
        => Assert.Equal("    return 0", WriteSentinel(Map(CdeclReturnKind.Direct, "Int")));

    // ----- Write/Lines equivalence (the consolidation contract) ----------------------------

    // Kinds are passed as strings (CdeclReturnKind is internal — a public [Theory] method
    // cannot expose it as a parameter without CS0051). ParseKind maps back inside the test.
    public static IEnumerable<object[]> AllInlineCases()
    {
        yield return new object[] { "Bool", "Int8", true };
        yield return new object[] { "Bool", "Int8", false };
        yield return new object[] { "ClassPointer", "UnsafeMutableRawPointer", true };
        yield return new object[] { "ClassPointer", "UnsafeMutableRawPointer", false };
        yield return new object[] { "OptionalClassPointer", "UnsafeMutableRawPointer?", true };
        yield return new object[] { "OptionalClassPointer", "UnsafeMutableRawPointer?", false };
        yield return new object[] { "Direct", "Int", true };
        yield return new object[] { "Direct", "Int", false };
    }

    [Theory]
    [MemberData(nameof(AllInlineCases))]
    public void Write_EmitsExactlyWhatLinesReturns(string kindName, string cdeclType, bool scalarParens)
    {
        var (spec, db) = EmptyDb();
        var mapping = Map(ParseKind(kindName), cdeclType);

        Assert.Equal(
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens).ToArray(),
            WriteLines(Expr, spec, db, mapping, scalarParens));
    }

    private static CdeclReturnKind ParseKind(string kindName) => kindName switch
    {
        "Bool" => CdeclReturnKind.Bool,
        "SimpleEnum" => CdeclReturnKind.SimpleEnum,
        "ClassPointer" => CdeclReturnKind.ClassPointer,
        "OptionalClassPointer" => CdeclReturnKind.OptionalClassPointer,
        "Direct" => CdeclReturnKind.Direct,
        _ => throw new System.ArgumentOutOfRangeException(nameof(kindName), kindName, null),
    };

    [Theory]
    [InlineData("Swift.Int", true)]  // raw-value enum, method/subscript form
    [InlineData("Swift.Int", false)] // raw-value enum, property-getter form
    [InlineData(null, true)]         // tag-only enum, method/subscript form
    [InlineData(null, false)]        // tag-only enum, property-getter form
    public void Write_EmitsExactlyWhatLinesReturns_ForSimpleEnum(string? rawValueType, bool scalarParens)
    {
        var (spec, db) = EnumDb(rawValueType);
        var mapping = Map(CdeclReturnKind.SimpleEnum, "Int");

        Assert.Equal(
            CdeclReturnRenderer.Lines(Expr, spec, db, mapping, scalarParens).ToArray(),
            WriteLines(Expr, spec, db, mapping, scalarParens));
    }

    private static void AssertRetainCountIsOne(IEnumerable<string> lines)
    {
        var count = lines.Sum(l =>
            CountOccurrences(l, "Unmanaged.passRetained(") );
        Assert.Equal(1, count);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
