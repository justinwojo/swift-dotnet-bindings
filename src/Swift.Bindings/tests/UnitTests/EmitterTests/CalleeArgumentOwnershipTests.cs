// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins which arguments Swift's own lowering takes <c>@owned</c>, and on which call arms that
/// convention reaches the C# caller.
///
/// <para>SILGen lowers an initializer as <c>(@owned A, @owned B, …, @thin Self.Type) -&gt; @owned
/// Self</c> and a setter as <c>(@owned Value, @owned Index…, @inout self) -&gt; ()</c>: the callee
/// releases what it was handed. An ordinary <c>func</c> — and a subscript <em>getter</em> over the
/// very same indices — borrows instead. So the convention follows the member kind, not the
/// parameter position or the argument's type.</para>
///
/// <para>Reaching Swift's own symbol is the other half. Direct dispatch does, and so does the
/// generated native assembly thunk, which shifts registers and tail-calls without owning anything.
/// A Swift-source wrapper is the arm that borrows: it introduces a frame whose parameter is
/// <c>@guaranteed</c> and mints its own +1 when it forwards, so the caller keeps its copy and must
/// still destroy it.</para>
///
/// <para>The thunk arm is the reason this oracle exists apart from the Optional-width oracle beside
/// it. Getting it backwards is silent: too few counts and a strong store takes zero net references,
/// too many and the value leaks. Both halves — the arm that emits a transfer and the arm that
/// leaves a destroy armed — read the single predicate asserted here.</para>
/// </summary>
public class CalleeArgumentOwnershipTests
{
    /// <summary>
    /// Every arm that reaches Swift's own accessor consumes the value; every Swift-source wrapper
    /// borrows it. The native thunk is the arm the width oracle and the ownership oracle disagree
    /// on, and it belongs on the consuming side.
    /// </summary>
    [Theory]
    // Direct CallConvSwift dispatch onto the accessor symbol.
    [InlineData(WrapperStrategy.None, false, false, false, false, true)]
    // Ownership-transparent assembly thunk: still the accessor's own convention.
    [InlineData(WrapperStrategy.NativeThunk, false, false, false, false, true)]
    // The thunk's entry point lives in the generated wrapper library, which must not by itself
    // demote it to a borrowing arm.
    [InlineData(WrapperStrategy.NativeThunk, true, false, false, false, true)]
    // Swift-source wrappers: a frame that borrows its parameter.
    [InlineData(WrapperStrategy.CdeclProperty, true, false, false, false, false)]
    [InlineData(WrapperStrategy.CdeclMethod, true, false, false, false, false)]
    [InlineData(WrapperStrategy.None, true, false, false, false, false)]
    [InlineData(WrapperStrategy.None, false, true, false, false, false)]
    [InlineData(WrapperStrategy.None, false, false, true, false, false)]
    // Async bridging hands the value to a completion-handler frame, not to the accessor.
    [InlineData(WrapperStrategy.None, false, false, false, true, false)]
    public void SetterValue_IsHandedOver_OnlyWhenCalleeIsTheAccessor(
        WrapperStrategy strategy,
        bool usesWrapperLibrary,
        bool usesFreeFunctionWrapper,
        bool hasOptionalPointerWrapper,
        bool isAsync,
        bool expectedHandOver)
    {
        var setter = CreateSetter(
            new NamedTypeSpec("TestModule.Payload"),
            strategy,
            usesWrapperLibrary,
            usesFreeFunctionWrapper,
            hasOptionalPointerWrapper,
            isAsync);

        Assert.Equal(
            expectedHandOver,
            CalleeArgumentOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
    }

    /// <summary>
    /// An initializer consumes its value parameters exactly as a setter consumes its new value, and
    /// on the same arms. This is the case that went unmodelled: a constructor is not an accessor, so
    /// an oracle written around the setter's first parameter answered "borrowed" for every
    /// reference-bearing argument a direct <c>init</c> was handed.
    /// </summary>
    [Theory]
    [InlineData(WrapperStrategy.None, false, true)]
    [InlineData(WrapperStrategy.NativeThunk, true, true)]
    [InlineData(WrapperStrategy.CdeclMethod, true, false)]
    [InlineData(WrapperStrategy.None, true, false)]
    public void InitializerArguments_FollowTheSameCalleeDecision(
        WrapperStrategy strategy, bool usesWrapperLibrary, bool expectedHandOver)
    {
        var ctor = CreateInitializer(strategy, usesWrapperLibrary);

        Assert.Equal(
            expectedHandOver,
            CalleeArgumentOwnership.IsHandedOverToCallee(ctor, ctor.CSSignature[1]));
    }

    /// <summary>
    /// Every value parameter of a multi-parameter initializer is consumed, not just the first —
    /// <c>init(t: Tok, label: String, k: CIdx)</c> lowers as
    /// <c>(@owned Tok, @owned String, @owned CIdx, @thin Self.Type)</c>.
    /// </summary>
    [Fact]
    public void EveryInitializerArgument_IsHandedOver()
    {
        var ctor = CreateInitializer(WrapperStrategy.None, usesWrapperLibrary: false);
        ctor.CSSignature.Add(CreateArg("label", new NamedTypeSpec("Swift.String")));
        ctor.CSSignature.Add(CreateArg("k", new NamedTypeSpec("TestModule.Payload")));

        Assert.All(
            ctor.CSSignature.Skip(1),
            arg => Assert.True(CalleeArgumentOwnership.IsHandedOverToCallee(ctor, arg)));
    }

    /// <summary>
    /// An <c>Optional&lt;class&gt;</c> value carries the same <c>@owned</c> convention as the bare
    /// class: the decision is the callee's, not the C# type's. Its marshalling arm differs (a
    /// carrier buffer rather than a payload handle), so it must not reach a different answer.
    /// </summary>
    [Theory]
    [InlineData(WrapperStrategy.NativeThunk, true, true)]
    [InlineData(WrapperStrategy.None, false, true)]
    [InlineData(WrapperStrategy.CdeclProperty, true, false)]
    public void OptionalClassSetterValue_FollowsTheSameCalleeDecision(
        WrapperStrategy strategy, bool usesWrapperLibrary, bool expectedHandOver)
    {
        var optionalClass = new NamedTypeSpec(
            "Swift.Optional",
            new TypeSpec[] { new NamedTypeSpec("TestModule.Payload") });

        var setter = CreateSetter(optionalClass, strategy, usesWrapperLibrary);

        Assert.Equal(
            expectedHandOver,
            CalleeArgumentOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
    }

    /// <summary>
    /// A subscript setter's indices are consumed alongside its new value —
    /// <c>subscript(i: Idx, s: String) -&gt; Tok</c> lowers its setter as
    /// <c>(@owned Tok, @owned Idx, @owned String, @inout self)</c>. Borrowing them under-retains
    /// every index that carries a reference.
    /// </summary>
    [Fact]
    public void SubscriptIndices_AreHandedOverBesideTheValue()
    {
        var setter = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.NativeThunk, usesWrapperLibrary: true);
        setter.CSSignature.Add(CreateArg("index", new NamedTypeSpec("TestModule.Key")));

        Assert.True(CalleeArgumentOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
        Assert.True(CalleeArgumentOwnership.IsHandedOverToCallee(setter, setter.CSSignature[2]));
    }

    /// <summary>
    /// The non-retaining (weak/unowned) sink lane keys off the setter's FIRST parameter, which is a
    /// question about which slot the value lands in rather than about ownership. Widening the
    /// ownership predicate must not widen that one onto a subscript's indices.
    /// </summary>
    [Fact]
    public void NewValueSlot_StaysTheFirstParameterOnly()
    {
        var setter = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.None);
        setter.CSSignature.Add(CreateArg("index", new NamedTypeSpec("TestModule.Key")));

        Assert.True(CalleeArgumentOwnership.IsSetterNewValue(setter, setter.CSSignature[1]));
        Assert.False(CalleeArgumentOwnership.IsSetterNewValue(setter, setter.CSSignature[2]));
    }

    /// <summary>
    /// A getter borrows the same indices its setter consumes, and has no value parameter at all, so
    /// nothing in its signature is handed over even on the arms that consume a setter's value.
    /// A plain method borrows likewise.
    /// </summary>
    [Fact]
    public void GetterAndMethodArguments_AreNeverHandedOver()
    {
        var getter = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.NativeThunk, usesWrapperLibrary: true);
        getter.Name = "payload_Get";

        Assert.False(CalleeArgumentOwnership.IsHandedOverToCallee(getter, getter.CSSignature[1]));

        var method = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.None);
        method.Name = "take";
        method.IsAccessor = false;

