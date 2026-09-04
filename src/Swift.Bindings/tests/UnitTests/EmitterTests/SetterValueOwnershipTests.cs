// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the ownership decision for the value a Swift stored-property setter is handed.
///
/// <para>SILGen lowers the setter as <c>(@owned Value, @guaranteed self) -&gt; ()</c>, so the new
/// value is handed across at +1 whenever the P/Invoke reaches Swift's own accessor — directly, or
/// through the generated native assembly thunk, which shifts registers and tail-calls without
/// owning anything. A Swift-source wrapper is the one arm that borrows: it introduces a frame whose
/// parameter is <c>@guaranteed</c> and passes its own +1 on, so the caller keeps its copy and must
/// still destroy it.</para>
///
/// <para>The thunk arm is the reason this oracle exists apart from the Optional-width oracle beside
/// it. Getting it backwards is silent: too few counts and a strong store takes zero net references,
/// too many and the value leaks. Both halves — the arm that emits a transfer and the arm that
/// leaves a destroy armed — read the single predicate asserted here.</para>
/// </summary>
public class SetterValueOwnershipTests
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
            SetterValueOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
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
            SetterValueOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
    }

    /// <summary>
    /// Only the value is consumed. A subscript setter's indices sit beside it under the ordinary
    /// <c>@guaranteed</c> convention, so transferring them would drop their only release.
    /// </summary>
    [Fact]
    public void SubscriptIndices_AreNotHandedOver()
    {
        var setter = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.NativeThunk, usesWrapperLibrary: true);
        setter.CSSignature.Add(CreateArg("index", new NamedTypeSpec("Swift.Int")));

        Assert.True(SetterValueOwnership.IsHandedOverToCallee(setter, setter.CSSignature[1]));
        Assert.False(SetterValueOwnership.IsHandedOverToCallee(setter, setter.CSSignature[2]));
    }

    /// <summary>
    /// A getter has no <c>@owned</c> parameter to hand over, so nothing in its signature is
    /// consumed even on the arms that consume a setter's value.
    /// </summary>
    [Fact]
    public void GetterArguments_AreNeverHandedOver()
    {
        var getter = CreateSetter(new NamedTypeSpec("TestModule.Payload"), WrapperStrategy.NativeThunk, usesWrapperLibrary: true);
        getter.Name = "payload_Get";

        Assert.False(SetterValueOwnership.IsHandedOverToCallee(getter, getter.CSSignature[1]));
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
        Assert.True(SetterValueOwnership.IsHandedOverToCallee(thunked, thunked.CSSignature[1]));
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
}
