// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for complex type projections — verifies each projection produces
/// correct parameter plans, return plans, element conversions, and type metadata.
/// </summary>
public class ComplexProjectionTests
{
    #region ExistentialProjection

    [Fact]
    public void Existential_WellKnown_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "Swift.Foundation.AnyError", proxyClassName: null);
        Assert.Equal("Swift.Foundation.AnyError", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_ProxyWrapped_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IImageProcessing", "ImageProcessingProxy");
        Assert.Equal("IImageProcessing", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_Unknown_Types()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        Assert.Equal("object", proj.PublicType);
        Assert.Equal("Swift.Runtime.ExistentialContainer0", proj.PInvokeType);
    }

    [Fact]
    public void Existential_ParameterPlan_ExtractsContainer()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetParameterPlan("item");
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IDescribable>", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_Proxy_ConstructsProxy()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("new DescribableProxy(result, ownsContainer: true)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_WellKnown_ConstructsOwnedType()
    {
        // A non-optional `any Error` return is an owned +1 transfer: the AnyError wrapper adopts
        // the boxed error and releases it on Dispose/finalize, so the owned ctor arg is emitted.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "Swift.Foundation.AnyError", proxyClassName: null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("new Swift.Foundation.AnyError(result, ownsContainer: true)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ReturnPlan_Object_PassThrough()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ElementConversion_Proxy()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var paramConv = proj.GetParameterElementConversion("e");
        Assert.NotNull(paramConv);
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IDescribable>", paramConv);

        // Element conversion stays NON-owning: it is reused by GetReceiverExistentialSetterConversion
        // to wrap borrowed Swift->C# callback parameters (+0 guaranteed). Stamping it ownsContainer:true
        // would give a borrowed-parameter proxy a value-witness Destroy on finalize -> over-release/UAF.
        // Scalar owned returns balance their +1 via GetReturnPlan (asserted above), not here.
        var retConv = proj.GetReturnElementConversion("e");
        Assert.NotNull(retConv);
        Assert.Equal("(IDescribable)new DescribableProxy(e)", retConv);
    }

    [Fact]
    public void Existential_OwnedElementConversion_EC1_Adopts()
    {
        // Owned OPTIONAL existential returns (OptionalProjection's existential-inner branch) adopt the
        // Swift-returned +1: the inner container is read out of an sret/out buffer that is then raw-freed,
        // so the proxy is the only surviving retain and must release on Dispose/finalize. The owned
        // variant stamps ownsContainer:true; the shared GetReturnElementConversion stays non-owning so
        // borrowed receiver-callback wraps don't over-release.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        Assert.Equal("(IDescribable)new DescribableProxy(e, ownsContainer: true)", proj.GetOwnedReturnElementConversion("e"));
        Assert.Equal("(IDescribable)new DescribableProxy(e)", proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void Existential_OwnedElementConversion_EC2_Adopts()
    {
        // EC2+ composition proxies now expose the ownership-aware ctor (mirroring EC1): a composition
        // container holds one conforming value regardless of protocol count, so the proxy adopts the
        // Swift-returned +1 and releases that one value via the existential's own metadata on
        // Dispose/finalize. The owned variant stamps ownsContainer:true; the shared
        // GetReturnElementConversion stays non-owning so borrowed receiver-callback wraps don't over-release.
        var ec2 = new ExistentialProjection("Swift.Runtime.ExistentialContainer2", "IDescribable", "DescribableProxy");
        Assert.Equal("(IDescribable)new DescribableProxy(e, ownsContainer: true)", ec2.GetOwnedReturnElementConversion("e"));
        Assert.Equal("(IDescribable)new DescribableProxy(e)", ec2.GetReturnElementConversion("e"));
    }

    [Fact]
    public void Existential_OwnedElementConversion_BareAny_FallsBackNonOwning()
    {
        // Bare any (EC0 / object) has no proxy and no single owned +1 to adopt, so the owned variant
        // falls back to the non-owning form (OwnsContainerArg is empty for EC0).
        var bareAny = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: true);
        Assert.Equal(bareAny.GetReturnElementConversion("e"), bareAny.GetOwnedReturnElementConversion("e"));
    }

    [Fact]
    public void Existential_ElementConversion_Object_ReturnCastsToObject()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        var retConv = proj.GetReturnElementConversion("e");
        Assert.Equal("(object)e", retConv);
    }

    [Fact]
    public void Existential_DoesNotRequireSwiftWrapper()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Existential_ParameterPlan_GetOrCreate_UsesPublicTypeAsGenericArg()
    {
        // GetParameterPlan must use ExistentialContainerFactory.GetOrCreate<PublicType>(param, wrapFallback)
        // so proxy types (ISwiftExistentialConvertible), Swift value types (IExistentialBoxable), AND
        // plain C# classes implementing the interface (auto-wrapped via the generator-emitted proxy
        // factory) all flow through the same call site.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IBlockMode", "BlockModeProxy");
        var plan = proj.GetParameterPlan("mode");

        Assert.Equal(
            "ExistentialContainerFactory.GetOrCreate<IBlockMode>(mode, static __v => new BlockModeProxy(__v))",
            plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ParameterPlan_WellKnown_UsesCastFallback()
    {
        // Well-known types (AnyError) are value types — can't use GetOrCreate's class constraint.
        // They use direct ISwiftExistentialConvertible cast instead.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "Swift.Foundation.AnyError", proxyClassName: null);
        var plan = proj.GetParameterPlan("err");

        Assert.Contains("ISwiftExistentialConvertible", plan.PInvokeExpression);
        Assert.Contains("GetExistentialContainer()", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ParameterPlan_Object_UsesCastFallback()
    {
        // Unknown protocols (resolved to "object") have no proxy, use direct ISwiftExistentialConvertible cast.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null);
        var plan = proj.GetParameterPlan("value");

        Assert.Contains("ISwiftExistentialConvertible", plan.PInvokeExpression);
        Assert.Contains("GetExistentialContainer()", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_ParameterElementConversion_UsesGetOrCreate()
    {
        // Element conversion for collection parameters also routes through GetOrCreate with
        // the same auto-wrap fallback used by scalar existential parameters, so array/set/dict
        // elements accept plain C# implementations of the interface.
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IRenderable", "RenderableProxy");
        var conv = proj.GetParameterElementConversion("item");

        Assert.NotNull(conv);
        Assert.Equal(
            "ExistentialContainerFactory.GetOrCreate<IRenderable>(item, static __v => new RenderableProxy(__v))",
            conv);
    }

    [Fact]
    public void Existential_BareAny_ParameterPlan_UsesBox()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: true);
        var plan = proj.GetParameterPlan("value");

        Assert.Equal("ExistentialContainer0.Box(value)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_BareAny_ReturnPlan_UsesUnbox()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: true);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("ExistentialContainer0.Unbox(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_BareAny_ParameterElementConversion_UsesBox()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: true);
        var conv = proj.GetParameterElementConversion("item");

        Assert.NotNull(conv);
        Assert.Equal("ExistentialContainer0.Box(item)", conv);
    }

    [Fact]
    public void Existential_BareAny_ReturnElementConversion_UsesUnbox()
    {
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: true);
        var conv = proj.GetReturnElementConversion("item");

        Assert.NotNull(conv);
        Assert.Equal("ExistentialContainer0.Unbox(item)", conv);
    }

    [Fact]
    public void Existential_NonBareAny_Object_StillUsesLegacyPath()
    {
        // Non-bare-Any object projection (unknown protocol) should NOT use Box/Unbox
        var proj = new ExistentialProjection("Swift.Runtime.ExistentialContainer0", "object", proxyClassName: null, isBareAny: false);
        var plan = proj.GetParameterPlan("value");

        Assert.Contains("ISwiftExistentialConvertible", plan.PInvokeExpression);
        Assert.DoesNotContain("Box", plan.PInvokeExpression);
    }

    #endregion

    #region Nested existential collection owned-return

    // An owned existential return (read out of an sret/out buffer that is then raw-freed) must adopt
    // Swift's moved +1 so the proxy releases on Dispose — `ownsContainer: true`. Before the fix, the
    // owned path on Array/Set/Dictionary recursed into the element only when the element was DIRECTLY an
    // ExistentialProjection; a NESTED container element ([[any P]], [Set<any P>], …) fell through to the
    // shared non-owning GetReturnElementConversion, so the inner existential leaked its moved +1. The fix
    // adds GetOwnedReturnElementConversion to every container so the owned selector recurses all the way
    // down. These tests pin: (a) the nested owned path stamps ownsContainer:true on the leaf, and (b) the
    // shared non-owning path stays borrowed (no over-release of borrowed receiver reads).

    private static ExistentialProjection DescribableExistential()
        => new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");

    [Fact]
    public void NestedOwnedReturn_ArrayOfExistential_StampsOwnsContainer()
    {
        // [any P] : the single-level owned element conversion wraps each proxy as owned.
        var proj = new ArrayProjection(DescribableExistential(), isParameter: false);
        var owned = proj.GetOwnedReturnElementConversion("e");

        Assert.Contains("AsProjected", owned);
        Assert.Contains("new DescribableProxy(e, ownsContainer: true)", owned);
    }

    [Fact]
    public void NestedOwnedReturn_ArrayOfArrayOfExistential_InnerLeafOwns()
    {
        // [[any P]] : the OUTER owned element conversion must recurse through the inner array's OWNED
        // conversion so the existential leaf two levels down still adopts its +1. This is the exact
        // regression: pre-fix the inner array fell to the non-owning path and dropped ownsContainer.
        var inner = new ArrayProjection(DescribableExistential(), isParameter: false);
        var outer = new ArrayProjection(inner, isParameter: false);
        var owned = outer.GetOwnedReturnElementConversion("e");

        Assert.NotNull(owned);
        Assert.Contains("new DescribableProxy(e, ownsContainer: true)", owned);
        // Doubly nested: an AsProjected inside an AsProjected.
        Assert.Equal(2, CountOccurrences(owned, "AsProjected"));
    }

    [Fact]
    public void NestedOwnedReturn_ArrayOfArrayOfExistential_SharedPathStaysBorrowed()
    {
        // The shared non-owning GetReturnElementConversion (used for borrowed receiver-callback wraps,
        // +0 guaranteed) must NOT adopt — otherwise a borrowed nested read over-releases.
        var inner = new ArrayProjection(DescribableExistential(), isParameter: false);
        var outer = new ArrayProjection(inner, isParameter: false);
        var borrowed = outer.GetReturnElementConversion("e");

        Assert.DoesNotContain("ownsContainer: true", borrowed);
        Assert.Contains("new DescribableProxy(e)", borrowed);
    }

    [Fact]
    public void NestedOwnedReturn_SetOfExistential_StampsOwnsContainer()
    {
        // Set<any P> : owned element conversion materializes via ToHashSet with the owned leaf wrap.
        var proj = new SetProjection(DescribableExistential(), isParameter: false);
        var owned = proj.GetOwnedReturnElementConversion("e");

        Assert.Contains("ToHashSet()", owned);
        Assert.Contains("new DescribableProxy(e, ownsContainer: true)", owned);
    }

    [Fact]
    public void NestedOwnedReturn_ArrayOfSetOfExistential_InnerLeafOwns()
    {
        // [Set<any P>] : cross-container recursion — the array's owned path threads the set's owned path,
        // which threads the existential's owned leaf.
        var innerSet = new SetProjection(DescribableExistential(), isParameter: false);
        var outer = new ArrayProjection(innerSet, isParameter: false);
        var owned = outer.GetOwnedReturnElementConversion("e");

        Assert.Contains("ToHashSet()", owned);
        Assert.Contains("new DescribableProxy(e, ownsContainer: true)", owned);
    }

    [Fact]
    public void NestedOwnedReturn_DictionaryValueExistential_StampsOwnsContainer()
    {
        // [K: any P] : the value projection's owned leaf wrap must thread through ToDictionary.
        var dict = new DictionaryProjection(new BlittableProjection("Int64"), DescribableExistential(), isParameter: false);
        var owned = dict.GetOwnedReturnElementConversion("e");

        Assert.Contains(".ToDictionary(", owned);
        Assert.Contains("new DescribableProxy(kvp.Value, ownsContainer: true)", owned);
    }

    [Fact]
    public void NestedReturn_DictionaryWithExistentialValue_ElementConversion_IsUniversalDonorConcreteDictionary()
    {
        // [[String: any P]] reverse-dispatch regression. The dictionary element conversion MUST
        // stay a CONCRETE Dictionary<,> (no leading IReadOnlyDictionary cast): the concrete type
        // is the universal donor, assignable to BOTH IReadOnlyDictionary (covariant /
        // forward-return consumers) AND IDictionary (a receiver param whose impl takes
        // IEnumerable<IDictionary>). A leading (IReadOnlyDictionary) cast breaks the receiver
        // path with CS1503. Per-leaf public-type casts remain (existential value → IDescribable).
        var innerDict = new DictionaryProjection(new StringProjection(), DescribableExistential(), isParameter: false);
        var conv = innerDict.GetReturnElementConversion("e");

        Assert.NotNull(conv);
        Assert.StartsWith("e.ToDictionary(", conv);
        // No leading interface cast on the dictionary itself — it must remain the concrete donor.
        Assert.False(conv!.StartsWith("(", System.StringComparison.Ordinal));
        // The existential value is still cast to its public interface inside the selector.
        Assert.Contains("(IDescribable)", conv);
    }

    [Fact]
    public void NestedOwnedReturn_DictionaryWithExistentialValue_OwnedElementConversion_IsUniversalDonorConcreteDictionary()
    {
        // The owned-return element path (used when this dictionary is itself an element of an OWNED
        // outer container) stays the same concrete universal donor as the borrowed path above — no
        // leading IReadOnlyDictionary cast — while still adopting the existential leaf's moved +1.
        var innerDict = new DictionaryProjection(new StringProjection(), DescribableExistential(), isParameter: false);
        var conv = innerDict.GetOwnedReturnElementConversion("e");

        Assert.NotNull(conv);
        Assert.StartsWith("e.ToDictionary(", conv);
        Assert.False(conv!.StartsWith("(", System.StringComparison.Ordinal));
        Assert.Contains("new DescribableProxy(kvp.Value, ownsContainer: true)", conv);
    }

    [Fact]
    public void TopLevelReturn_DictionaryOfDictionary_AsProjectedValueSelector_CastsToReadOnlyInterface()
    {
        // [String: [String: any P]] return: the invariant-slot cast that used to live on the
        // inner element conversion is now applied by the OUTER dictionary's AsProjected value
        // selector. The outer value slot (IReadOnlyDictionary<string, IReadOnlyDictionary<...>>)
        // is INVARIANT, so the concrete inner Dictionary produced by ToDictionary must be cast
        // to its IReadOnlyDictionary PublicType in the selector or AsProjected infers the wrong
        // TResult (CS0266).
        var outerDict = new DictionaryProjection(
            new StringProjection(),
            new DictionaryProjection(new StringProjection(), DescribableExistential(), isParameter: false),
            isParameter: false);
        var plan = outerDict.GetReturnPlan("__res", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(", plan.PInvokeExpression);
        // The value selector wraps the inner concrete dictionary in its read-only interface type.
        Assert.Contains("(IReadOnlyDictionary<string, IDescribable>)", plan.PInvokeExpression);
    }

    [Fact]
    public void TopLevelReturn_DictionaryOfArrayOfDictionary_AsProjectedValueSelector_CastsToReadOnlyListInterface()
    {
        // [String: [[String: any P]]] return regression. The outer dictionary value is an ARRAY
        // of dictionaries. The array element conversion yields IReadOnlyList<concrete Dictionary>
        // (covariance absorbs the concrete inner dict for the array itself), but the OUTER
        // dictionary value slot is INVARIANT, so the array value must be cast to its EXACT
        // IReadOnlyList<IReadOnlyDictionary<…>> public type in the selector or AsProjected
        // infers IReadOnlyList<Dictionary<…>> and the invariant outer dictionary rejects it
        // with CS0266. The cast is legal via the covariant IReadOnlyList<out T>.
        var outerDict = new DictionaryProjection(
            new StringProjection(),
            new ArrayProjection(
                new DictionaryProjection(new StringProjection(), DescribableExistential(), isParameter: false),
                isParameter: false),
            isParameter: false);
        var plan = outerDict.GetReturnPlan("__res", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(", plan.PInvokeExpression);
        // The value selector wraps the inner array (of concrete dictionaries) in its declared read-only
        // list-of-read-only-dictionary public type.
        Assert.Contains("(IReadOnlyList<IReadOnlyDictionary<string, IDescribable>>)", plan.PInvokeExpression);
    }

    [Fact]
    public void NestedReturn_ArrayOfDictionary_ElementConversion_NoCastNeeded_Covariant()
    {
        // Asymmetry contrast: an Array element conversion does NOT prefix a PublicType cast — the
        // outer IReadOnlyList<out T> is covariant, so a concrete inner Dictionary is absorbed. This
        // pins the reason the Dictionary path needs the cast and the Array path must not grow one.
        var arr = new ArrayProjection(
            new DictionaryProjection(new StringProjection(), DescribableExistential(), isParameter: false),
            isParameter: false);
        var conv = arr.GetReturnElementConversion("e");

        Assert.NotNull(conv);
        // The array element conversion is the bare covariant AsProjected form — no leading
        // (IReadOnlyList<...>) cast — unlike the invariant Dictionary path above.
        Assert.StartsWith("e.AsProjected(", conv);
        Assert.False(conv!.StartsWith("(", System.StringComparison.Ordinal));
    }

    [Fact]
    public void NestedOwnedReturn_TopLevelReturnPlan_ArrayOfArrayOfExistential_OwnsInnerLeaf()
    {
        // The real emission site: a method returning [[any P]] builds its return plan from the OWNED
        // element conversion, so the inner existential leaf is adopted (not leaked).
        var inner = new ArrayProjection(DescribableExistential(), isParameter: false);
        var outer = new ArrayProjection(inner, isParameter: false);
        var plan = outer.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("new DescribableProxy(e, ownsContainer: true)", plan.PInvokeExpression);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    #endregion

    #region ArrayProjection

    [Fact]
    public void Array_BlittableElement_ReturnType()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        Assert.Equal("IReadOnlyList<Int64>", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Array_BlittableElement_ParamType()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: true);
        Assert.Equal("IEnumerable<Int64>", proj.PublicType);
    }

    [Fact]
    public void Array_StringElement_Types()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        Assert.Equal("IReadOnlyList<string>", proj.PublicType);
    }

    [Fact]
    public void Array_ParamPlan_NoConversion_HasUsing()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("items");

        Assert.Equal("itemsBuffer", plan.PInvokeExpression);
        // Should have Using for SwiftArray and PayloadBuffer
        Assert.True(plan.SetupStatements.Count >= 2);
    }

    [Fact]
    public void Array_ParamPlan_WithConversion_HasSelectAndDisposal()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("names");

        Assert.Equal("namesBuffer", plan.PInvokeExpression);
        // Should have Select conversion line
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("new SwiftString", firstLine.Code);

        // Should have try/finally for disposal since StringProjection.ElementRequiresDisposal=true
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void Array_ParamPlan_EnumElement_DirectFromEnumerable()
    {
        // Enums are blittable — no element conversion needed. Direct FromEnumerable.
        var elem = new SimpleEnumProjection("Direction", "int");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("dirs");

        Assert.Equal("dirsBuffer", plan.PInvokeExpression);
        // No Select — enums pass directly to FromEnumerable
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FromEnumerable(dirs)", firstLine.Code);
        Assert.DoesNotContain(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void Array_ReturnPlan_Direct_RequiresUnsafe()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        // Direct returns consume the source register slot (copy + VWT-Destroy) to balance the
        // wire carrier's element-ref +1; the address is passed by `&result`, not `new IntPtr(&result)`.
        Assert.Contains("MarshalFromSwiftObjectConsuming", plan.PInvokeExpression);
        Assert.Contains("&result", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(e => e)", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_IndirectResult_NoUnsafe()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.DoesNotContain("new IntPtr(&", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_StringElement_HasConversionLambda()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(e =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_AsyncCallback_IsPassThrough()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.AsyncCallback);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_DoesNotRequireSwiftWrapper()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), false);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Array_ExposesElementProjection()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, false);
        Assert.Same(elem, proj.ElementProjection);
    }

    [Fact]
    public void Array_ObjCBridgeable_ParamPlan_UsesFromNSObjects()
    {
        var elem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("urls");

        Assert.Equal("urlsBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("NSArray.FromNSObjects(urls.ToArray())", firstLine.Code);
    }

    [Fact]
    public void Array_NestedObjCBridgeable_ParamPlan_RecursivelyConverts()
    {
        // [[URL]]: outer array's elements are [URL] (IEnumerable<NSUrl>), not NSObject.
        // The parameter plan must recursively convert inner arrays to NSArray.
        var innerElem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var innerArray = new ArrayProjection(innerElem, isParameter: true);
        var outerArray = new ArrayProjection(innerArray, isParameter: true);
        var plan = outerArray.GetParameterPlan("nested");

        Assert.Equal("nestedBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        // Must apply inner conversion: Select + FromNSObjects for inner arrays
        Assert.Contains(".Select(e =>", firstLine.Code);
        Assert.Contains("NSArray.FromNSObjects", firstLine.Code);
    }

    [Fact]
    public void Set_NestedObjCBridgeable_ParamPlan_RecursivelyConverts()
    {
        // Set<[URL]>: inner elements are IEnumerable<NSUrl>, not NSObject.
        var innerElem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var innerArray = new ArrayProjection(innerElem, isParameter: true);
        var outerSet = new SetProjection(innerArray, isParameter: true);
        var plan = outerSet.GetParameterPlan("items");

        Assert.Equal("itemsBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(e =>", firstLine.Code);
        Assert.Contains("NSArray.FromNSObjects", firstLine.Code);
    }

    [Fact]
    public void Dictionary_NestedObjCBridgeable_ToNSObject_RecursivelyConverts()
    {
        // [String: [URL]]: inner value is IReadOnlyList<NSUrl>, not NSObject.
        var innerElem = new ObjCBridgeableProjection("Foundation.NSUrl");
        var innerArray = new ArrayProjection(innerElem, isParameter: true);
        var result = DictionaryProjection.ToNSObject(innerArray, "val");

        // Must apply inner conversion to produce NSArray
        Assert.Contains("NSArray.FromNSObjects", result);
        Assert.Contains("(Foundation.NSObject)", result);
    }

    #endregion

    #region DictionaryProjection

    [Fact]
    public void Dictionary_StringString_Types()
    {
        var key = new StringProjection();
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        Assert.Equal("IReadOnlyDictionary<string, string>", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Dictionary_ParamType()
    {
        var key = new StringProjection();
        var val = new BlittableProjection("Int64");
        var proj = new DictionaryProjection(key, val, isParameter: true);
        Assert.Equal("IDictionary<string, Int64>", proj.PublicType);
    }

    [Fact]
    public void Dictionary_BlittableBlittable_ParamPlan_NoConversion()
    {
        var key = new BlittableProjection("Int64");
        var val = new BlittableProjection("double");
        var proj = new DictionaryProjection(key, val, isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);
        // No Select conversion needed — first stmt is container creation, then Using + PayloadBuffer
        var firstSetup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FromDictionary", firstSetup.Code);
    }

    [Fact]
    public void Dictionary_StringString_ParamPlan_HasConversionAndDisposal()
    {
        var key = new StringProjection();
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("new SwiftString", firstLine.Code);
    }

    [Fact]
    public void Dictionary_ReturnPlan_Direct_RequiresUnsafe()
    {
        var key = new BlittableProjection("Int64");
        var val = new BlittableProjection("double");
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwift", plan.PInvokeExpression);
        Assert.Contains("SwiftDictionary", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ReturnPlan_StringValue_HasConversionLambda()
    {
        var key = new BlittableProjection("Int64");
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Blittable key → no key conversion, string value → 1-arg overload
        Assert.Contains(".AsProjected(v =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ReturnPlan_StringKeyEnumValue()
    {
        var key = new StringProjection();
        var val = new SimpleEnumProjection("Direction", "int");
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // String key has conversion (ToString), enum value has no conversion (blittable passthrough)
        Assert.Contains(".AsProjected(k =>", plan.PInvokeExpression);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Dictionary_ExposesKeyAndValueProjections()
    {
        var key = new StringProjection();
        var val = new BlittableProjection("Int64");
        var proj = new DictionaryProjection(key, val, false);
        Assert.Same(key, proj.KeyProjection);
        Assert.Same(val, proj.ValueProjection);
    }

    #endregion

    #region OptionalProjection

    [Fact]
    public void Optional_Blittable_Types()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        Assert.Equal("Int64?", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Optional_String_Types()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        Assert.Equal("string?", proj.PublicType);
    }

    [Fact]
    public void Optional_ParamPlan_SimpleInner_InlineTernary()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetParameterPlan("val");

        Assert.Equal("valBuffer", plan.PInvokeExpression);
        // Simple inner (no element conversion) → Using with ternary
        var firstSetup = plan.SetupStatements[0];
        var usingStmt = Assert.IsType<MarshalStatement.Using>(firstSetup);
        Assert.Contains("SwiftOptional", usingStmt.Type);
        Assert.Contains("NewSome", usingStmt.InitExpression);
        Assert.Contains("NewNone", usingStmt.InitExpression);
    }

    [Fact]
    public void Optional_ParamPlan_ComplexInner_HasBranching()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var plan = proj.GetParameterPlan("name");

        Assert.Equal("nameBuffer", plan.PInvokeExpression);
        // Complex inner (has element conversion) → Block if/else
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header.Contains("if ("));
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "else");
    }

    [Fact]
    public void Optional_ReturnPlan_Direct_RequiresUnsafe()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        // Uses HasValue/Some pattern (setup line has MarshalFromSwift, expression has HasValue)
        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("MarshalFromSwift", setupLine.Code);
        Assert.Contains("SwiftOptional", setupLine.Code);
        Assert.Contains("_swiftOpt.HasValue", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_IndirectResult_NoUnsafe()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("_swiftOpt.HasValue", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_StringInner_HasTwoStepConversion()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Two-step: HasValue check first, then conditional conversion
        Assert.NotEmpty(plan.SetupStatements);
        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("_swiftOpt", setupLine.Code);
        Assert.DoesNotContain("ToNullable", setupLine.Code);
        Assert.Contains("ToString()", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_ExistentialInner_DiscriminantCheck()
    {
        var inner = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var proj = new OptionalProjection(inner, isExistentialInner: true);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Behavior: indirect-result existential bypass — None is detected via the
        // metadata-pointer null test (canonical encoding); Some constructs the proxy.
        // The bypass exists because SwiftOptional<T>.Case → VWT GetEnumTag is broken
        // on Mono iOS Simulator for existential optionals.
        Assert.Contains("== IntPtr.Zero", plan.PInvokeExpression);
        Assert.Contains("new DescribableProxy", plan.PInvokeExpression);
        Assert.DoesNotContain("GetEnumTag", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ExposesInnerProjection()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        Assert.Same(inner, proj.InnerProjection);
    }

    [Fact]
    public void Optional_AnyError_DirectReturnPlan_BoxedPointer()
    {
        // DataLoader.Validate pattern — `(any Error)?` is the
        // ONE existential optional that's NOT 5-word-container-via-sret. `any Error` is
        // class-bound (boxed reference, MemoryLayout = 8). Swift returns `Optional<(any
        // Error)>` directly in x0 with nil = IntPtr.Zero. The wrapper must construct
        // AnyError over Payload0 (the boxed pointer) — sbw_anyErrorGetDescription
        // loads `(any Error).self` (8 bytes) from the container and never reads the
        // remaining EC1 slots, so they may stay zero.
        //
        // ExistentialProjection with PublicType "Swift.Foundation.AnyError" is the
        // signal that fires the AnyError-specific direct branch; OptionalProjection
        // emits the boxed-pointer null test instead of the buffer-relative
        // metadata-pointer test used by every other `(any P)?`.
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1",
            "Swift.Foundation.AnyError",
            proxyClassName: null);
        var proj = new OptionalProjection(inner, isExistentialInner: true);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // Boxed-pointer null check on the IntPtr return — NOT a buffer offset read.
        Assert.Contains("result == IntPtr.Zero", plan.PInvokeExpression);
        // AnyError construction wraps the boxed pointer into ExistentialContainer1.Payload0.
        Assert.Contains("new Swift.Foundation.AnyError(", plan.PInvokeExpression);
        Assert.Contains("Payload0 = result", plan.PInvokeExpression);
        // No sret-style buffer arithmetic, no SwiftOptional discriminant.
        Assert.DoesNotContain("byte*", plan.PInvokeExpression);
        Assert.DoesNotContain("SwiftOptionalCases", plan.PInvokeExpression);
        Assert.DoesNotContain("GetEnumTag", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ParamPlan_ArrayInner_UsesSwiftArrayNotIntPtr()
    {
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: true);
        var proj = new OptionalProjection(arrayProj);
        var plan = proj.GetParameterPlan("items");

        Assert.Equal("itemsBuffer", plan.PInvokeExpression);
        // SwiftOptional should use SwiftArray<Int64>, not IntPtr
        var allCode = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
        var allUsings = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type));
        var combined = allCode + "\n" + allUsings;
        Assert.Contains("SwiftOptional<SwiftArray<Int64>>", combined);
        Assert.DoesNotContain("SwiftOptional<IntPtr>", combined);
    }

    [Fact]
    public void Optional_ParamPlan_DictionaryInner_UsesSwiftDictionaryNotIntPtr()
    {
        var dictProj = new DictionaryProjection(
            new StringProjection(), new BlittableProjection("Int64"), isParameter: true);
        var proj = new OptionalProjection(dictProj);
        var plan = proj.GetParameterPlan("data");

        Assert.Equal("dataBuffer", plan.PInvokeExpression);
        var allCode = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
        var allUsings = string.Join("\n", plan.SetupStatements.OfType<MarshalStatement.Using>().Select(u => u.Type));
        var combined = allCode + "\n" + allUsings;
        Assert.Contains("SwiftOptional<SwiftDictionary<SwiftString, Int64>>", combined);
        Assert.DoesNotContain("SwiftOptional<IntPtr>", combined);
    }

    [Fact]
    public void Optional_ReturnPlan_ArrayInner_UsesContainerConversion()
    {
        var arrayProj = new ArrayProjection(new StringProjection(), isParameter: false);
        var proj = new OptionalProjection(arrayProj);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Should use discriminant check + AsProjected, not ToNullable
        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
        Assert.DoesNotContain("ToNullable", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ReturnPlan_DictionaryInner_UsesContainerConversion()
    {
        var dictProj = new DictionaryProjection(
            new BlittableProjection("Int64"), new StringProjection(), isParameter: false);
        var proj = new OptionalProjection(dictProj);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_ElementConversion_NonFrozenStructInner_BuildsSwiftOptionalWrapper()
    {
        // Array<Optional<NonFrozenStruct>> — per-element conversion builds tagged
        // SwiftOptional<TStruct> over the typed wrapper, NOT SwiftOptional<IntPtr>.
        // The tagged-optional slot's `Some` payload holds an ISwiftObject, which is
        // copied into Swift's Array<Optional<TStruct>> storage by value via VWT.
        // Lowering Some to a raw IntPtr would silently downgrade the slot to a
        // 1-word handle instead of the typed struct wrapper.
        var inner = new NonFrozenStructProjection("Tips.Rule");
        var proj = new OptionalProjection(inner);
        var conv = proj.GetParameterElementConversion("e");

        Assert.NotNull(conv);
        Assert.Contains("SwiftOptional<Tips.Rule>.NewSome", conv);
        Assert.Contains("SwiftOptional<Tips.Rule>.NewNone", conv);
        // Some-arg passes the typed wrapper directly — no DangerousGetHandle in
        // the container-element path.
        Assert.DoesNotContain("Payload.DangerousGetHandle()", conv);
    }

    [Fact]
    public void Optional_ElementConversion_ClassInner_UsesNilPointerOptimization()
    {
        // Array<Optional<Class>> — Swift classes are nil-pointer-optimized (8-byte bare pointer, 0 = nil).
        // Element conversion returns a bare IntPtr ternary, NOT a SwiftOptional<IntPtr> wrapper.
        var inner = new ClassProjection("MyLoader");
        var proj = new OptionalProjection(inner);
        var conv = proj.GetParameterElementConversion("e");

        Assert.NotNull(conv);
        Assert.Contains("IntPtr.Zero", conv);
        Assert.Contains("Payload.DangerousGetHandle()", conv);
        Assert.DoesNotContain("SwiftOptional<", conv);
    }

    [Fact]
    public void Optional_SwiftContainerGenericType_ClassInner_IsBareIntPtr()
    {
        // For Swift class refs inside Optional, the container element type is bare IntPtr
        // (nil-pointer-optimized). SwiftOptional<IntPtr> would be 9 bytes — wrong ABI.
        var proj = new OptionalProjection(new ClassProjection("MyLoader"));
        Assert.Equal("IntPtr", proj.SwiftContainerGenericType);
    }

    [Fact]
    public void Optional_SwiftContainerGenericType_NonFrozenStructInner_IsTaggedSwiftOptional()
    {
        // For non-frozen structs inside Optional, the container element type is the
        // tagged SwiftOptional<TStruct> over the typed wrapper — NOT SwiftOptional<IntPtr>.
        // SwiftArray<SwiftOptional<TStruct>>'s per-slot storage routes through
        // ISwiftObject.MarshalToSwift / VWT.InitializeWithCopy, which expects the typed
        // wrapper so the struct's payload bytes are copied by value into the contiguous
        // Array<Optional<TStruct>> slot the @_cdecl wrapper maps with
        // assumingMemoryBound(to: Array<Optional<TStruct>>.self).
        var proj = new OptionalProjection(new NonFrozenStructProjection("Tips.Rule"));
        Assert.Equal("SwiftOptional<Tips.Rule>", proj.SwiftContainerGenericType);
    }

    [Fact]
    public void Optional_ElementConversion_DerivesPatternVarFromInput_NoCollision()
    {
        // Two optional element conversions in the same expression (e.g., dict key + value) must
        // not both declare the same pattern variable — that would trigger CS0128 at compile time.
        var proj = new OptionalProjection(new NonFrozenStructProjection("Tips.Rule"));
        var keyConv = proj.GetParameterElementConversion("kv.Key");
        var valConv = proj.GetParameterElementConversion("kv.Value");

        Assert.NotNull(keyConv);
        Assert.NotNull(valConv);
        Assert.Contains("__v_kv_Key", keyConv);
        Assert.Contains("__v_kv_Value", valConv);
        // Pattern variables from the two conversions must be distinct.
        Assert.DoesNotContain("__v_kv_Key", valConv);
        Assert.DoesNotContain("__v_kv_Value", keyConv);
    }

    [Fact]
    public void Optional_ElementRequiresDisposal_TaggedPath_IsTrue()
    {
        // Tagged optional path allocates SwiftOptional<T> wrappers per element — container
        // parameter plans must dispose them in the finally block to avoid leaking native buffers.
        var proj = new OptionalProjection(new NonFrozenStructProjection("Tips.Rule"));
        Assert.True(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void Optional_ElementRequiresDisposal_NilPointerPath_IsFalse()
    {
        // Nil-pointer-optimized paths (classes, ObjC types) emit bare IntPtr element conversions
        // with no allocation, so disposal is not required.
        Assert.False(new OptionalProjection(new ClassProjection("MyLoader")).ElementRequiresDisposal);
    }

    #endregion

    #region TupleProjection

    [Fact]
    public void Tuple_AllBlittable_Types()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });

        Assert.Equal("(Int64, double)", proj.PublicType);
        Assert.Equal("ValueTuple<Int64, double>", proj.PInvokeType);
    }

    [Fact]
    public void Tuple_MixedTypes()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });

        Assert.Equal("(string, Int64)", proj.PublicType);
        Assert.Equal("ValueTuple<SwiftString, Int64>", proj.PInvokeType);
    }

    [Fact]
    public void Tuple_AllBlittable_ParamPlan_IsPassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });
        var plan = proj.GetParameterPlan("t");
        Assert.Equal("t", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Tuple_MixedTypes_ParamPlan_HasConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetParameterPlan("t");

        // Should have setup line for string conversion
        Assert.NotEmpty(plan.SetupStatements);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("new SwiftString", firstLine.Code);
    }

    [Fact]
    public void Tuple_AllBlittable_ReturnPlan_IsPassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("double")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Tuple_MixedTypes_ReturnPlan_HasPerElementConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // Should have setup for string element conversion
        Assert.NotEmpty(plan.SetupStatements);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("elem0", firstLine.Code);
        Assert.Contains("ToString()", firstLine.Code);

        // Final expression should use converted elem0 and raw Item2
        Assert.Contains("elem0", plan.PInvokeExpression);
        Assert.Contains("result.Item2", plan.PInvokeExpression);
    }

    [Fact]
    public void Tuple_ExposesElementProjections()
    {
        var elems = new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new StringProjection()
        };
        var proj = new TupleProjection(elems);
        Assert.Equal(2, proj.ElementProjections.Count);
        Assert.Same(elems[0], proj.ElementProjections[0]);
        Assert.Same(elems[1], proj.ElementProjections[1]);
    }

    [Fact]
    public void Tuple_PInvokeAttribute_IsNull()
    {
        var proj = new TupleProjection(new ITypeProjection[] { new BlittableProjection("Int64") });
        Assert.Null(proj.PInvokeAttribute);
    }

    #endregion

    #region ClosureProjection

    [Fact]
    public void Closure_Action_Types()
    {
        var proj = new ClosureProjection(
            Array.Empty<ITypeProjection>(),
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("global::System.Action", proj.PublicType);
        Assert.Equal("SwiftClosureData", proj.PInvokeType);
    }

    [Fact]
    public void Closure_ActionWithArgs_Types()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection(), new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("global::System.Action<string, Int64>", proj.PublicType);
    }

    [Fact]
    public void Closure_Func_Types()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection() },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Equal("global::System.Func<string, bool>", proj.PublicType);
    }

    [Fact]
    public void Closure_NonEscaping_PInvokeType_IsFuncPtr()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: false,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Contains("delegate* unmanaged[Swift]", proj.PInvokeType);
    }

    [Fact]
    public void Closure_Escaping_ParamPlan_HasGCHandle()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var plan = proj.GetParameterPlan("handler");

        Assert.Contains("GCHandle.Alloc", plan.SetupStatements.OfType<MarshalStatement.Line>().First().Code);
        Assert.Contains("SwiftClosureData", plan.SetupStatements.OfType<MarshalStatement.Line>().Last().Code);
        Assert.Equal("handlerClosure", plan.PInvokeExpression);

        // Escaping closures: GCHandle intentionally leaked — Swift may store the closure
        // beyond the P/Invoke return. The callback also does NOT free (may fire multiple times).
        Assert.Empty(plan.CleanupStatements);
    }

    [Fact]
    public void Closure_ReturnPlan_HasLambdaBody()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        // Should check for null function pointer
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FunctionPointer == IntPtr.Zero", firstLine.Code);
    }

    [Fact]
    public void Closure_Escaping_HasCallbackDeclarations()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        var callbacks = proj.CallbackDeclarations;
        Assert.Single(callbacks);
        Assert.Equal("testCallback", callbacks[0].MethodName);
        Assert.Equal("CallConvCdecl", callbacks[0].CallingConvention);
        Assert.NotNull(callbacks[0].StaticFieldDeclaration);
        Assert.Contains("s_testCallback", callbacks[0].StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_NonEscaping_NoCallbackDeclarations()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: false,
            throws: false,
            isAsync: false,
            callbackName: "testCallback");

        Assert.Empty(proj.CallbackDeclarations);
    }

    [Fact]
    public void Closure_Callback_HasDelegateExtraction()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new StringProjection() },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "onComplete");

        var callbacks = proj.CallbackDeclarations;
        var cb = callbacks[0];
        // Top-level body has: Line (GetDelegate), Line (invoke statements)
        var bodyCode = string.Join("\n", cb.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("GetDelegateFromContext", bodyCode);
        // Args should be reverse-converted (P/Invoke → delegate types)
        Assert.Contains("ToString()", bodyCode);
    }

    [Fact]
    public void Closure_ReturnPlan_WithConvertedArg_IncludesConversionInLambda()
    {
        // NonFrozenStruct arg has element conversion — the lambda body should include it
        var argProj = new NonFrozenStructProjection("Pipeline");
        var proj = new ClosureProjection(
            new ITypeProjection[] { argProj },
            returnProjection: new BoolProjection(),
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "pipelineCb");

        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // The lambda body should contain the conversion variable
        var lambdaLine = plan.SetupStatements.OfType<MarshalStatement.Line>()
            .FirstOrDefault(l => l.Code.Contains("closureResult"));
        Assert.NotNull(lambdaLine);
        Assert.Contains("arg0Converted", lambdaLine!.Code);
        Assert.Contains("DangerousGetHandle", lambdaLine.Code);
    }

    [Fact]
    public void Closure_VoidCallback_StaticField_HasVoidReturn()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: null,
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "voidCb");

        var callback = proj.CallbackDeclarations[0];
        Assert.NotNull(callback.StaticFieldDeclaration);
        // delegate* should end with ", void>" for void callback — last type arg is return type
        Assert.Contains(", void>", callback.StaticFieldDeclaration!);
        // Static field should be initialized with method address
        Assert.Contains("= &voidCb;", callback.StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_NonVoidCallback_StaticField_HasReturnType()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true, throws: false, isAsync: false,
            callbackName: "boolCb");

        var callback = proj.CallbackDeclarations[0];
        Assert.NotNull(callback.StaticFieldDeclaration);
        // delegate* should end with return type
        Assert.Contains(", bool>", callback.StaticFieldDeclaration!);
        // Static field should be initialized with method address
        Assert.Contains("= &boolCb;", callback.StaticFieldDeclaration!);
    }

    [Fact]
    public void Closure_DoesNotRequireSwiftWrapper()
    {
        var proj = new ClosureProjection(
            Array.Empty<ITypeProjection>(), null, true, false, false, "cb");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region AsyncProjection

    [Fact]
    public void Async_TaskT_Types()
    {
        var inner = new StringProjection();
        var proj = new AsyncProjection(inner, throws: true, callbackPrefix: "test");
        Assert.Equal("global::System.Threading.Tasks.Task<string>", proj.PublicType);
        Assert.Equal("void", proj.PInvokeType);
    }

    [Fact]
    public void Async_Task_VoidReturn_Types()
    {
        var proj = new AsyncProjection(innerReturnProjection: null, throws: false, callbackPrefix: "test");
        Assert.Equal("global::System.Threading.Tasks.Task", proj.PublicType);
        Assert.Equal("void", proj.PInvokeType);
    }

    [Fact]
    public void Async_RequiresSwiftWrapper()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        Assert.True(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Async_GetSwiftWrapperCode_NotNull()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$s10TestModule9fetchDatayys5Int64VYaKF",
            ModuleName = "TestModule",
            MethodName = "fetchData",
            OriginalCallExpression = "TestModule.fetchData()"
        });
        Assert.NotNull(code);
        Assert.Contains("@_silgen_name", code);
        Assert.Contains("$s10TestModule9fetchDatayys5Int64VYaKF_async", code);
        Assert.Contains("Task {", code);
        Assert.Contains("callback", code);
        Assert.Contains("errorCallback", code);
        Assert.Contains("_SBWTaskEntry", code);
        Assert.Contains("defer {", code);
        Assert.Contains("TestModule.fetchData()", code);
        Assert.Contains("CancellationError", code);
    }

    [Fact]
    public void Async_SwiftWrapper_NonThrowing_NoTryCatch()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            ModuleName = "TestModule",
            MethodName = "fetchData",
            OriginalCallExpression = "fetchData()"
        });
        Assert.NotNull(code);
        Assert.DoesNotContain("do {", code);
        Assert.DoesNotContain("catch", code);
        Assert.DoesNotContain("errorCallback", code);
        Assert.Contains("await fetchData()", code);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesMethodNameFallback()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            ModuleName = "TestModule",
            MethodName = "fetchData"
        });
        Assert.NotNull(code);
        // Without OriginalCallExpression, falls back to MethodName()
        Assert.Contains("await fetchData()", code);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesMangledNameForSilgenName()
    {
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$s4Test6myFuncyyYaF",
            ModuleName = "Test",
            MethodName = "myFunc"
        });
        Assert.NotNull(code);
        Assert.Contains("@_silgen_name(\"$s4Test6myFuncyyYaF_async\")", code);
    }

    [Fact]
    public void Async_ReturnPlan_AsyncCallback_HasTCS()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var plan = proj.GetReturnPlan("handle", ReturnStrategy.AsyncCallback);

        Assert.NotEmpty(plan.SetupStatements);
        var tcsLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("TaskCompletionSource", tcsLine.Code);

        var holderLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[1]);
        Assert.Contains("new object[]", holderLine.Code);
    }

    [Fact]
    public void Async_CallbackDeclarations_Throwing_HasSuccessAndError()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var callbacks = proj.CallbackDeclarations;

        Assert.Equal(2, callbacks.Count);
        Assert.Equal("testSuccessCallback", callbacks[0].MethodName);
        Assert.Equal("testErrorCallback", callbacks[1].MethodName);
    }

    [Fact]
    public void Async_CallbackDeclarations_NonThrowing_SuccessOnly()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var callbacks = proj.CallbackDeclarations;

        Assert.Single(callbacks);
        Assert.Equal("testSuccessCallback", callbacks[0].MethodName);
    }

    [Fact]
    public void Async_SuccessCallback_HasResultConversion()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var callback = proj.CallbackDeclarations[0];

        Assert.Contains("SwiftString rawResult", callback.Signature);
        var bodyCode = string.Join("\n", callback.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("TrySetResult", bodyCode);
        Assert.Contains("ToString()", bodyCode);
    }

    [Fact]
    public void Async_ErrorCallback_HasExceptionCreation()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: true, callbackPrefix: "test");
        var errorCallback = proj.CallbackDeclarations[1];

        var bodyCode = string.Join("\n", errorCallback.Body.OfType<MarshalStatement.Line>().Select(l => l.Code));
        // A Swift CancellationError must surface as a CANCELED Task (await throws
        // OperationCanceledException, Task.IsCanceled == true), not a faulted one —
        // TrySetException(new OperationCanceledException(...)) lands the Task in
        // Faulted and breaks Task.IsCanceled / when-any cancellation filtering.
        Assert.Contains("TrySetCanceled", bodyCode);
        Assert.DoesNotContain("TrySetException(isCancelled", bodyCode);
        Assert.DoesNotContain("new OperationCanceledException", bodyCode);
        // Non-cancel errors still fault the Task with the marshalled message.
        Assert.Contains("TrySetException", bodyCode);
        Assert.Contains("PtrToStringUTF8", bodyCode);
        Assert.Contains("SwiftException(errorMessage)", bodyCode);
    }

    [Fact]
    public void Async_SwiftWrapper_UsesSwiftTypeNames()
    {
        // Int64 should map to Int64 in Swift, not remain as C# type
        var proj = new AsyncProjection(new BlittableProjection("Int64"), throws: true, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$sTest",
            ModuleName = "Test",
            MethodName = "fetch",
            OriginalCallExpression = "fetch()"
        });
        Assert.NotNull(code);
        Assert.Contains("Int64, Int64", code); // return param + task param in callback
    }

    [Fact]
    public void Async_SwiftWrapper_UsesContextSwiftCallbackReturnType()
    {
        // When SwiftCallbackReturnType is provided, it should be used instead of mapping
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var code = proj.GetSwiftWrapperCode(new SwiftWrapperContext
        {
            MangledName = "$sTest",
            ModuleName = "Test",
            MethodName = "getName",
            OriginalCallExpression = "getName()",
            SwiftCallbackReturnType = "String"
        });
        Assert.NotNull(code);
        Assert.Contains("String, Int64", code); // String from context, Int64 for task
    }

    [Fact]
    public void Async_ExposesInnerProjection()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new AsyncProjection(inner, false, "test");
        Assert.Same(inner, proj.InnerReturnProjection);
    }

    #endregion

    #region Element Conversion Defaults

    [Fact]
    public void Blittable_ElementConversions_AreNull()
    {
        ITypeProjection proj = new BlittableProjection("Int64");
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void Bool_ElementConversions_AreNull()
    {
        ITypeProjection proj = new BoolProjection();
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void String_ElementConversions()
    {
        var proj = new StringProjection();
        Assert.Equal("new SwiftString(e)", proj.GetParameterElementConversion("e"));
        Assert.Equal("e.ToString()", proj.GetReturnElementConversion("e"));
        Assert.True(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void SimpleEnum_ElementConversions_AreNull()
    {
        // Enums are blittable — no element conversion needed inside containers.
        // Standalone parameter/return plans handle the cast to/from underlying type.
        var proj = new SimpleEnumProjection("Direction", "int");
        Assert.Null(proj.GetParameterElementConversion("e"));
        Assert.Null(proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void ObjCBridged_ElementConversions()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        Assert.Equal("(IntPtr)e.Handle", proj.GetParameterElementConversion("e"));
        Assert.Equal("ObjCRuntime.Runtime.GetNSObject<UIImage>(e)!", proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void NonFrozenStruct_ElementConversions()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        Assert.Equal("e.Payload.DangerousGetHandle()", proj.GetParameterElementConversion("e"));
        // Return element conversion is null — when used inside Optional, ToNullable() handles
        // construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
        Assert.Null(proj.GetReturnElementConversion("e"));
    }

    [Fact]
    public void NativeRemapped_ElementConversions()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        Assert.Equal("new SwiftURL(e)", proj.GetParameterElementConversion("e"));
        // MarshalFromSwiftType = _swiftWrapperType, so container elements are already
        // the wrapper type — just call the conversion method directly (no re-wrapping).
        Assert.Equal("e.ToNSUrl()", proj.GetReturnElementConversion("e"));
        Assert.True(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void NativeRemapped_InArray_ParamPlan_UsesFromFactoryMethod()
    {
        // Array<Foundation.NSUrl> parameter — element conversion should use FromNSUrl factory method
        var elem = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var proj = new ArrayProjection(elem, isParameter: true);
        var plan = proj.GetParameterPlan("urls");

        Assert.Equal("urlsBuffer", plan.PInvokeExpression);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(", firstLine.Code);
        Assert.Contains("SwiftURL.FromNSUrl", firstLine.Code);
        // Should have disposal since NativeRemapped.ElementRequiresDisposal = true
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
    }

    [Fact]
    public void NativeRemapped_InArray_ReturnPlan_UsesToConversionMethod()
    {
        // Array<Foundation.NSUrl> return — element conversion should use ToNSUrl
        var elem = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var proj = new ArrayProjection(elem, isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains(".AsProjected(e =>", plan.PInvokeExpression);
        Assert.Contains("ToNSUrl()", plan.PInvokeExpression);
        // Must NOT contain the namespace-qualified fallback "ToFoundation.NSUrl"
        Assert.DoesNotContain("ToFoundation", plan.PInvokeExpression);
    }

    #endregion

    #region CallbackDeclaration Defaults

    [Fact]
    public void SimpleProjections_HaveNoCallbackDeclarations()
    {
        ITypeProjection proj = new BlittableProjection("Int64");
        Assert.Empty(proj.CallbackDeclarations);

        proj = new StringProjection();
        Assert.Empty(proj.CallbackDeclarations);

        proj = new BoolProjection();
        Assert.Empty(proj.CallbackDeclarations);
    }

    #endregion
}