        Assert.False(CalleeArgumentOwnership.IsHandedOverToCallee(method, method.CSSignature[1]));
    }

    /// <summary>
    /// The width oracle keeps its own answer. It is asked whether the value moves through memory —
    /// which the thunk does — and re-collapsing the two questions is exactly the confusion that
    /// under-retained a thunked strong store.
    /// </summary>
    [Fact]
    public void WidthOracle_StillCountsTheThunk_WhileOwnershipDoesNot()
    {
        var thunked = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.NativeThunk, usesWrapperLibrary: true);

        Assert.True(DirectOptionalAbi.UsesSwiftSideCarrier(thunked));
        Assert.True(CalleeArgumentOwnership.IsHandedOverToCallee(thunked, thunked.CSSignature[1]));
    }

    /// <summary>
    /// An explicit <c>borrowing</c> overrides the member-kind default for that one parameter:
    /// <c>init(w: borrowing W, n: String)</c> lowers as <c>(@guaranteed W, @owned String, …)</c>.
    /// Handing the borrowed one across at +1 would strand a count for the life of the process, and
    /// the sibling proves the override is per-parameter rather than disarming the whole member.
    /// </summary>
    [Fact]
    public void Initializer_BorrowingParameter_IsBorrowed_WhileItsSiblingIsStillHandedOver()
    {
        var ctor = CreateInitializer(WrapperStrategy.None, usesWrapperLibrary: false);
        ctor.CSSignature[1].Ownership = ParameterOwnership.Shared;
        ctor.CSSignature.Add(CreateArg("note", new NamedTypeSpec("Swift.String")));

        Assert.False(CalleeArgumentOwnership.IsHandedOverToCallee(ctor, ctor.CSSignature[1]));
        Assert.True(CalleeArgumentOwnership.IsHandedOverToCallee(ctor, ctor.CSSignature[2]));
    }

    /// <summary>
    /// A parameter Swift passes <c>inout</c> is an address the callee writes through, never a value
    /// it releases.
    /// </summary>
    [Fact]
    public void Initializer_InOutParameter_IsNeverHandedOver()
    {
        var ctor = CreateInitializer(WrapperStrategy.None, usesWrapperLibrary: false);
        ctor.CSSignature[1].Ownership = ParameterOwnership.InOut;

        Assert.False(CalleeArgumentOwnership.IsHandedOverToCallee(ctor, ctor.CSSignature[1]));
    }

    /// <summary>
    /// Setters are recognized by the accessor suffix the emitter gives them, so an ordinary Swift
    /// function whose own name ends that way looks identical by name alone. It borrows its
    /// arguments like any other function; classifying it as a setter would leak a count per call.
    /// </summary>
    [Fact]
    public void OrdinaryMethodWhoseNameEndsInTheAccessorSuffix_IsNotTreatedAsASetter()
    {
        var method = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.None);
        method.Name = "inspect_Set";
        method.IsAccessor = false;

        Assert.False(CalleeArgumentOwnership.IsHandedOverToCallee(method, method.CSSignature[1]));
        Assert.False(CalleeArgumentOwnership.IsSetterNewValue(method, method.CSSignature[1]));
    }

    /// <summary>
    /// The ObjC bridge reads its pointer off a wrapper the caller keeps owning — its own binding
    /// object, or the managed wrapper around a constant group's global NSString — so the plan has to
    /// carry a transfer for the arms that reach a consuming callee. The render site is what decides
    /// whether to write it, so declaring it unconditionally is the plan's whole job here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjCBridgeableParameterPlan_CarriesAHandOver(bool typedEnum)
    {
        var projection = new ObjCBridgeableProjection(
            "Foundation.NSUrl",
            typedEnum ? new AppleTypedEnumAdapter("VNBarcodeSymbology", "Foundation.NSString") : null);

        var plan = projection.GetParameterPlan("value");

        Assert.NotNull(plan.OwnedHandOverStatement);
        Assert.Contains(plan.PInvokeExpression, plan.OwnedHandOverStatement);
    }

    /// <summary>
    /// An array of ObjC-bridged elements crosses as one bridged container object, and a consuming
    /// callee releases that object exactly as it would a bare bridged argument. The leaf projection
    /// above already publishes a hand-over; the container plan has to publish one too, or an
    /// initializer taking <c>[NSURL]</c> hands the callee a +0 container it then releases.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjCBridgedArrayParameterPlan_CarriesAHandOver(bool nested)
    {
        ITypeProjection element = new ObjCBridgeableProjection("Foundation.NSUrl");
        if (nested)
            element = new ArrayProjection(element, isParameter: true);
        var projection = new ArrayProjection(element, isParameter: true);

        var plan = projection.GetParameterPlan("urls");

        Assert.NotNull(plan.OwnedHandOverStatement);
        Assert.Contains("urlsBuffer", plan.OwnedHandOverStatement);
        Assert.Contains(plan.PInvokeExpression, plan.OwnedHandOverStatement);
    }

    /// <summary>
    /// The dictionary and set siblings bridge through the same borrowed <c>using</c> wrapper as the
    /// array, so they have to publish the same transfer — an initializer taking
    /// <c>[String: URL]</c> or <c>Set&lt;URL&gt;</c> consumes the bridged container exactly as one
    /// taking <c>[URL]</c> does. Asserted across every bridged container shape so no sibling can
    /// drift back to +0 on its own.
    /// </summary>
    [Theory]
    [InlineData("dictionary")]
    [InlineData("set")]
    [InlineData("nestedSet")]
    public void ObjCBridgedDictionaryAndSetParameterPlans_CarryAHandOver(string shape)
    {
        ITypeProjection leaf = new ObjCBridgeableProjection("Foundation.NSUrl");
        ITypeProjection projection = shape switch
        {
            "dictionary" => new DictionaryProjection(new StringProjection(), leaf, isParameter: true),
            "set" => new SetProjection(leaf, isParameter: true),
            _ => new SetProjection(new ArrayProjection(leaf, isParameter: true), isParameter: true),
        };
        Assert.True(projection.UsesObjCContainerBridge);

        var plan = projection.GetParameterPlan("values");

        Assert.NotNull(plan.OwnedHandOverStatement);
        Assert.Contains("valuesBuffer", plan.OwnedHandOverStatement);
        Assert.Contains(plan.PInvokeExpression, plan.OwnedHandOverStatement);
    }

    /// <summary>
    /// An Optional over a bridged container builds the inner plan inside an <c>if</c> block and
    /// copies its handle into an outer buffer that is zero when the value is absent. The transfer
    /// has to be minted on that outer buffer — the inner one is out of scope at the call — and it
    /// has to follow the inner plan: a container that carries no hand-over gets none here either.
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("dictionary")]
    [InlineData("set")]
    public void OptionalObjCBridgedContainerParameterPlan_HandsOverTheOuterBuffer(string shape)
    {
        ITypeProjection leaf = new ObjCBridgeableProjection("Foundation.NSUrl");
        ITypeProjection inner = shape switch
        {
            "array" => new ArrayProjection(leaf, isParameter: true),
            "dictionary" => new DictionaryProjection(new StringProjection(), leaf, isParameter: true),
            _ => new SetProjection(leaf, isParameter: true),
        };
        var projection = new OptionalProjection(inner);

        var plan = projection.GetParameterPlan("values");

        Assert.Equal("valuesBuffer", plan.PInvokeExpression);
        Assert.NotNull(plan.OwnedHandOverStatement);
        Assert.Contains("(valuesBuffer)", plan.OwnedHandOverStatement);
        Assert.DoesNotContain("valuesValBuffer", plan.OwnedHandOverStatement);
    }

    /// <summary>
    /// The optional plan's collection wrapper must outlive the call: the `using` that owns it sits
    /// at method scope, guarded on the value being present, and the buffer the call and the
    /// hand-over read is taken off that same wrapper. A wrapper declared inside a narrower block
    /// is disposed — and the collection released — before either runs.
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("dictionary")]
    [InlineData("set")]
    public void OptionalObjCBridgedContainerParameterPlan_OwnerOutlivesTheCall(string shape)
    {
        ITypeProjection leaf = new ObjCBridgeableProjection("Foundation.NSUrl");
        ITypeProjection inner = shape switch
        {
            "array" => new ArrayProjection(leaf, isParameter: true),
            "dictionary" => new DictionaryProjection(new StringProjection(), leaf, isParameter: true),
            _ => new SetProjection(leaf, isParameter: true),
        };
        var plan = new OptionalProjection(inner).GetParameterPlan("values");

        Assert.DoesNotContain(plan.SetupStatements, s => s is MarshalStatement.Block);
        var owner = Assert.Single(plan.SetupStatements.OfType<MarshalStatement.Using>());
        Assert.EndsWith("?", owner.Type);
        Assert.Contains("is null ? null :", owner.InitExpression);

        var buffer = Assert.Single(plan.SetupStatements.OfType<MarshalStatement.Line>(),
            l => l.Code.StartsWith("IntPtr valuesBuffer"));
        Assert.Contains($"{owner.Name}.Handle", buffer.Code);
        Assert.Contains($"{owner.Name} is null ? IntPtr.Zero", buffer.Code);
        Assert.True(plan.SetupStatements.IndexOf(owner) < plan.SetupStatements.IndexOf(buffer),
            "the buffer is read off the wrapper, so the wrapper must be declared first");
    }

    /// <summary>
    /// The bare and the optional plans read their handle off the same owner declaration, so the
    /// collection is built the same way on both and neither can drift to a shorter-lived wrapper.
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("dictionary")]
    [InlineData("set")]
    public void ObjCBridgedContainerParameterPlan_BareAndOptionalShareOneOwner(string shape)
    {
        ITypeProjection leaf = new ObjCBridgeableProjection("Foundation.NSUrl");
        ITypeProjection inner = shape switch
        {
            "array" => new ArrayProjection(leaf, isParameter: true),
            "dictionary" => new DictionaryProjection(new StringProjection(), leaf, isParameter: true),
            _ => new SetProjection(leaf, isParameter: true),
        };

        var bare = inner.GetParameterPlan("values");
        var optional = new OptionalProjection(inner).GetParameterPlan("values");

        var bareOwner = Assert.Single(bare.SetupStatements.OfType<MarshalStatement.Using>());
        var optionalOwner = Assert.Single(optional.SetupStatements.OfType<MarshalStatement.Using>());
        Assert.Equal(bareOwner.Name, optionalOwner.Name);
        Assert.Equal($"{bareOwner.Type}?", optionalOwner.Type);
        Assert.Equal(bare.OwnedHandOverStatement, optional.OwnedHandOverStatement);
        Assert.Contains($"IntPtr valuesBuffer = {bareOwner.Name}.Handle;",
            bare.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
    }

    /// <summary>
    /// Every bridged shape mints its transfer through one helper, so the retain is the same
    /// isa-dispatched, null-tolerant call whether the handle came from a leaf wrapper or a
    /// container the plan built itself.
    /// </summary>
    [Fact]
    public void ObjCBridgedPlans_ShareOneHandOverShape()
    {
        var leaf = new ObjCBridgeableProjection("Foundation.NSUrl").GetParameterPlan("v");
        var array = new ArrayProjection(new ObjCBridgeableProjection("Foundation.NSUrl"), isParameter: true).GetParameterPlan("v");

        Assert.Equal(
            MarshallingHelpers.ObjCHandleHandOverStatement(leaf.PInvokeExpression),
            leaf.OwnedHandOverStatement);
        Assert.Equal(
            MarshallingHelpers.ObjCHandleHandOverStatement(array.PInvokeExpression),
            array.OwnedHandOverStatement);
        Assert.Contains("UnknownObjectRetain", MarshallingHelpers.ObjCHandleHandOverStatement("h"));
    }

    private static MethodDecl CreateSetter(
        TypeSpec valueType,
        WrapperStrategy strategy,
        bool usesWrapperLibrary = false,
        bool usesFreeFunctionWrapper = false,
        bool hasOptionalPointerWrapper = false,
        bool isAsync = false)
    {
        return new MethodDecl
        {
            Name = "payload_Set",
            MangledName = "$s10TestModule4Host7payloadAA7PayloadCvs",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = isAsync,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty),
                CreateArg("value", valueType),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            WrapperStrategy = strategy,
            UsesWrapperLibrary = usesWrapperLibrary,
            UsesFreeFunctionWrapper = usesFreeFunctionWrapper,
            HasOptionalPointerWrapper = hasOptionalPointerWrapper,
        };
    }

    private static MethodDecl CreateInitializer(
        WrapperStrategy strategy,
        bool usesWrapperLibrary)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4HostV7payloadAcA7PayloadC_tcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            IsAccessor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Host")),
                CreateArg("payload", new NamedTypeSpec("TestModule.Payload")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            WrapperStrategy = strategy,
            UsesWrapperLibrary = usesWrapperLibrary,
            UsesFreeFunctionWrapper = false,
            HasOptionalPointerWrapper = false,
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec)
        => new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

    // ---------------------------------------------------------------------------------------
    // Emitted output. The predicate above says WHICH arguments cross a consuming boundary; these
    // assert the emitter actually mints the transfer there and nowhere else.
    //
    // A class argument is the carrier that makes this observable at the emitter level: it has no
    // marshalling step of its own — the call site hands the object's payload handle straight to the
    // P/Invoke — so its transfer is a statement beside the call rather than something folded into a
    // value's lowering. The lever that puts these calls on Swift's own symbol is a NESTED frozen
    // struct beside the class argument, which is the shape the @_cdecl wrapper declines outright.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Consuming arm: an initializer releases every value parameter, so the caller has to hand over
    /// a reference of its own. Without it the strong store takes zero net references and the object
    /// dies while a live managed wrapper still owns it.
    /// </summary>
    [Fact]
    public void DirectInitializer_ClassArgument_IsHandedOver()
    {
        var typeDatabase = CreateEmissionTypeDatabase();
        var moduleDecl = CreateEmissionModule();
        var parentDecl = CreateEmissionStruct("Host", moduleDecl);
        CreateEmissionClass("Holder", moduleDecl, typeDatabase);

        var (csOutput, _) = EmitConstructor(
            CreateEmissionConstructor(parentDecl, moduleDecl, NestedFrozenArg(moduleDecl), ClassArg("holder", moduleDecl)),
            typeDatabase);

        Assert.Contains("CallConvSwift", csOutput);
        Assert.Equal(1, CountOccurrences(csOutput, "UnknownObjectRetain"));
    }

    /// <summary>
    /// Borrowing control on the same arm, the same carrier and the same type: an ordinary
    /// <c>func</c> takes its argument <c>@guaranteed</c>, so a transfer minted here is a reference
    /// nobody ever releases — the opposite failure, and just as silent.
    /// </summary>
    [Fact]
    public void DirectMethod_ClassArgument_IsBorrowed()
    {
        var typeDatabase = CreateEmissionTypeDatabase();
        var moduleDecl = CreateEmissionModule();
        var parentDecl = CreateEmissionStruct("Host", moduleDecl);
        CreateEmissionClass("Holder", moduleDecl, typeDatabase);

        var (csOutput, _) = EmitMethod(
            CreateEmissionMethod("inspect", parentDecl, moduleDecl, NestedFrozenArg(moduleDecl), ClassArg("holder", moduleDecl)),
            typeDatabase);

        Assert.Contains("CallConvSwift", csOutput);
        Assert.DoesNotContain("UnknownObjectRetain", csOutput);
    }

    /// <summary>
    /// An ObjC-bridged argument is read as a bare handle off a managed object that keeps owning it,
    /// so it needs the same transfer — retained isa-dispatched, because an NSObject-rooted payload
    /// needs <c>objc_retain</c> rather than <c>swift_retain</c>. Exactly one: the pass-through walk
    /// that covers plain Swift classes must not double up on the same argument.
    /// </summary>
    [Fact]
    public void DirectInitializer_ObjCBridgedArgument_IsHandedOverExactlyOnce()
    {
        var typeDatabase = CreateEmissionTypeDatabase();
        var moduleDecl = CreateEmissionModule();
        var parentDecl = CreateEmissionStruct("Host", moduleDecl);
        CreateEmissionClass("Bridged", moduleDecl, typeDatabase, TypeRecordFlags.ObjCBridged);

        var (csOutput, _) = EmitConstructor(
            CreateEmissionConstructor(parentDecl, moduleDecl, NestedFrozenArg(moduleDecl), ClassArg("bridged", moduleDecl, "Bridged")),
            typeDatabase);

        Assert.Contains("CallConvSwift", csOutput);
        Assert.Equal(1, CountOccurrences(csOutput, "UnknownObjectRetain"));
    }

    /// <summary>
    /// An ObjC-rooted parent runs its Swift init inside a separate static helper, so the transfer
    /// has to be minted in that helper's body rather than in the constructor body the other arms
    /// share. Nothing about the callee changes — the class argument is still consumed — so a
    /// hand-over that lives only on the shared path leaves this whole family under-retained.
    /// </summary>
    [Fact]
    public void DirectInitializer_ObjCRootedParent_ClassArgument_IsHandedOver()
    {
        var typeDatabase = CreateEmissionTypeDatabase();
        var moduleDecl = CreateEmissionModule();
        var parentDecl = CreateEmissionObjCRootedClass("RootedHost", moduleDecl, typeDatabase);
        CreateEmissionClass("Holder", moduleDecl, typeDatabase);

        var (csOutput, _) = EmitConstructor(
            CreateEmissionConstructor(parentDecl, moduleDecl, NestedFrozenArg(moduleDecl), ClassArg("holder", moduleDecl)),
            typeDatabase);

        Assert.Contains("CallConvSwift", csOutput);
        Assert.Equal(1, CountOccurrences(csOutput, "UnknownObjectRetain"));
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        handler.Emit(
            new CSharpWriter(csOutput),
            new SwiftWriter(swiftOutput),
            new MethodEnvironment(methodDecl, typeDatabase),
            new Conductor(new NullLoggerFactory()),
            TypeHandlerContext.Empty);
        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        handler.Emit(
            new CSharpWriter(csOutput),
            new SwiftWriter(swiftOutput),
            new MethodEnvironment(methodDecl, typeDatabase),
            new Conductor(new NullLoggerFactory()),
            TypeHandlerContext.Empty);
        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        for (int i = text.IndexOf(pattern); i >= 0; i = text.IndexOf(pattern, i + pattern.Length))
            count++;
        return count;
    }

    private static ModuleDecl CreateEmissionModule()
        => new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static StructDecl CreateEmissionStruct(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    /// <summary>
    /// A class parent that inherits NSObject, which routes its initializer through the separate
    /// static-helper emission path instead of the constructor body every other parent shares.
    /// </summary>
    private static ClassDecl CreateEmissionObjCRootedClass(
        string name,
        ModuleDecl moduleDecl,
        TypeDatabase typeDatabase)
    {
        var classDecl = CreateEmissionClass(name, moduleDecl, typeDatabase, TypeRecordFlags.ObjCRooted);
        classDecl.IsObjCRooted = true;
        return classDecl;
    }

    private static ClassDecl CreateEmissionClass(
        string name,
        ModuleDecl moduleDecl,
        TypeDatabase typeDatabase,
        TypeRecordFlags flags = TypeRecordFlags.None)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}");
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = swiftTypeName,
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}CMa",
                Flags = flags,
                Kind = TypeRecordKind.Class,
            })
        });

        return classDecl;
    }

    /// <summary>
    /// A frozen struct whose module-qualified name carries a second dot — i.e. one nested inside
    /// another type. That is the parameter shape the <c>@_cdecl</c> wrapper declines, which is what
    /// leaves the whole member (and every other argument in it) on Swift's own symbol.
    /// </summary>
    private static ArgumentDecl NestedFrozenArg(ModuleDecl moduleDecl)
        => CreateEmissionArg("inner", new NamedTypeSpec("TestModule.Outer.Inner"), moduleDecl);

    private static ArgumentDecl ClassArg(string name, ModuleDecl moduleDecl, string typeName = "Holder")
        => CreateEmissionArg(name, new NamedTypeSpec($"TestModule.{typeName}"), moduleDecl);

    private static ArgumentDecl CreateEmissionArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
        => new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl,
        };

    private static MethodDecl CreateEmissionConstructor(
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        params ArgumentDecl[] parameters)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateEmissionArg(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        signature.AddRange(parameters);

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4HostV5inner6holderAcA5Outer5InnerV_AA6HolderCtcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateEmissionMethod(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        params ArgumentDecl[] parameters)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateEmissionArg(string.Empty, TupleTypeSpec.Empty, moduleDecl)
        };
        signature.AddRange(parameters);

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule4HostV{name.Length}{name}yyAA5Outer5InnerV_AA6HolderCtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static TypeDatabase CreateEmissionTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Host"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Host"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Host"),
                MetadataAccessor = "$s10TestModule4HostVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Inner"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
                MetadataAccessor = "$s10TestModule5Outer5InnerVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8,
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }
}
