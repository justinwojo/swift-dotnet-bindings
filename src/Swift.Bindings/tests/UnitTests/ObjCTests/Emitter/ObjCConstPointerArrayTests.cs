// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// A pointer to a value type is structurally identical whether it addresses ONE value or the FIRST
/// ELEMENT of an array, and the two want opposite projections. These tests pin the three outcomes
/// the emitter must produce, none of which a compiler can distinguish from the others:
///
/// <list type="bullet">
/// <item>a MUTABLE pointer with no count keyword really is one caller-allocated slot → <c>out T</c>;</item>
/// <item>a pointer paired with a <c>count:</c> keyword is an array → an <c>[Internal]</c>
/// pointer+count member plus a pinning array overload;</item>
/// <item>a CONST pointer with no count is read-only (so never an <c>out</c>, whose call-site
/// semantics zero the caller's storage before the callee runs) and has no length to project an
/// array from either → the member drops with a recorded skip.</item>
/// </list>
/// </summary>
public class ObjCConstPointerArrayTests
{
    // ─────────────────────────────────────────────
    // Type-mapper split: shape vs. constness
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("const CGPoint *")]
    [InlineData("const CLLocationCoordinate2D *")]
    [InlineData("const NSInteger *")]
    public void IsValueTypePointerParameter_ConstPointee_IsNotAnOutParameter(string qualType)
    {
        var typeRef = ObjCTypeRefParser.Parse(qualType);
        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null));
        Assert.True(ObjCTypeMapper.IsConstValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null));
    }

    [Theory]
    [InlineData("CGPoint *")]
    [InlineData("CLLocationCoordinate2D *")]
    [InlineData("NSInteger *")]
    public void IsValueTypePointerParameter_MutablePointee_IsStillAnOutParameter(string qualType)
    {
        var typeRef = ObjCTypeRefParser.Parse(qualType);
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null));
        Assert.False(ObjCTypeMapper.IsConstValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null));
    }

    /// <summary>
    /// The structural half is const-blind on purpose: the array projection accepts both flavours of
    /// pointee (a const buffer the callee reads, a mutable one it fills), so it must not be gated on
    /// the qualifier that only decides the SINGLE-value projection.
    /// </summary>
    [Theory]
    [InlineData("const CGPoint *")]
    [InlineData("CGPoint *")]
    public void IsValueTypePointerShape_IgnoresConstness(string qualType)
    {
        Assert.True(ObjCTypeMapper.IsValueTypePointerShape(
            ObjCTypeRefParser.Parse(qualType), typedefMap: null, enumNames: null));
    }

    [Theory]
    [InlineData("const NSString *")]
    [InlineData("const void *")]
    [InlineData("const id")]
    public void IsConstValueTypePointerParameter_NonValueTypePointee_ReturnsFalse(string qualType)
    {
        Assert.False(ObjCTypeMapper.IsConstValueTypePointerParameter(
            ObjCTypeRefParser.Parse(qualType), typedefMap: null, enumNames: null));
    }

    // ─────────────────────────────────────────────
    // Fail-closed: a const pointer with no count has no sound projection
    // ─────────────────────────────────────────────

    [Fact]
    public void Method_ConstValueTypePointerWithoutCount_DropsWithRecordedSkip()
    {
        var module = BuildClass(Method("distanceFromOrigin:", "CGFloat", ("point", "const CGPoint *")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("DistanceFromOrigin", apiDefinition);
        Assert.DoesNotContain("distanceFromOrigin:", apiDefinition);
        Assert.Null(arrayOverloads);

        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "distanceFromOrigin:");
        Assert.Equal("Method", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
        // The detail has to name the offending parameter and the reason a reader can act on.
        Assert.Contains("point", skip.Detail);
        Assert.Contains("const CGPoint *", skip.Detail);
    }

    /// <summary>The positive control for the drop above: a mutable single slot still binds.</summary>
    [Fact]
    public void Method_MutableValueTypePointerWithoutCount_StillEmitsOutParameter()
    {
        var module = BuildClass(Method("tryFirstPoint:", "BOOL", ("outPoint", "CGPoint *")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.Contains("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "tryFirstPoint:");
    }

    /// <summary>
    /// A protocol requirement has no implementation to hang a pinning overload off, so the array
    /// escape hatch is unavailable there and a const pointer parameter can only fail closed.
    /// </summary>
    [Fact]
    public void ProtocolRequirement_ConstValueTypePointerWithCount_DropsWithRecordedSkip()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol("TLConsumer", p => p.Method(Method("consumePoints:count:", "void",
                ("points", "const CGPoint *"), ("count", "NSUInteger"))))
            .Build();

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("ConsumePoints", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "consumePoints:count:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    /// <summary>
    /// A C function carries no selector keyword at all, so nothing can ever identify its const
    /// pointer parameter as an array — the whole function drops.
    /// </summary>
    [Fact]
    public void Function_ConstValueTypePointer_DropsWithRecordedSkip()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLDistanceFromOrigin",
                ReturnType = ObjCTypeRefParser.Parse("CGFloat"),
                Parameters = [Param("point", "const CGPoint *")],
            })
            .Build();

        var (content, diagnostics) = EmitStructsAndEnumsWithDiagnostics(module);

        Assert.DoesNotContain("TLDistanceFromOrigin", content);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "TLDistanceFromOrigin");
        Assert.Equal("Function", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
        Assert.Contains("point", skip.Detail);
    }

    [Fact]
    public void Function_MutableValueTypePointer_StillEmitsOutParameter()
    {
        var module = ObjCModuleBuilder.Create()
            .WithFunction(new ObjCFunctionDecl
            {
                Name = "TLReadFirstPoint",
                ReturnType = ObjCTypeRefParser.Parse("BOOL"),
                Parameters = [Param("outPoint", "CGPoint *")],
            })
            .Build();

        var (content, diagnostics) = EmitStructsAndEnumsWithDiagnostics(module);

        Assert.Contains("out CGPoint", content);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "TLReadFirstPoint");
    }

    // ─────────────────────────────────────────────
    // Array projection: pointer + count: → one C# array parameter
    // ─────────────────────────────────────────────

    [Fact]
    public void Method_ConstPointerWithCount_BindsAsArrayNotOutParameter()
    {
        var module = BuildClass(ClassMethod("bufferWithPoints:count:", "instancetype",
            ("points", "const CGPoint *"), ("count", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        // The declaration bgen sees is the raw buffer half, marked internal so it is not the API a
        // consumer reaches for — and emphatically not an `out`, which would zero the input buffer.
        Assert.Contains("[Internal]", apiDefinition);
        Assert.Contains("IntPtr points", apiDefinition);
        Assert.DoesNotContain("out CGPoint", apiDefinition);

        // The API a consumer reaches for takes the array and supplies the count itself.
        Assert.NotNull(arrayOverloads);
        Assert.Contains("partial class TLPointBuffer", arrayOverloads);
        Assert.Contains("CGPoint[] points", arrayOverloads);
        Assert.DoesNotContain("nuint count", arrayOverloads);

        Assert.Empty(diagnostics.SkippedSymbols);
    }

    /// <summary>
    /// The projection is keyed on the count keyword, not on constness: a MUTABLE pointer paired with
    /// a count is an output buffer of <c>count</c> elements, and an <c>out T</c> there would hand the
    /// callee room for exactly one.
    /// </summary>
    [Fact]
    public void Method_MutablePointerWithCount_BindsAsArrayNotOutParameter()
    {
        var module = BuildClass(Method("copyPointsInto:count:", "void",
            ("points", "CGPoint *"), ("count", "NSUInteger")));

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Contains("IntPtr points", apiDefinition);
        Assert.NotNull(arrayOverloads);
        Assert.Contains("CGPoint[] points", arrayOverloads);
    }

    [Fact]
    public void ArrayOverload_StaticSelector_EmitsStaticOverloadReturningTheClass()
    {
        var module = BuildClass(ClassMethod("bufferWithPoints:count:", "instancetype",
            ("points", "const CGPoint *"), ("count", "NSUInteger")));

        var (_, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        Assert.Contains("public static TLPointBuffer BufferWithPoints(CGPoint[] points)", arrayOverloads);
    }

    [Fact]
    public void ArrayOverload_InstanceSelector_ForwardsRemainingParametersUnchanged()
    {
        var module = BuildClass(Method("appendPoints:count:scaledBy:", "void",
            ("points", "const CGPoint *"), ("count", "NSUInteger"), ("scale", "CGFloat")));

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        // The trailing value-type parameter keeps its projected type and passes straight through.
        Assert.Contains("public void AppendPoints(CGPoint[] points, nfloat scale)", arrayOverloads);
        Assert.Contains("scale", arrayOverloads);
        Assert.Contains("nfloat scale", apiDefinition);
    }

    /// <summary>
    /// The public overload claims the natural name; the raw pointer+count member it forwards to takes
    /// the underscored one. Both must exist, and the overload must call the member it declares.
    /// </summary>
    [Fact]
    public void ArrayOverload_ForwardsToTheInternalMemberThatWasActuallyDeclared()
    {
        var module = BuildClass(Method("appendPoints:count:", "void",
            ("points", "const CGPoint *"), ("count", "NSUInteger")));

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        Assert.Contains("void _AppendPoints(IntPtr points, nuint count);", apiDefinition);
        Assert.Contains("_AppendPoints(", arrayOverloads);
        Assert.Contains("public void AppendPoints(CGPoint[] points)", arrayOverloads);
    }

    /// <summary>
    /// The overload pins the managed array rather than copying it, so the callee reads the caller's
    /// own storage — the whole point of the projection.
    /// </summary>
    [Fact]
    public void ArrayOverload_PinsTheArrayForTheDurationOfTheCall()
    {
        var module = BuildClass(Method("appendPoints:count:", "void",
            ("points", "const CGPoint *"), ("count", "NSUInteger")));

        var (_, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        Assert.Contains("fixed (CGPoint*", arrayOverloads);
        // `fixed` is only legal in an unsafe context, and bgen declares its own half of the class
        // unsafe as well.
        Assert.Contains("unsafe partial class", arrayOverloads);
    }

    // ─────────────────────────────────────────────
    // Array projection: shapes that must NOT be guessed at
    // ─────────────────────────────────────────────

    /// <summary>
    /// Only an exact <c>count</c> keyword directly after the pointer is taken as an element count.
    /// A length in some other unit (bytes, a stride, a range) would make the array length a guess,
    /// and guessing wrong reintroduces the same silent-wrong-data failure in a new shape.
    /// </summary>
    [Theory]
    [InlineData("appendPoints:length:")]
    [InlineData("appendPoints:byteCount:")]
    [InlineData("appendPoints:stride:")]
    public void Method_ConstPointerWithNonCountKeyword_DropsInsteadOfGuessing(string selector)
    {
        var module = BuildClass(Method(selector, "void", ("points", "const CGPoint *"), ("n", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("AppendPoints", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == selector && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    /// <summary>
    /// A count that is not an integer is not a count. Nothing is projected, and the const pointer
    /// then has no sound projection left.
    /// </summary>
    [Fact]
    public void Method_ConstPointerWithNonIntegralCount_DropsInsteadOfGuessing()
    {
        var module = BuildClass(Method("appendPoints:count:", "void",
            ("points", "const CGPoint *"), ("count", "NSString *")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("AppendPoints", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "appendPoints:count:");
    }

    /// <summary>
    /// A member gets ONE array overload, so a selector carrying two pointer+count pairs cannot be
    /// projected without leaving the other pair mis-bound. Neither is projected.
    /// </summary>
    [Fact]
    public void Method_TwoCandidatePairs_ProjectsNeitherAndDrops()
    {
        var module = BuildClass(Method("mergePoints:count:withPoints:count:", "void",
            ("first", "const CGPoint *"), ("firstCount", "NSUInteger"),
            ("second", "const CGPoint *"), ("secondCount", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("MergePoints", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "mergePoints:count:withPoints:count:");
    }

    /// <summary>
    /// A block parameter is re-typed by bgen into a generated delegate, so a forwarding call written
    /// against the ApiDefinition spelling would not compile. The projection declines, and the const
    /// pointer beside it then has nowhere to go.
    /// </summary>
    [Fact]
    public void Method_PassThroughParameterBgenRetypes_DeclinesTheProjection()
    {
        var module = BuildClass(new ObjCMethodDecl
        {
            Selector = "appendPoints:count:completion:",
            ReturnType = ObjCTypeRefParser.Parse("void"),
            IsInstanceMethod = true,
            Parameters =
            [
                Param("points", "const CGPoint *"),
                Param("count", "NSUInteger"),
                new ObjCParameterDecl
                {
                    Name = "completion",
                    Type = new ObjCTypeRef { Name = "", IsBlock = true, BlockReturnType = ObjCTypeRefParser.Parse("void") },
                },
            ],
        });

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("AppendPoints", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "appendPoints:count:completion:");
    }

    /// <summary>
    /// A run that produces no overloads must clear a file left behind by a previous generate: the
    /// SDK reuses one intermediate directory, and a stale overload would reference an internal member
    /// the current ApiDefinition no longer declares.
    /// </summary>
    [Fact]
    public void ArrayOverloadsFile_RemovedWhenTheRunProducesNone()
    {
        var module = BuildClass(Method("sumOfX", "CGFloat"));

        var (_, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module, seedStaleArrayOverloads: true);

        Assert.Null(arrayOverloads);
    }

    // ─────────────────────────────────────────────
    // Constness a typedef hides
    // ─────────────────────────────────────────────

    /// <summary>
    /// Clang spells <c>typedef const CGPoint TLConstPoint; -m:(TLConstPoint *)p</c> as plain
    /// <c>TLConstPoint *</c>, so the qualifier survives only on the typedef's own target. Reading the
    /// parameter's own flag alone would call a read-only input mutable and project it as an
    /// <c>out</c>.
    /// </summary>
    [Fact]
    public void IsValueTypePointerParameter_ConstnessHiddenByTypedef_IsNotAnOutParameter()
    {
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["TLConstPoint"] = ObjCTypeRefParser.Parse("const CGPoint"),
        };
        var typeRef = ObjCTypeRefParser.Parse("TLConstPoint *");

        Assert.False(ObjCTypeMapper.IsValueTypePointerParameter(typeRef, typedefMap, enumNames: null));
        Assert.True(ObjCTypeMapper.IsConstValueTypePointerParameter(typeRef, typedefMap, enumNames: null));
    }

    /// <summary>
    /// Chain flattening keeps only the last hop's type, so a <c>const</c> applied at an intermediate
    /// alias has to be carried forward or it vanishes from the resolved map entirely.
    /// </summary>
    [Fact]
    public void BuildResolvedTypedefMap_ConstAppliedAtIntermediateAlias_StaysConst()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef("TLPoint", "CGPoint")
            .WithTypedef(new ObjCTypedefDecl { Name = "TLConstPoint", UnderlyingType = ObjCTypeRefParser.Parse("const TLPoint") })
            .Build();

        var resolved = ObjCTypeMapper.BuildResolvedTypedefMap(module);

        Assert.True(ObjCTypeMapper.HasConstPointee(ObjCTypeRefParser.Parse("TLConstPoint *"), resolved));
        Assert.False(ObjCTypeMapper.HasConstPointee(ObjCTypeRefParser.Parse("TLPoint *"), resolved));
    }

    [Fact]
    public void Method_ConstPointeeBehindTypedef_DropsWithRecordedSkip()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLConstPoint", UnderlyingType = ObjCTypeRefParser.Parse("const CGPoint") })
            .WithClass("TLPointBuffer", "NSObject", c => c.Method(
                Method("distanceFromOrigin:", "CGFloat", ("point", "TLConstPoint *"))))
            .Build();

        var (apiDefinition, _, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "distanceFromOrigin:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    // ─────────────────────────────────────────────
    // Indirection a typedef hides
    // ─────────────────────────────────────────────
    //
    // A typedef can carry the pointer itself (`typedef const CGPoint *TLPointRun;`), and Clang spells
    // such a parameter with the alias name alone. Deciding pointer-ness from the usage only reads a
    // pointer parameter as a plain value, and the member then binds the struct BY VALUE — the callee
    // is handed a copy in registers where it expects an address.

    [Fact]
    public void IsValueTypePointerShape_IndirectionSuppliedByTypedef_IsStillAPointerShape()
    {
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["TLPointRef"] = ObjCTypeRefParser.Parse("CGPoint *"),
        };
        var typeRef = ObjCTypeRefParser.Parse("TLPointRef");

        Assert.True(ObjCTypeMapper.IsValueTypePointerShape(typeRef, typedefMap, enumNames: null));
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef, typedefMap, enumNames: null));
        Assert.Equal("CGPoint", ObjCTypeMapper.MapValueTypePointerParameterType(typeRef, typedefMap));
    }

    /// <summary>
    /// The alias supplying the pointer AND the usage adding one is a double pointer, which addresses
    /// a pointer rather than a value — no more an out-param than the <c>NSError **</c> the direct
    /// spelling rejects.
    /// </summary>
    [Fact]
    public void IsValueTypePointerShape_TypedefPointerDereferencedAgain_IsNotAPointerShape()
    {
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["TLPointRef"] = ObjCTypeRefParser.Parse("CGPoint *"),
        };

        Assert.False(ObjCTypeMapper.IsValueTypePointerShape(
            ObjCTypeRefParser.Parse("TLPointRef *"), typedefMap, enumNames: null));
    }

    /// <summary>
    /// Chain flattening keeps only the last hop's type, so a <c>*</c> applied at an intermediate
    /// alias has to be carried forward exactly like a <c>const</c> — otherwise the resolved entry
    /// says "plain value" about a pointer and every later reader agrees with it.
    /// </summary>
    [Fact]
    public void BuildResolvedTypedefMap_PointerAppliedAtIntermediateAlias_StaysAPointer()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef("TLPoint", "CGPoint")
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRun", UnderlyingType = ObjCTypeRefParser.Parse("const TLPoint *") })
            .Build();

        var resolved = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var typeRef = ObjCTypeRefParser.Parse("TLPointRun");

        Assert.True(ObjCTypeMapper.IsValueTypePointerShape(typeRef, resolved, enumNames: null));
        Assert.True(ObjCTypeMapper.IsConstValueTypePointerParameter(typeRef, resolved, enumNames: null));
        Assert.Equal("CGPoint", ObjCTypeMapper.MapValueTypePointerParameterType(typeRef, resolved));
    }

    /// <summary>
    /// The levels accumulate: an alias that adds a <c>*</c> to an alias that already added one names
    /// a pointer to a pointer, which is no more a single-value out slot than <c>NSError **</c>.
    /// </summary>
    [Fact]
    public void BuildResolvedTypedefMap_PointerAppliedTwiceAcrossTheChain_IsNotAPointerShape()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRef", UnderlyingType = ObjCTypeRefParser.Parse("CGPoint *") })
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRefRef", UnderlyingType = ObjCTypeRefParser.Parse("TLPointRef *") })
            .Build();

        var resolved = ObjCTypeMapper.BuildResolvedTypedefMap(module);

        Assert.True(ObjCTypeMapper.IsValueTypePointerShape(
            ObjCTypeRefParser.Parse("TLPointRef"), resolved, enumNames: null));
        Assert.False(ObjCTypeMapper.IsValueTypePointerShape(
            ObjCTypeRefParser.Parse("TLPointRefRef"), resolved, enumNames: null));
    }

    /// <summary>
    /// Qualifying an alias that is ALREADY a pointer makes the pointer read-only, not the value it
    /// addresses (<c>CGPoint *const</c>), so the callee can still write through it and the parameter
    /// stays a legal single-value <c>out</c> slot. Treating that as a read-only pointee would drop a
    /// bindable member.
    /// </summary>
    [Fact]
    public void BuildResolvedTypedefMap_ConstQualifyingAPointerAlias_LeavesThePointeeMutable()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRef", UnderlyingType = ObjCTypeRefParser.Parse("CGPoint *") })
            .WithTypedef(new ObjCTypedefDecl { Name = "TLConstPointRef", UnderlyingType = ObjCTypeRefParser.Parse("const TLPointRef") })
            .Build();

        var resolved = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var typeRef = ObjCTypeRefParser.Parse("TLConstPointRef");

        Assert.False(ObjCTypeMapper.HasConstPointee(typeRef, resolved));
        Assert.True(ObjCTypeMapper.IsValueTypePointerParameter(typeRef, resolved, enumNames: null));
    }

    /// <summary>
    /// The companion case: the pointer hop comes first and the <c>const</c> is applied to the value
    /// BELOW it, which really does make the pointee read-only.
    /// </summary>
    [Fact]
    public void BuildResolvedTypedefMap_ConstBelowAPointerHop_KeepsThePointeeReadOnly()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLConstPoint", UnderlyingType = ObjCTypeRefParser.Parse("const CGPoint") })
            .WithTypedef(new ObjCTypedefDecl { Name = "TLConstPointRun", UnderlyingType = ObjCTypeRefParser.Parse("TLConstPoint *") })
            .Build();

        var resolved = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var typeRef = ObjCTypeRefParser.Parse("TLConstPointRun");

        Assert.True(ObjCTypeMapper.HasConstPointee(typeRef, resolved));
        Assert.True(ObjCTypeMapper.IsConstValueTypePointerParameter(typeRef, resolved, enumNames: null));
    }

    [Fact]
    public void Method_ArrayPointerBehindChainedTypedef_ProjectsAsAnArrayOverload()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef("TLPoint", "CGPoint")
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRun", UnderlyingType = ObjCTypeRefParser.Parse("const TLPoint *") })
            .WithClass("TLPointBuffer", "NSObject", c => c.Method(
                Method("appendPoints:count:", "void", ("points", "TLPointRun"), ("count", "NSUInteger"))))
            .Build();

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.Contains("IntPtr points", apiDefinition);
        Assert.NotNull(arrayOverloads);
        Assert.Contains("CGPoint[] points", arrayOverloads);
    }

    [Fact]
    public void Method_ConstPointerBehindTypedef_DropsInsteadOfBindingTheStructByValue()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRun", UnderlyingType = ObjCTypeRefParser.Parse("const CGPoint *") })
            .WithClass("TLPointBuffer", "NSObject", c => c.Method(
                Method("distanceFromOrigin:", "CGFloat", ("point", "TLPointRun"))))
            .Build();

        var (apiDefinition, _, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("DistanceFromOrigin", apiDefinition);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "distanceFromOrigin:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    /// <summary>
    /// The array projection reads the same shape test, so a run of values reached through an alias
    /// projects exactly like the directly-spelled one instead of silently binding one struct.
    /// </summary>
    [Fact]
    public void Method_ArrayPointerBehindTypedef_ProjectsAsAnArrayOverload()
    {
        var module = ObjCModuleBuilder.Create()
            .WithTypedef(new ObjCTypedefDecl { Name = "TLPointRun", UnderlyingType = ObjCTypeRefParser.Parse("const CGPoint *") })
            .WithClass("TLPointBuffer", "NSObject", c => c.Method(
                Method("appendPoints:count:", "void", ("points", "TLPointRun"), ("count", "NSUInteger"))))
            .Build();

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.Contains("IntPtr points", apiDefinition);
        Assert.NotNull(arrayOverloads);
        Assert.Contains("CGPoint[] points", arrayOverloads);
        Assert.Contains("fixed (CGPoint* __arrayPtr = points)", arrayOverloads);
    }

    // ─────────────────────────────────────────────
    // Fail-closed: an array-shaped pointer no overload can rescue
    // ─────────────────────────────────────────────
    //
    // Constness is not the only unsound shape. A pointer the SELECTOR declares to be a run of values
    // — by naming a `count:` right after it — needs storage for `count` elements, and `out T` supplies
    // exactly one; the callee then reads or writes past the end of it. Wherever the array overload
    // cannot be built, that member must drop rather than fall back to the `out` projection.

    [Fact]
    public void Category_MutablePointerWithCount_DropsInsteadOfEmittingOutParameter()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass("TLPointBuffer", "NSObject")
            .WithCategory("Bulk", "TLPointBuffer", c => c.Method(
                Method("copyPointsInto:count:", "void", ("points", "CGPoint *"), ("count", "NSUInteger"))))
            .Build();

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "copyPointsInto:count:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    [Fact]
    public void ProtocolRequirement_MutablePointerWithCount_DropsWithRecordedSkip()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol("TLConsumer", p => p.Method(Method("fillPoints:count:", "void",
                ("points", "CGPoint *"), ("count", "NSUInteger"))))
            .Build();

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "fillPoints:count:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    /// <summary>
    /// A constructor becomes a bgen <c>Constructor</c> declaration, which a partial-class overload
    /// cannot forward to, so the array escape hatch is unavailable there too.
    /// </summary>
    [Fact]
    public void Constructor_MutablePointerWithCount_DropsWithRecordedSkip()
    {
        var module = BuildClass(Method("initWithPoints:count:", "instancetype",
            ("points", "CGPoint *"), ("count", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "initWithPoints:count:" && s.Reason == ObjCSkipReason.UnsupportedConstruct);
    }

    /// <summary>
    /// The projection declines a selector carrying two pairs; the pointers must not then fall through
    /// to the single-value projection they were never compatible with.
    /// </summary>
    [Fact]
    public void Method_TwoCandidateMutablePairs_DropsInsteadOfEmittingOutParameters()
    {
        var module = BuildClass(Method("mergePoints:count:withPoints:count:", "void",
            ("first", "CGPoint *"), ("firstCount", "NSUInteger"),
            ("second", "CGPoint *"), ("secondCount", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "mergePoints:count:withPoints:count:");
    }

    /// <summary>
    /// A count this projection cannot consume is still a declaration about an array. The pointer does
    /// not quietly revert to <c>out</c> just because the count came back unusable.
    /// </summary>
    [Fact]
    public void Method_MutablePointerWithNonIntegralCount_DropsInsteadOfGuessing()
    {
        var module = BuildClass(Method("fillPoints:count:", "void",
            ("points", "CGPoint *"), ("count", "NSString *")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.DoesNotContain("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolName == "fillPoints:count:");
    }

    /// <summary>The positive control: a mutable pointer with a NON-count sibling is still one slot.</summary>
    [Fact]
    public void Method_MutablePointerWithUnrelatedSibling_StillEmitsOutParameter()
    {
        var module = BuildClass(Method("readPoint:atIndex:", "BOOL",
            ("outPoint", "CGPoint *"), ("index", "NSUInteger")));

        var (apiDefinition, arrayOverloads, diagnostics) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.Contains("out CGPoint", apiDefinition);
        Assert.Null(arrayOverloads);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "readPoint:atIndex:");
    }

    // ─────────────────────────────────────────────
    // Array overload fidelity
    // ─────────────────────────────────────────────

    /// <summary>
    /// A count narrower than the array length must not wrap: an unchecked narrowing conversion would
    /// hand the callee a length that does not describe the buffer it was given (a 256-element array
    /// through a <c>uint8_t count</c> arrives as zero).
    /// </summary>
    [Fact]
    public void ArrayOverload_NarrowCountType_ConvertsTheLengthChecked()
    {
        var module = BuildClass(Method("appendPoints:count:", "void",
            ("points", "const CGPoint *"), ("count", "uint8_t")));

        var (_, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        Assert.Contains("checked((byte)", arrayOverloads);
    }

    /// <summary>
    /// The overload is the member consumers call, so it has to carry the selector's platform floor —
    /// otherwise the platform analyzer sees an unconditionally-available API and stops warning about
    /// calling a newer selector from an older deployment target.
    /// </summary>
    [Fact]
    public void ArrayOverload_CarriesTheSelectorsAvailability()
    {
        var method = Method("appendPoints:count:", "void", ("points", "const CGPoint *"), ("count", "NSUInteger"));
        var module = BuildClass(method with
        {
            Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "17.0" }],
        });

        var (apiDefinition, arrayOverloads, _) = EmitApiDefinitionWithArrayOverloads(module);

        Assert.NotNull(arrayOverloads);
        Assert.Contains("SupportedOSPlatform(\"ios17.0\")", arrayOverloads);
        // The internal half keeps its own annotation; the two must not drift apart.
        Assert.Contains("SupportedOSPlatform(\"ios17.0\")", apiDefinition);
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private static ObjCModule BuildClass(ObjCMethodDecl method) =>
        ObjCModuleBuilder.Create()
            .WithClass("TLPointBuffer", "NSObject", c => c.Method(method))
            .Build();

    private static ObjCMethodDecl Method(string selector, string returnType, params (string name, string type)[] parameters) =>
        BuildMethod(selector, returnType, isInstanceMethod: true, parameters);

    private static ObjCMethodDecl ClassMethod(string selector, string returnType, params (string name, string type)[] parameters) =>
        BuildMethod(selector, returnType, isInstanceMethod: false, parameters);

    private static ObjCMethodDecl BuildMethod(string selector, string returnType, bool isInstanceMethod, (string name, string type)[] parameters) =>
        new()
        {
            Selector = selector,
            ReturnType = ObjCTypeRefParser.Parse(returnType),
            IsInstanceMethod = isInstanceMethod,
            Parameters = parameters.Select(p => Param(p.name, p.type)).ToList(),
        };

    private static ObjCParameterDecl Param(string name, string qualType) =>
        new() { Name = name, Type = ObjCTypeRefParser.Parse(qualType) };
}
