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
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IMyProtocol>", result);
    }

    /// <summary>
    /// A borrowing callee on the auto-wrap arm keeps the cached carrier: the wrapped proxy's own
    /// construction reference stays the only one, and the caller destroys it when the wrapper dies.
    /// This is the control the two consuming cases below are read against.
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
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IMyProtocol>", result);
        Assert.DoesNotContain("CreateOwned", result);
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
        Assert.Contains("ExistentialContainerFactory.CreateOwnedExistential1<IMyProtocol>", result);
        Assert.DoesNotContain("GetOrCreate", result);
        // The wrap fallback still rides along, or a plain C# conformer has nothing to box.
        Assert.Contains("new MyProtocolProxy(__v)", result);
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
        Assert.Contains("ExistentialContainerFactory.CreateOwnedExistential1ConsumerOwned<IMyProtocol>", result);
        Assert.Contains("ProxyImplOwnership.ConsumerOwned", result);
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
        Assert.Contains(
            "CreateOwnedCompositionExistential<IFirstAndSecond, Swift.Runtime.ExistentialContainer2>",
            result);
        Assert.DoesNotContain("GetExistentialContainer()", result);
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
        Assert.Contains("ExistentialContainerFactory.CreateOwnedExistential1<IMyClassBound>", result);
        Assert.Contains("classBoundCarrier: true", result);
        Assert.StartsWith("Swift.Runtime.ClassExistentialContainer1.FromExistentialContainer1(", result);
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
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IMyClassBound>", result);
        Assert.DoesNotContain("classBoundCarrier", result);
        Assert.StartsWith("Swift.Runtime.ClassExistentialContainer1.FromExistentialContainer1(", result);
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
