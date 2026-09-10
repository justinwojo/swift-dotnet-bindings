// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class NoncopyableArgumentEmitterTests
{
    [Theory]
    [InlineData(ParameterOwnership.Owned, false)]
    [InlineData(ParameterOwnership.Owned, true)]
    [InlineData(ParameterOwnership.Shared, false)]
    [InlineData(ParameterOwnership.Shared, true)]
    public void NoncopyableArgument_GuardAndLeaseEncloseNativeHandoff(ParameterOwnership ownership, bool throws)
    {
        var (cs, swift, method) = Emit(ownership, throws, noncopyable: true);
        Assert.True(method.UsesCdeclWrapper);
        int capture = cs.IndexOf("var resourceNonCopyablePayload = resource.Payload;", StringComparison.Ordinal);
        int guard = cs.IndexOf("resourceNonCopyablePayload.IsConsumed", StringComparison.Ordinal);
        int lease = cs.IndexOf("using var resourceNonCopyablePin =", StringComparison.Ordinal);
        int call = cs.IndexOf("PInvoke_useResource_", StringComparison.Ordinal);
        Assert.True(capture >= 0 && capture < guard && guard < lease && lease < call, cs);
        Assert.Contains("new global::Swift.Runtime.SafeHandlePin(resourceNonCopyablePayload)", cs);
        Assert.Contains("ObjectDisposedException(nameof(resource)", cs);
        if (ownership == ParameterOwnership.Owned)
        {
            int mark = cs.IndexOf("resourceNonCopyablePayload.MarkConsumed();", StringComparison.Ordinal);
            Assert.True(mark > call, cs);
            Assert.Contains(".move()", swift);
            Assert.DoesNotContain("resource.Payload.MarkConsumed()", cs);
            if (throws)
            {
                int errorDelivery = cs.IndexOf("SwiftMarshal.ThrowSwiftError", mark, StringComparison.Ordinal);
                Assert.True(errorDelivery > mark, cs);
            }
        }
        else
        {
            Assert.DoesNotContain("MarkConsumed", cs);
            Assert.DoesNotContain(".move()", swift);
        }
    }

    [Fact]
    public void CopyableArgument_DoesNotAcquireNoncopyableGuardOrLease()
    {
        var (cs, _, _) = Emit(ParameterOwnership.Shared, false, noncopyable: false);
        Assert.DoesNotContain("NonCopyablePayload", cs);
        Assert.DoesNotContain("NonCopyablePin", cs);
    }

    /// <summary>
    /// A generic argument's Swift buffer is a stack span the emitter fills through
    /// <c>MarshalToSwift</c>. The pointer to it is what the <c>finally</c> reads to decide whether
    /// to run the value witness's Destroy, so publishing the pointer before the span holds a value
    /// makes an uninitialized buffer indistinguishable from an initialized one: a
    /// <c>MarshalToSwift</c> that throws (a <c>~Copyable</c> value used twice raises
    /// <c>ObjectDisposedException</c> from its own consumed-state guard) then unwinds into a Destroy
    /// over undefined stack bytes. Marshalling first makes a non-null pointer mean "live value".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericArgument_PublishesItsBufferPointerOnlyAfterTheValueIsInIt(bool opened)
    {
        var (cs, _, _) = EmitGeneric(ParameterOwnership.Shared, opened);
        int marshal = cs.IndexOf("SwiftMarshal.MarshalToSwift(value, ref valuePayloadSpan);", StringComparison.Ordinal);
        int publish = cs.IndexOf("valuePayload = (IntPtr)Unsafe.AsPointer", StringComparison.Ordinal);
        Assert.True(marshal >= 0 && publish > marshal, cs);
        Assert.Contains("if (valuePayload != IntPtr.Zero)", cs);
    }

    /// <summary>
    /// A borrowed generic argument's buffer is the caller's to destroy: Swift reads it
    /// <c>@in_guaranteed</c> and destroys nothing, so exactly one Destroy has to run here. This is
    /// the arm a <c>~Copyable</c> value reaches through <c>borrowing T</c>, whose value moves out of
    /// C# into the buffer — one deinit, at the end of the call.
    /// </summary>
    [Fact]
    public void BorrowedGenericArgument_StillDestroysItsBuffer()
    {
        var (cs, _, _) = EmitGeneric(ParameterOwnership.Shared, opened: false);
        Assert.Contains("ValueWitnessTable->Destroy((void *)valuePayload", cs);
    }

    /// <summary>
    /// A consumed generic argument's buffer is handed to Swift <c>@in</c>: the callee destroys it,
    /// which for a <c>~Copyable</c> value means its <c>deinit</c> has already run. Destroying it
    /// again here runs a second deinit over storage Swift has released.
    /// </summary>
    [Fact]
    public void ConsumedGenericArgument_LeavesTheBufferToTheCallee()
    {
        var (cs, _, _) = EmitGeneric(ParameterOwnership.Owned, opened: false);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value, ref valuePayloadSpan);", cs);
        Assert.DoesNotContain("ValueWitnessTable->Destroy((void *)valuePayload", cs);
    }

    /// <summary>
    /// The opening wrapper reads the payload back with <c>.pointee</c>, which is a copy — the
    /// buffer the caller filled is still initialised when the wrapper returns, whatever ownership
    /// the Swift declaration gave the parameter. So the <c>@in</c> hand-off above does not apply
    /// on this route: the one Destroy that has to run is the caller's, for both ownerships. Losing
    /// it on the consuming arm would leak every value passed through an opened generic slot.
    /// </summary>
    [Theory]
    [InlineData(ParameterOwnership.Shared)]
    [InlineData(ParameterOwnership.Owned)]
    public void OpenedGenericArgument_DestroysItsOwnBufferForEitherOwnership(ParameterOwnership ownership)
    {
        var (cs, swift, _) = EmitGeneric(ownership, opened: true);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value, ref valuePayloadSpan);", cs);
        Assert.Contains("ValueWitnessTable->Destroy((void *)valuePayload", cs);
        // The wrapper copies out of the buffer rather than moving out of it — the read that makes
        // the caller-side Destroy the correct and only one.
        Assert.Contains(".pointee", swift);
        Assert.DoesNotContain(".move()", swift);
    }

    /// <param name="opened">
    /// <c>true</c> builds the shape the method-level-generic opening wrapper admits, so the member
    /// routes through a <c>@_cdecl</c> wrapper. <c>false</c> constrains the parameter on a marker
    /// protocol, which leaves no runtime conformance record to cast metadata against — the member
    /// is declined by the opening analysis and keeps the direct <c>CallConvSwift</c> P/Invoke,
    /// where Swift's own <c>@in</c>/<c>@in_guaranteed</c> conventions decide who destroys.
    /// </param>
    private static (string cs, string swift, MethodDecl method) EmitGeneric(
        ParameterOwnership ownership, bool opened = true)
    {
        var database = new TypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
        var module = new ModuleDecl
        {
            Name = "TestModule", Properties = new(), Methods = new(), Types = new(),
            Dependencies = new(), Protocols = new(), ParentDecl = null, ModuleDecl = null
        };
        database.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        var method = new MethodDecl
        {
            Name = "discard", MangledName = "$s10TestModule7discardyyxnlF",
            MethodType = MethodType.Static, IsConstructor = false, Throws = false,
            IsAsync = false, IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            RawGenericSig = opened ? null : "<T where T : Swift.Sendable>",
            ParentDecl = module, ModuleDecl = module,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", SwiftTypeSpec = TupleTypeSpec.Empty,
                    ParentDecl = null, ModuleDecl = module, IsInOut = false, IsGeneric = false },
                new() { Name = "value", PrivateName = "value", SwiftTypeSpec = new NamedTypeSpec("T"),
                    ParentDecl = null, ModuleDecl = module, IsInOut = false, IsGeneric = true, Ownership = ownership }
            }
        };
        module.Methods.Add(method);
        var cs = new StringWriter();
        var swift = new StringWriter();
        var handler = new MethodHandler(NullLogger<MethodHandler>.Instance);
        handler.Emit(new CSharpWriter(cs), new SwiftWriter(swift), new MethodEnvironment(method, database),
            new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return (cs.ToString(), swift.ToString(), method);
    }

    private static (string cs, string swift, MethodDecl method) Emit(ParameterOwnership ownership, bool throws, bool noncopyable)
    {
        var database = new TypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
        var module = new ModuleDecl
        {
            Name = "TestModule", Properties = new(), Methods = new(), Types = new(),
            Dependencies = new(), Protocols = new(), ParentDecl = null, ModuleDecl = null
        };
        var moduleDatabase = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var name = SwiftTypeName.FromModuleQualifiedName("TestModule.Resource");
        moduleDatabase.RegisterType(name, new TypeRecord
        {
            SwiftTypeName = name,
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Resource"),
            MetadataAccessor = "$s10TestModule8ResourceVMa", Kind = TypeRecordKind.Struct,
            Flags = noncopyable ? TypeRecordFlags.NonCopyable : TypeRecordFlags.None
        });
        database.AddModuleDatabase(moduleDatabase);
        var method = new MethodDecl
        {
            Name = "useResource", MangledName = "$s10TestModule11useResourceyyAA0D0VnF",
            MethodType = MethodType.Static, IsConstructor = false, Throws = throws,
            IsAsync = false, IsSynthesizedAccessor = false, GenericParameters = new(),
            ParentDecl = module, ModuleDecl = module,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", SwiftTypeSpec = TupleTypeSpec.Empty,
                    ParentDecl = null, ModuleDecl = module, IsInOut = false, IsGeneric = false },
                new() { Name = "resource", PrivateName = "resource", SwiftTypeSpec = new NamedTypeSpec("TestModule.Resource"),
                    ParentDecl = null, ModuleDecl = module, IsInOut = false, IsGeneric = false, Ownership = ownership }
            }
        };
        module.Methods.Add(method);
        var cs = new StringWriter();
        var swift = new StringWriter();
        var handler = new MethodHandler(NullLogger<MethodHandler>.Instance);
        handler.Emit(new CSharpWriter(cs), new SwiftWriter(swift), new MethodEnvironment(method, database),
            new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return (cs.ToString(), swift.ToString(), method);
    }
}
