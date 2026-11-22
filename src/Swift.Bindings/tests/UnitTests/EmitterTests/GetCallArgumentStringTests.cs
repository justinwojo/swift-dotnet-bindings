// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Signature.GetCallArgumentString — all 25+ pattern branches.
/// </summary>
public class GetCallArgumentStringTests
{
    [Fact]
    public void GetCallArgumentString_SafeHandle_ReturnsPayload()
    {
        var param = new Parameter(MarshalledType.NonFrozenSafeHandle, "loader");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("loader.Payload", result);
    }

    [Fact]
    public void GetCallArgumentString_EnumSafeHandle_ReturnsDangerousGetHandle()
    {
        var param = new Parameter(MarshalledType.EnumSafeHandle, "status");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("status.Payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SimpleEnumInt64_ReturnsCast()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("Int64", "Direction"), "direction");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(Int64)direction", result);
    }

    [Fact]
    public void GetCallArgumentString_ExistentialContainer_ReturnsConversion()
    {
        var param = new Parameter(new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol"), "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("handlerExistentialLease.Container", result);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.NotNull(lease);
        Assert.Equal("using var handlerExistentialLease = global::Swift.Runtime.BorrowedExistentialArgument.Begin<IMyProtocol>(handler);", lease!.Declaration);
        Assert.False(lease.CompletesOnNativeReturn);
    }

    /// <summary>
    /// A borrowing callee on the auto-wrap arm keeps the cached carrier: the wrapped proxy's own
    /// construction reference stays the only one, and the caller destroys it when the wrapper dies.
    /// This is the control the two consuming cases below are read against. The carrier is read off
    /// a lease declared before the call, which holds the proxy until the call returns: the proxy is
    /// otherwise cached only weakly, so a collection during the call would release the box Swift is
    /// borrowing.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ExistentialWithProxy_Borrowed_ReusesTheCachedCarrier()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
            {
                ProxyClassName = "MyProtocolProxy",
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("handlerExistentialLease.Container", result);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.NotNull(lease);
        Assert.Equal("using var handlerExistentialLease = global::Swift.Runtime.BorrowedExistentialArgument.Begin<IMyProtocol>(handler, static __v => new MyProtocolProxy(__v));", lease!.Declaration);
        Assert.False(lease.CompletesOnNativeReturn);
        Assert.DoesNotContain("CreateOwned", lease.Declaration);
    }

    /// <summary>
    /// A consumed boxed <c>any Error</c> passes the box retained by a lease declared before the call,
    /// which the body completes after native return; a throw before then releases the retain. The
    /// borrowed one reads the box inline and needs no lease.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetCallArgumentString_BoxedError_ConsumedThroughALease(bool handedOver)
    {
        var param = new Parameter(
            new MarshalledType.Existential("System.IntPtr", "global::Swift.Foundation.AnyError")
            {
                BoxedError = true,
                HandedOverToCallee = handedOver,
            },
            "failure");
        var result = Signature.GetCallArgumentString(param);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.DoesNotContain("UnknownObjectRetain", result);
        if (handedOver)
        {
            Assert.Equal("failureErrorTransfer.Pointer", result);
            Assert.NotNull(lease);
            Assert.Equal("failureErrorTransfer", lease!.Name);
            Assert.StartsWith("using var failureErrorTransfer = new global::Swift.Runtime.OwnedArgument.ObjectTransfer(", lease.Declaration);
            Assert.Contains("GetExistentialContainer().Payload0, failure);", lease.Declaration);
            Assert.True(lease.CompletesOnNativeReturn);
        }
        else
        {
            Assert.EndsWith("GetExistentialContainer().Payload0", result);
            Assert.Null(lease);
        }
    }

    /// <summary>
    /// A callee that releases the container has to be handed a reference of its own. Reusing the
    /// cached carrier here aliases the auto-wrapped proxy's sole construction reference, so the
    /// callee's release takes a count the caller still owns and the conformer dies under a live
    /// managed wrapper.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ExistentialWithProxy_HandedOver_MintsItsOwnCarrier()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
            {
                ProxyClassName = "MyProtocolProxy",
                HandedOverToCallee = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        var lease = Signature.GetExistentialArgumentLease(param);
        // The mint happens before the call, in a lease the body completes once Swift has the count.
        Assert.Equal("handlerExistentialLease.Container", result);
        Assert.NotNull(lease);
        Assert.Contains("OwnedExistentialArgument.Begin<IMyProtocol>(handler, ", lease!.Declaration);
        // The wrap fallback still rides along, or a plain C# conformer has nothing to box.
        Assert.Contains("new MyProtocolProxy(__v)", lease.Declaration);
        Assert.Contains("classBoundCarrier: false", lease.Declaration);
        Assert.True(lease.CompletesOnNativeReturn);
    }

    /// <summary>
    /// The non-retaining sink and the consuming callee are independent axes: a
    /// <c>weak</c>/<c>unowned</c> setter whose callee also releases needs BOTH the consumer-owned
    /// proxy flavor and a freshly minted carrier, not one or the other.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ConsumerOwnedExistential_HandedOver_MintsThroughTheConsumerOwnedLane()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
            {
                ProxyClassName = "MyProtocolProxy",
                ConsumerOwnsCarrier = true,
                HandedOverToCallee = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.Equal("handlerExistentialLease.Container", result);
        Assert.NotNull(lease);
        Assert.Contains("OwnedExistentialArgument.BeginConsumerOwned<IMyProtocol>(handler, ", lease!.Declaration);
        Assert.Contains("ProxyImplOwnership.ConsumerOwned", lease.Declaration);
        Assert.True(lease.CompletesOnNativeReturn);
    }

    /// <summary>
    /// Compositions never reach the auto-wrap factory — the only C# type implementing one is a
    /// Swift-vended proxy, and its <c>GetExistentialContainer()</c> BORROWS the bytes it stores. A
    /// consuming callee therefore needs a value-witness copy of those bytes; passing them unchanged
    /// aliases the proxy's own reference.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_CompositionExistential_HandedOver_CopiesTheBorrowedBytes()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer2", "IFirstAndSecond")
            {
                HandedOverToCallee = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.Equal("handlerExistentialLease.Container", result);
        Assert.NotNull(lease);
        Assert.Contains(
            "OwnedExistentialArgument.BeginComposition<IFirstAndSecond, Swift.Runtime.ExistentialContainer2>(handler)",
            lease!.Declaration);
        Assert.True(lease.CompletesOnNativeReturn);
    }

    /// <summary>
    /// Borrowing control for the composition arm: with no consuming callee the stored bytes travel
    /// as they are, and a copy minted here would be a reference nobody releases.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_CompositionExistential_Borrowed_PassesTheStoredBytes()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer2", "IFirstAndSecond"),
            "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("GetExistentialContainer()", result);
        Assert.DoesNotContain("CreateOwned", result);
    }

