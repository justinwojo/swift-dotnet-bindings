// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// An <c>Optional</c> whose payload projects to a value-wrapper CLASS (a resilient struct) crossing a
/// reverse callback. The managed spelling <c>Wide?</c> is only a nullable-reference annotation, so
/// anything keyed on it (metadata lookup, borrowed read, buffer write) silently treats the Optional as
/// its payload: a <c>nil</c> argument is read as a value, and a <c>null</c> result is never written as
/// Swift's empty case. Both directions have to go through <c>Optional&lt;Wide&gt;</c> itself.
/// </summary>
public class OptionalValueWrapperCallbackTests
{
    private static readonly NamedTypeSpec Wide = new("TestModule.Wide");
    private static readonly NamedTypeSpec OptionalWide = Optional(Wide);

    [Fact]
    public void SwiftLane_OptionalResilientStructArgument_IsReadThroughTheOptional()
    {
        // (Wide?) -> Void on the Swift lane: Swift passes the address of its Optional<Wide>.
        var callback = EmitEscaping(new ClosureTypeSpec(OptionalWide, TupleTypeSpec.Empty), useCdecl: false);

        Assert.Contains("SwiftMarshal.MarshalOptionalCallbackArg<TestModule.Wide>(new IntPtr(arg0))", callback);
        Assert.DoesNotContain("MarshalCallbackArg<TestModule.Wide?>", callback);
    }

    [Fact]
    public void SwiftLane_OptionalRuntimeStructArgument_IsNotTreatedAsAWrapperClass()
    {
        // Swift.AnyHashable is recorded as a resilient struct, but the runtime projects it as a C#
        // struct: its T? is Nullable<T>, which the class-constrained Optional reader cannot take.
        var callback = EmitEscaping(new ClosureTypeSpec(Optional(new NamedTypeSpec("Swift.AnyHashable")), TupleTypeSpec.Empty), useCdecl: false);

        Assert.DoesNotContain("MarshalOptionalCallbackArg<", callback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectReturn_OptionalResilientStruct_WritesTheWholeOptional(bool useCdecl)
    {
        // () -> Wide?: both lanes hand the callback a buffer sized and later moved as Optional<Wide>.
        var callback = EmitIndirect(new ClosureTypeSpec(TupleTypeSpec.Empty, OptionalWide), useCdecl);

        Assert.Contains("SwiftOptional<TestModule.Wide>.NewNone()", callback);
        Assert.Contains("SwiftOptional<TestModule.Wide>.NewSome(", callback);
        Assert.Contains("GetTypeMetadataOrThrow<SwiftOptional<TestModule.Wide>>()", callback);
        Assert.DoesNotContain("GetTypeMetadataOrThrow<TestModule.Wide?>", callback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingCallback_OptionalResilientStructResult_WritesTheWholeOptional(bool useCdecl)
    {
        // () throws -> Wide?: the result buffer is an Optional<Wide> on both lanes.
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, OptionalWide) { Throws = true };
        var handler = new ClosureHandler(CreateTypeDatabase());
        var writer = new StringWriter();
        ClosureEmitter.EmitThrowingClosureCallback(new CSharpWriter(writer), "run", "body", closure, handler, "$s10TestModule3runyyF", "TestModule", useCdecl);
        var callback = writer.ToString().Replace("global::", "", System.StringComparison.Ordinal);

        Assert.Contains("SwiftOptional<TestModule.Wide>.NewNone()", callback);
        Assert.DoesNotContain("GetTypeMetadataOrThrow<TestModule.Wide?>", callback);
    }

    [Fact]
    public void SwiftLane_ThrowingCallback_AddressOnlyResult_IsWrittenThroughTheIndirectResultRegister()
    {
        // Swift passes an address-only result's buffer in its indirect-result register and reads the
        // value from it; a callback returning a buffer of its own leaves Swift's uninitialized.
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, OptionalWide) { Throws = true };
        var handler = new ClosureHandler(CreateTypeDatabase());
        var callbackWriter = new StringWriter();
        var pointerWriter = new StringWriter();
        ClosureEmitter.EmitThrowingClosureCallback(new CSharpWriter(callbackWriter), "run", "body", closure, handler, "$s10TestModule3runyyF", "TestModule", useCdecl: false);
        ClosureEmitter.EmitThrowingClosureCallbackPointer(new CSharpWriter(pointerWriter), "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl: false);
        var callback = callbackWriter.ToString().Replace("global::System.Runtime.InteropServices.Swift.", "", System.StringComparison.Ordinal);
        var pointer = pointerWriter.ToString().Replace("global::System.Runtime.InteropServices.Swift.", "", System.StringComparison.Ordinal);

        Assert.Contains("private static unsafe void run_body_", callback);
        Assert.Contains("(SwiftError* errorOut, SwiftIndirectResult __indirectResult, SwiftSelf context)", callback);
        Assert.Contains("(void*)indirectResult", callback);
        Assert.DoesNotContain("NativeMemory.Alloc", callback);
        Assert.Contains("<SwiftError*, SwiftIndirectResult, SwiftSelf, void>", pointer);
    }

    [Fact]
    public void SwiftLane_ThrowingCallback_ScalarResult_StaysInTheReturnRegister()
    {
        var intType = new NamedTypeSpec("Swift.Int");
        var closure = new ClosureTypeSpec(intType, intType) { Throws = true };
        var handler = new ClosureHandler(CreateTypeDatabase());
        var writer = new StringWriter();
        ClosureEmitter.EmitThrowingClosureCallback(new CSharpWriter(writer), "run", "body", closure, handler, "$s10TestModule3runyyF", "TestModule", useCdecl: false);

        Assert.DoesNotContain("SwiftIndirectResult", writer.ToString());
        Assert.Contains("private static unsafe nint run_body_", writer.ToString());
    }

    [Fact]
    public void IndirectReturn_PlainResilientStruct_KeepsThePayloadWrite()
    {
        // The non-optional return is unaffected: the buffer is the struct itself.
        var callback = EmitIndirect(new ClosureTypeSpec(TupleTypeSpec.Empty, Wide), useCdecl: false);

        Assert.Contains("GetTypeMetadataOrThrow<TestModule.Wide>()", callback);
        Assert.DoesNotContain("SwiftOptional<", callback);
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
        // Compare type names without the global:: qualifier the handler adds only for some databases.
        return writer.ToString().Replace("global::", "", System.StringComparison.Ordinal);
    }

    private static string EmitIndirect(ClosureTypeSpec closure, bool useCdecl)
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var writer = new StringWriter();
        ClosureEmitter.EmitIndirectReturnCallback(new CSharpWriter(writer), "run", "body", closure, handler, "$s10TestModule3runyyF", useCdecl);
        // Compare type names without the global:: qualifier the handler adds only for some databases.
        return writer.ToString().Replace("global::", "", System.StringComparison.Ordinal);
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "AnyHashable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
                MetadataAccessor = "$ss11AnyHashableVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Wide"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Wide"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Wide"),
                MetadataAccessor = "$s10TestModule4WideVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }
}
