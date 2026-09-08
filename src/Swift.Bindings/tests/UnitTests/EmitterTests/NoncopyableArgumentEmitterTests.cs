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
