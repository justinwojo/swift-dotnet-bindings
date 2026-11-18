// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// How a reverse callback on the Swift lane (<c>CallConvSwift</c>, called by Swift as a thick closure)
/// hands its result back. Swift owns what a closure returns: a reference comes back in the return
/// register at +1, an address-only value is written into the buffer Swift passes in its
/// indirect-result register, and a loadable value is exploded across return registers. The
/// <c>@_cdecl</c> lane is a different contract — its Swift adapter takes a returned reference
/// unretained — so only the Swift lane retains.
/// </summary>
public class SwiftLaneCallbackResultTests
{
    private static readonly NamedTypeSpec Node = new("TestModule.Node");
    private static readonly NamedTypeSpec Wide = new("TestModule.Wide");
    private static readonly NamedTypeSpec Point = new("TestModule.Point");
    private static readonly NamedTypeSpec IntType = new("Swift.Int");

    [Fact]
    public void SwiftLane_ThrowingCallback_ClassResult_IsReturnedOwned()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, Node) { Throws = true };
        var handler = new ClosureHandler(CreateTypeDatabase());
        var writer = new StringWriter();
        ClosureEmitter.EmitThrowingClosureCallback(new CSharpWriter(writer), "run", "body", closure, handler, "$s10TestModule3runyyF", "TestModule", useCdecl: false);
        var callback = writer.ToString();

        Assert.Contains("private static unsafe void* run_body_", callback);
        Assert.Contains("Swift.Runtime.Arc.Retain(", callback);
        Assert.DoesNotContain("SwiftIndirectResult", callback);
    }

    [Fact]
    public void SwiftLane_EscapingCallback_ClassResult_IsReturnedOwnedInTheReturnRegister()
    {
        var callback = EmitEscaping(new ClosureTypeSpec(TupleTypeSpec.Empty, Node), useCdecl: false);

        Assert.Contains("private static unsafe void* run_body_", callback);
        Assert.Contains("Swift.Runtime.Arc.Retain(", callback);
        Assert.DoesNotContain("SwiftIndirectResult", callback);
    }

    [Fact]
    public void SwiftLane_EscapingCallback_OptionalClassResult_IsReturnedOwned()
    {
        var callback = EmitEscaping(new ClosureTypeSpec(TupleTypeSpec.Empty, Optional(Node)), useCdecl: false);

        Assert.Contains("Swift.Runtime.Arc.Retain(", callback);
    }

    [Fact]
    public void CdeclLane_ClassResult_StaysBorrowed()
    {
        // The @_cdecl adapter reads the pointer with takeUnretainedValue, so the callback must not retain.
        var callback = EmitEscaping(new ClosureTypeSpec(TupleTypeSpec.Empty, Node), useCdecl: true);

        Assert.DoesNotContain("Arc.Retain(", callback);
    }

    [Fact]
    public void SwiftLane_OnlyAddressOnlyResults_TakeTheIndirectResultCallback()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(ClosureEmitter.UsesIndirectReturnCallback(new ClosureTypeSpec(TupleTypeSpec.Empty, Node), handler, useCdecl: false));
        Assert.True(ClosureEmitter.UsesIndirectReturnCallback(new ClosureTypeSpec(TupleTypeSpec.Empty, Wide), handler, useCdecl: false));
        Assert.True(ClosureEmitter.UsesIndirectReturnCallback(new ClosureTypeSpec(TupleTypeSpec.Empty, Optional(Wide)), handler, useCdecl: false));
        // The @_cdecl lane keeps its adapter's result-buffer contract for every non-primitive result.
        Assert.True(ClosureEmitter.UsesIndirectReturnCallback(new ClosureTypeSpec(TupleTypeSpec.Empty, Node), handler, useCdecl: true));
    }

    [Fact]
    public void SwiftLane_LoadableValueResults_AreUnmodelled()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        // Exploded across return registers: neither a pointer nor a buffer models them.
        Assert.True(ClosureEmitter.IsUnmodelledDirectLaneReturn(Point, handler, out _));
        Assert.True(ClosureEmitter.IsUnmodelledDirectLaneReturn(Optional(IntType), handler, out _));

        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(TupleTypeSpec.Empty, handler, out _));
        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(IntType, handler, out _));
        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(Node, handler, out _));
        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(Optional(Node), handler, out _));
        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(Wide, handler, out _));
        Assert.False(ClosureEmitter.IsUnmodelledDirectLaneReturn(Optional(Wide), handler, out _));
    }

    private static NamedTypeSpec Optional(TypeSpec inner)
    {
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(inner);
        return optional;
    }

    private static string EmitEscaping(ClosureTypeSpec closure, bool useCdecl)
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var writer = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(new CSharpWriter(writer), "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
        return writer.ToString();
    }

    private static TypeDatabase CreateTypeDatabase()
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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        Register(testModule, "Node", "$s10TestModule4NodeCMa", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        Register(testModule, "Wide", "$s10TestModule4WideVMa", TypeRecordFlags.None, TypeRecordKind.Struct);
        Register(testModule, "Point", "$s10TestModule5PointVMa", TypeRecordFlags.Frozen, TypeRecordKind.Struct);
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static void Register(ModuleTypeDatabase module, string name, string accessor, TypeRecordFlags flags, TypeRecordKind kind)
        => module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = accessor,
                Flags = flags,
                Kind = kind,
            });
}