    /// <summary>
    /// Bare <c>Any</c> lands in an <c>ExistentialContainer0</c>, which carries no witness table and
    /// no protocol identity for the runtime to copy through. It stays on the borrowed form even
    /// under a consuming callee — the ownership gate excludes it rather than minting a carrier the
    /// runtime cannot describe.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_BareAnyExistential_HandedOver_StaysBorrowed()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer0", "object")
            {
                HandedOverToCallee = true,
            },
            "value");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("GetExistentialContainer()", result);
        Assert.DoesNotContain("CreateOwned", result);
    }

    /// <summary>
    /// A well-known one-word carrier (a boxed <c>any Error</c>) is an arity-1 container whose public
    /// type is not a generated interface, so it lands past the auto-wrap arm. The arity-generic
    /// composition copy would pick its value witness from the word count alone and read the words
    /// that carrier leaves unused, so a consuming callee keeps the borrowed form here rather than
    /// getting a copy made through the wrong layout.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_WellKnownArity1Carrier_HandedOver_StaysBorrowed()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "Swift.Foundation.AnyError")
            {
                HandedOverToCallee = true,
            },
            "error");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("GetExistentialContainer()", result);
        Assert.DoesNotContain("CreateOwned", result);
    }

    /// <summary>
    /// A class-constrained protocol existential travels as the two-word
    /// <c>[classRef][witnessTable]</c> pair, so the argument has to be narrowed to the class carrier
    /// the declaration speaks in — and the owned mint has to be told, or the opaque value witness
    /// reads the words outside the pair as an inline payload and its metadata.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ClassBoundExistential_HandedOver_MintsThroughTheClassLayout()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ClassExistentialContainer1", "IMyClassBound")
            {
                ProxyClassName = "MyClassBoundProxy",
                HandedOverToCallee = true,
                ClassBoundArity1 = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.Equal("Swift.Runtime.ClassExistentialContainer1.FromExistentialContainer1(handlerExistentialLease.Container)", result);
        Assert.NotNull(lease);
        Assert.Contains("OwnedExistentialArgument.Begin<IMyClassBound>(handler, ", lease!.Declaration);
        Assert.Contains("classBoundCarrier: true", lease.Declaration);
        Assert.True(lease.CompletesOnNativeReturn);
    }

    /// <summary>
    /// A consumed existential's +1 must not be minted inside the call's argument list: a later
    /// argument that throws while the list is evaluated (a disposed proxy, an incompatible
    /// implementation) would strand it with no owner. Every consumed existential of a call is
    /// minted in its own lease before the call, and the call only reads the leases.
    /// </summary>
    [Fact]
    public void CallArguments_ConsumedExistentials_MintNothingInsideTheCall()
    {
        var signature = new Signature(
            "void",
            new[]
            {
                new Parameter(
                    new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
                    {
                        ProxyClassName = "MyProtocolProxy",
                        HandedOverToCallee = true,
                    },
                    "first"),
                new Parameter(
                    new MarshalledType.Existential("Swift.Runtime.ExistentialContainer2", "IFirstAndSecond")
                    {
                        HandedOverToCallee = true,
                    },
                    "second"),
            });
        var call = signature.CallArgumentsString();
        Assert.Equal("firstExistentialLease.Container, secondExistentialLease.Container", call);
        Assert.DoesNotContain("CreateOwned", call);
        Assert.DoesNotContain("GetOrCreate", call);
        foreach (var parameter in signature.Parameters)
            Assert.True(Signature.GetExistentialArgumentLease(parameter)!.CompletesOnNativeReturn);
    }

    /// <summary>
    /// A consumed existential with no proxy to wrap with (an interface only Swift-vended or boxable
    /// conformers implement) still gets its own lease rather than an inline mint.
    /// </summary>
    [Fact]
    public void GetExistentialArgumentLease_ConsumedExistentialWithoutProxy_MintsWithoutAWrap()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
            {
                HandedOverToCallee = true,
            },
            "handler");
        var lease = Signature.GetExistentialArgumentLease(param);
        Assert.NotNull(lease);
        Assert.Equal(
            "using var handlerExistentialLease = global::Swift.Runtime.OwnedExistentialArgument.Begin<IMyProtocol>(handler, classBoundCarrier: false);",
            lease!.Declaration);
        Assert.Equal("handlerExistentialLease.Container", Signature.GetCallArgumentString(param));
    }

    /// <summary>
    /// The lease is a local of the member's body, so it must not take a name a parameter already
    /// has: a parameter spelled like another one's lease leaves that lease, declaration and read
    /// alike, under a fresh name.
    /// </summary>
    [Fact]
    public void ExistentialArgumentLeaseName_AParameterSpelledLikeTheLease_MovesTheLease()
    {
        var scope = SyntheticLocalNames.Resolve(MethodTakingParametersNamed("handler", "handlerExistentialLease"));
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol")
            {
                ProxyClassName = "MyProtocolProxy",
                HandedOverToCallee = true,
            },
            "handler");
        var name = Signature.ExistentialArgumentLeaseName(param, scope);
        Assert.NotEqual("handlerExistentialLease", name);
        Assert.StartsWith($"using var {name} = ", Signature.GetExistentialArgumentLease(param, scope)!.Declaration);
        Assert.Equal($"{name}.Container", Signature.GetCallArgumentString(param, leaseScope: scope));
    }

    private static MethodDecl MethodTakingParametersNamed(params string[] names)
    {
        static ArgumentDecl Argument(string name) => new()
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        var signature = new List<ArgumentDecl> { Argument(string.Empty) };
        signature.AddRange(names.Select(Argument));
        return new MethodDecl
        {
            Name = "compute",
            MangledName = "$s10TestModule7computeyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Borrowing control for the class-bound arm: the mint argument only qualifies a hand-over, but
    /// the narrowing is about the wire shape and applies to a borrowed call site just the same.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ClassBoundExistential_Borrowed_TakesNoMintArgument()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ClassExistentialContainer1", "IMyClassBound")
            {
                ProxyClassName = "MyClassBoundProxy",
                ClassBoundArity1 = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("Swift.Runtime.ClassExistentialContainer1.FromExistentialContainer1(handlerExistentialLease.Container)", result);
        Assert.Contains("BorrowedExistentialArgument.Begin<IMyClassBound>(handler, static __v => new MyClassBoundProxy(__v))", Signature.GetExistentialArgumentLease(param)!.Declaration);
        Assert.DoesNotContain("classBoundCarrier", Signature.GetExistentialArgumentLease(param)!.Declaration);
    }

    /// <summary>
    /// The conformance the source expression comes through is the opaque one — a proxy implements
    /// <c>ISwiftExistentialConvertible&lt;ExistentialContainer1&gt;</c> whatever its protocol's
    /// constraint — so a class-bound parameter with no interface public type still reads the opaque
    /// container and narrows, rather than casting to a conformance nothing implements.
    /// </summary>
    [Fact]
    public void GetCallArgumentString_ClassBoundExistential_NonInterfacePublicType_ReadsOpaqueThenNarrows()
    {
        var param = new Parameter(
            new MarshalledType.Existential("Swift.Runtime.ClassExistentialContainer1", "object")
            {
                ClassBoundArity1 = true,
            },
            "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("ISwiftExistentialConvertible<Swift.Runtime.ExistentialContainer1>", result);
        Assert.StartsWith("Swift.Runtime.ClassExistentialContainer1.FromExistentialContainer1(", result);
    }

    /// <summary>
    /// The declaration side of the same fact: what the P/Invoke declares for a class-constrained
    /// existential parameter is the two-word carrier. The opaque container is 40 bytes, which the
    /// call passes indirectly — the callee would read the caller's buffer address as the object.
    /// </summary>
    [Fact]
    public void PInvokeParameters_ClassBoundExistential_DeclaresTheTwoWordCarrier()
    {
        var signature = new Signature(
            "void",
            new[]
            {
                new Parameter(
                    new MarshalledType.Existential("Swift.Runtime.ClassExistentialContainer1", "IMyClassBound")
                    {
                        ClassBoundArity1 = true,
                    },
                    "handler"),
            });
        var declared = signature.PInvokeParametersString();
        Assert.Contains("Swift.Runtime.ClassExistentialContainer1 handler", declared);
        Assert.DoesNotContain("Swift.Runtime.ExistentialContainer1 handler", declared);
    }

    [Fact]
    public void GetCallArgumentString_IntPtrFromNonFrozen_ReturnsHandle()
    {
        var param = new Parameter(MarshalledType.NonFrozenIntPtr, "response");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("responseHandle", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferRef_ReturnsBufferRefDisposable()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "point", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref pointDisposable.BufferRef", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferNonRef_ReturnsBuffer()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "point");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("pointDisposable.Buffer", result);
    }

    [Fact]
    public void GetCallArgumentString_OutModifier_ReturnsOutVar()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "result", "out");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("out var result", result);
    }

    [Fact]
    public void GetCallArgumentString_RefModifier_ReturnsRef()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "value", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref value", result);
    }

    [Fact]
    public void GetCallArgumentString_SwiftClosureData_ReturnsClosure()
    {
        var param = new Parameter(MarshalledType.SwiftClosureLegacy, "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackClosure", result);
    }

    [Fact]
    public void GetCallArgumentString_DelegateUnmanaged_ReturnsFuncPtr()
    {
        var param = new Parameter(new MarshalledType.ConventionCFuncPtr("delegate* unmanaged[Cdecl]<long, void>"), "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackFuncPtr", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfClass_ReturnsHandleDeref()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_selfClass");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("_handle.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfFixed_ReturnsCastSelf()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_selfFixed");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(IntPtr)__self", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfIntPtr_ReturnsPayloadHandle()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_self");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("_payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_PlainType_ReturnsName()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "count");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("count", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncCallback_ReturnsName()
    {
        var param = new Parameter(MarshalledType.AsyncCallback, "onComplete");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("onComplete", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncErrorCallback_ReturnsName()
    {
        var param = new Parameter(MarshalledType.AsyncErrorCallback, "onError");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("onError", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncContext_ReturnsNull()
    {
        var param = new Parameter(MarshalledType.AsyncContext, "context");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("null", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncTask_ReturnsGCHandleConversion()
    {
        var param = new Parameter(MarshalledType.AsyncTask, "task");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("GCHandle.ToIntPtr(task)", result);
    }

    [Fact]
    public void GetCallArgumentString_CdeclClosureFuncPtr_ReturnsHandleGuard()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureFuncPtr("onComplete", "handler"), "funcPtr");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("handlerHandle.IsAllocated", result);
        Assert.Contains("s_onComplete", result);
        Assert.Contains("IntPtr.Zero", result);
    }

    [Fact]
    public void GetCallArgumentString_CdeclClosureContext_ReturnsHandleGuard()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureContext("handler"), "context");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("handlerHandle.IsAllocated", result);
        Assert.Contains("GCHandle.ToIntPtr(handlerHandle)", result);
        Assert.Contains("IntPtr.Zero", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncThrowingContext_ReturnsContextPtr()
    {
        var param = new Parameter(new MarshalledType.AsyncThrowingContext("callback"), "ctx");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackContextPtr", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncThrowingStartFunc_ReturnsStartFunc()
    {
        var funcPtrType = "delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>";
        var param = new Parameter(new MarshalledType.AsyncThrowingStartFunc("onStart", funcPtrType), "startFunc");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("s_onStart_Start", result);
    }

    [Fact]
    public void GetCallArgumentString_ObjCBridged_ReturnsHandle()
    {
        var param = new Parameter(new MarshalledType.ObjCBridged("UIImage"), "image");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("imageHandle", result);
    }

    [Fact]
    public void GetCallArgumentString_NativeRemappedSafeHandle_ReturnsSwiftPayload()
    {
        var param = new Parameter(MarshalledType.NativeRemappedNonFrozen, "url");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("urlSwift.Payload", result);
    }

    [Fact]
    public void GetCallArgumentString_NativeRemapped_ReturnsSwiftSuffix()
    {
        var param = new Parameter(new MarshalledType.NativeRemappedFrozen("URL"), "url");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("urlSwift", result);
    }
}
