// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The admission rules for an <c>inout</c> parameter in a closure's OWN signature.
///
/// Swift hands such a parameter to the block by reference and reads back whatever the block left
/// in the cell, so a bridge that lowers the argument by value produces a member that compiles on
/// both sides and silently discards every mutation. The write-back exists on exactly one route —
/// the ordinary <c>@convention(c)</c> adapter — so two things have to hold: the carrier set the
/// admitting gate uses must be the same one the cdecl-compatibility gate uses (otherwise the
/// member falls to the by-value direct lane and the mutation is dropped), and every OTHER route
/// must refuse the shape outright.
/// </summary>
public class InOutClosureArgumentTests
{
    #region Carrier admission

    [Fact]
    public void IsSupportedClosure_InOutScalarArgument_IsAdmitted()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(handler.IsSupportedClosure(InOutClosure(Int32())));
    }

    [Fact]
    public void IsSupportedClosure_InOutStringArgument_IsAdmitted()
    {
        // String's C# projection is not the Swift value's own carrier, but the write path
        // converts through SwiftString explicitly, so it stays admitted.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(handler.IsSupportedClosure(InOutClosure(new NamedTypeSpec("Swift.String"))));
    }

    [Fact]
    public void IsSupportedClosure_InOutClassArgument_IsRefused()
    {
        // A reference cell is a slot Swift load/stores and releases the displaced object
        // through; the projection carries the object rather than the slot.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(handler.IsSupportedClosure(InOutClosure(new NamedTypeSpec("Test.Box"))));
    }

    [Fact]
    public void IsSupportedClosure_InOutOptionalArgument_IsRefused()
    {
        // The seeded value reaches the callback as a buffer address, which is never null, so
        // the pointer-shaped discriminator tests would all report a present value.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var optional = new NamedTypeSpec("Swift.Optional", new TypeSpec[] { Int32() });

        Assert.False(handler.IsSupportedClosure(InOutClosure(optional)));
    }

    [Fact]
    public void IsSupportedInOutClosureArgument_TupleCarrier_IsRefused()
    {
        // A tuple reaches the managed callback by value rather than through the address-based
        // read every other carrier shares. The assertion is on the carrier helper rather than on
        // a whole closure because a tuple in ARGUMENT position is flattened by EachArgument —
        // `(inout (Int32, Int32)) -> Void` and `(inout Int32, inout Int32) -> Void` are the same
        // walk — so only a nested tuple carrier reaches this arm.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var tuple = new TupleTypeSpec(new TypeSpec[] { Int32(), Int32() }) { IsInOut = true };

        Assert.False(handler.IsSupportedInOutClosureArgument(tuple));
    }

    [Fact]
    public void IsSupportedClosure_AsyncInOutArgument_IsRefused()
    {
        // The adapter writes the block's result back synchronously, on the same call.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = InOutClosure(Int32());
        closure.IsAsync = true;

        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_ThrowingInOutArgument_IsRefused()
    {
        // A throwing block has an error channel the store-back is not sequenced against.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = InOutClosure(Int32());
        closure.Throws = true;

        Assert.False(handler.IsSupportedClosure(closure));
    }

    #endregion

    #region Route agreement

    [Fact]
    public void IsSupportedClosure_InOutArgumentDisallowed_IsRefused()
    {
        // Every route without a write-back cell passes allowInOutArguments: false, and must get
        // a refusal even for a carrier the cdecl adapter would accept.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(handler.IsSupportedClosure(InOutClosure(Int32()), allowInOutArguments: false));
    }

    [Fact]
    public void IsSupportedClosure_NonInOutArgument_IgnoresTheInOutSwitch()
    {
        // The switch narrows the inout arm only; an ordinary closure is unaffected by it.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Int32(), TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closure));
        Assert.True(handler.IsSupportedClosure(closure, allowInOutArguments: false));
    }

    [Fact]
    public void IsCdeclCompatibleType_InOutArgument_AgreesWithTheAdmittingGate()
    {
        // These two gates are layered: the first decides whether the member emits, the second
        // whether the @_cdecl wrapper is generated. A member the first admits and the second
        // refuses falls to the by-value direct lane, which drops the mutation — so for every
        // carrier the two must return the same answer.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var carriers = new TypeSpec[]
        {
            Int32(),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Test.Box"),
            new NamedTypeSpec("Swift.Optional", new TypeSpec[] { Int32() }),
        };

        foreach (var carrier in carriers)
        {
            carrier.IsInOut = true;
            Assert.Equal(
                handler.IsSupportedInOutClosureArgument(carrier),
                ClosureEmitter.IsCdeclCompatibleType(carrier, handler));
        }
    }

    [Fact]
    public void IsSupportedInOutClosureArgument_NonInOutArgument_IsRefused()
    {
        // The helper answers a question about a cell; a by-value argument has none.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(handler.IsSupportedInOutClosureArgument(Int32()));
    }

    #endregion

    #region Nested detection

    [Fact]
    public void HasInOutArgument_TopLevelArgument_IsDetected()
    {
        Assert.True(ClosureHandler.HasInOutArgument(InOutClosure(Int32())));
    }

    [Fact]
    public void HasInOutArgument_NestedClosureArgument_IsDetected()
    {
        // A nested closure's own inout parameter is just as unbridged on the routes that ask
        // this question, so the walk has to descend rather than read only the top level.
        var nested = InOutClosure(Int32());
        var outer = new ClosureTypeSpec(nested, TupleTypeSpec.Empty);

        Assert.True(ClosureHandler.HasInOutArgument(outer));
    }

    [Fact]
    public void HasInOutArgument_NestedClosureReturn_IsDetected()
    {
        var nested = InOutClosure(Int32());
        var outer = new ClosureTypeSpec(TupleTypeSpec.Empty, nested);

        Assert.True(ClosureHandler.HasInOutArgument(outer));
    }

    [Fact]
    public void HasInOutArgument_NoInOutAnywhere_IsFalse()
    {
        var nested = new ClosureTypeSpec(Int32(), TupleTypeSpec.Empty);
        var outer = new ClosureTypeSpec(nested, nested);

        Assert.False(ClosureHandler.HasInOutArgument(outer));
    }

    #endregion

    #region Route restrictions

    [Fact]
    public void IsSupportedClosure_InOutArgumentWithIndirectReturn_IsRefused()
    {
        // The write-back is implemented on the direct-return escaping callback. A closure whose
        // RETURN needs indirect marshalling is emitted by a different trampoline that declares one
        // parameter per argument and no out-cell, while the Swift adapter would still pass two
        // pointers and move a cell that trampoline never initialized.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var argument = Int32();
        argument.IsInOut = true;
        var closure = new ClosureTypeSpec(argument, new NamedTypeSpec("Swift.String"));

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
        Assert.False(handler.IsSupportedClosure(closure));
    }

    [Fact]
    public void IsSupportedClosure_ByValueArgumentWithIndirectReturn_StaysAdmitted()
    {
        // The restriction above is scoped to the `inout` lowering, not to indirect return itself —
        // the by-value shape has no out-cell to go missing.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Int32(), new NamedTypeSpec("Swift.String"));

        Assert.True(handler.RequiresIndirectReturnMarshalling(closure));
        Assert.True(handler.IsSupportedClosure(closure));
    }

    [Theory]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.OpaquePointer")]
    public void IsSupportedInOutClosureArgument_PointerCarrier_IsRefused(string pointerName)
    {
        // A pointer projects to IntPtr, which carries no Swift type metadata, so the write-back has
        // nothing to size or initialize the out-cell from.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var pointer = new NamedTypeSpec(pointerName) { IsInOut = true };

        Assert.False(handler.IsSupportedInOutClosureArgument(pointer));
    }

    [Theory]
    [InlineData("Swift.UnsafeRawBufferPointer")]
    [InlineData("Swift.UnsafeMutableRawBufferPointer")]
    public void IsSupportedInOutClosureArgument_RawBufferCarrier_IsRefused(string bufferName)
    {
        // A raw buffer lowers as a (pointer, count) PAIR, and that split is classified ahead of
        // `inout` on both the Swift convention-C type and the managed parameter list — the
        // signatures would describe a length where the adapter passes an out-cell.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var buffer = new NamedTypeSpec(bufferName) { IsInOut = true };

        // The carrier is registered and is not a pointer type, so nothing downstream refuses it —
        // the dedicated raw-buffer arm is the only thing that can, which is what makes this
        // assertion a pin on that arm rather than on an unrelated lookup miss.
        Assert.True(MarshallingHelpers.IsAnyUnsafeRawBufferPointer(buffer));
        Assert.False(TypeDatabaseExtensions.IsPointerType(buffer));
        Assert.True(handler.IsSupportedClosure(
            new ClosureTypeSpec(new NamedTypeSpec(bufferName), TupleTypeSpec.Empty)));

        Assert.False(handler.IsSupportedInOutClosureArgument(buffer));
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec Int32() => new NamedTypeSpec("Swift.Int32");

    private static ClosureTypeSpec InOutClosure(TypeSpec argument)
    {
        argument.IsInOut = true;
        return new ClosureTypeSpec(argument, TupleTypeSpec.Empty);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("Test", "/tmp/libTest.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Test.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.Box"),
                MetadataAccessor = "$s4Test3BoxCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        foreach (var bufferName in new[]
                 {
                     "Swift.UnsafeRawBufferPointer", "Swift.UnsafeMutableRawBufferPointer"
                 })
        {
            swiftModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(bufferName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Span"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(bufferName),
                    MetadataAccessor = "$sSwMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                });
        }

        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion
}
