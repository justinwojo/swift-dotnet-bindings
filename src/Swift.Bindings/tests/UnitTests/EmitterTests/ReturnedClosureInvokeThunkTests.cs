// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The invocation contract for a Swift closure C# receives as a value: it is called only through
/// its @_cdecl invoke thunk, which reloads the closure at the abstraction the wrapper stored it at.
/// These tests pin which shapes the thunk carries (existential arguments by address, existential
/// and tuple results through out-pointers) and that every other shape is refused with a reason.
/// </summary>
public class ReturnedClosureInvokeThunkTests
{
    #region Refusals

    [Fact]
    public void Refusal_AsyncClosure_NamesAsync()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int")) { IsAsync = true };

        var refusal = ClosureEmitter.GetInvokeThunkRefusal(closure, handler);

        Assert.NotNull(refusal);
        Assert.Contains("async", refusal);
    }

    [Fact]
    public void Refusal_InOutArgument_NamesTheArgument()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var inoutInt = new NamedTypeSpec("Swift.Int") { IsInOut = true };
        var closure = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int"), inoutInt }),
            TupleTypeSpec.Empty);

        var refusal = ClosureEmitter.GetInvokeThunkRefusal(closure, handler);

        Assert.NotNull(refusal);
        Assert.Contains("argument 1", refusal);
        Assert.Contains("inout", refusal);
    }

    [Fact]
    public void Refusal_ClosureArgument_IsRefused()
    {
        // A closure that itself takes a closure: the thunk has no marshalling for a callback
        // argument, so C# cannot call it.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var inner = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var closure = new ClosureTypeSpec(inner, TupleTypeSpec.Empty);

        var refusal = ClosureEmitter.GetInvokeThunkRefusal(closure, handler);

        Assert.NotNull(refusal);
        Assert.Contains("argument 0", refusal);
    }

    [Fact]
    public void Refusal_ConventionCArgument_IsRefused()
    {
        // A C function pointer nested in a returned closure's parameter list has no thunk
        // marshalling either; it is refused rather than passed through a guessed convention.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var cfn = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        cfn.Attributes.Add(new TypeSpecAttribute("convention") { Parameters = { "c" } });
        var closure = new ClosureTypeSpec(cfn, TupleTypeSpec.Empty);

        Assert.NotNull(ClosureEmitter.GetInvokeThunkRefusal(closure, handler));
    }

    [Theory]
    [InlineData("Swift.String")]
    [InlineData("TestModule.Point")]
    public void Refusal_NonScalarResult_NamesTheResult(string resultType)
    {
        // String and struct results would need an indirect result buffer the thunk does not
        // build yet; they are refused, not returned through a guessed register.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec(resultType));

        var refusal = ClosureEmitter.GetInvokeThunkRefusal(closure, handler);

        Assert.NotNull(refusal);
        Assert.Contains("result", refusal);
    }

    [Fact]
    public void Refusal_OptionalResult_IsRefused()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int")));

        Assert.NotNull(ClosureEmitter.GetInvokeThunkRefusal(closure, handler));
    }

    [Fact]
    public void Refusal_ReturnedClosureResult_IsRefused()
    {
        // A closure returning a closure would hand C# another closure value with no thunk of its own.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var inner = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"));
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, inner);

        Assert.NotNull(ClosureEmitter.GetInvokeThunkRefusal(closure, handler));
    }

    [Fact]
    public void Refusal_SingleElementOrExistentialTupleResult_IsRefused()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var withString = new ClosureTypeSpec(TupleTypeSpec.Empty,
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.String") }));

        Assert.NotNull(ClosureEmitter.GetInvokeThunkRefusal(withString, handler));
    }

    [Fact]
    public void Admitted_ExistentialArgumentAndResult()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Shape(), Shape());

        Assert.Null(ClosureEmitter.GetInvokeThunkRefusal(closure, handler));
        Assert.Null(ClosureEmitter.GetInvokeThunkRefusal(
            new ClosureTypeSpec(ObjectShape(), ObjectShape()), handler));
        Assert.Null(ClosureEmitter.GetInvokeThunkRefusal(
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Error()), handler));
    }

    [Fact]
    public void Admitted_TupleOfScalarsResult()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, ScalarTuple());

        Assert.Null(ClosureEmitter.GetInvokeThunkRefusal(closure, handler));
    }

    [Fact]
    public void AddressedValueShapes_AreFlaggedForTheConformanceLane()
    {
        // The protocol-conformance lane builds the same thunk for closures handed to a C#
        // implementation, but does not take these by-address shapes; it reads this predicate.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(ClosureEmitter.UsesInvokeThunkAddressedValues(new ClosureTypeSpec(Shape(), new NamedTypeSpec("Swift.Int32")), handler));
        Assert.True(ClosureEmitter.UsesInvokeThunkAddressedValues(new ClosureTypeSpec(TupleTypeSpec.Empty, Shape()), handler));
        Assert.True(ClosureEmitter.UsesInvokeThunkAddressedValues(new ClosureTypeSpec(TupleTypeSpec.Empty, ScalarTuple()), handler));
        Assert.False(ClosureEmitter.UsesInvokeThunkAddressedValues(
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32")), handler));
    }

    #endregion

    #region Existential argument

    [Fact]
    public void ExistentialArgument_Swift_LoadsTheExistentialFromTheContainerAddress()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Shape(), new NamedTypeSpec("Swift.Int32"));

        var swift = EmitSwift(closure, handler);

        Assert.Contains("_ arg0: Swift.UnsafeRawPointer", swift);
        Assert.Contains("arg0.load(as: (any TestModule.Shape).self)", swift);
    }

    [Fact]
    public void ExistentialArgument_CSharp_BuildsAndDestroysTheContainerLikeAMethodParameter()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Shape(), new NamedTypeSpec("Swift.Int32"));

        var cs = EmitCSharp(closure, handler);

        // The P/Invoke takes the container's address.
        Assert.Contains("nint funcPtr, nint ctx, nint arg0)", cs);
        // A C#-authored conformer is wrapped in the proxy; a Swift-vended one hands over its container.
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<", cs);
        Assert.Contains("static __v => new ", cs);
        Assert.Contains("ShapeProxy(__v)", cs);
        // The heap copy is destroyed only when GetOrCreate boxed it, and the source stays
        // reachable until Swift has returned — both in the finally.
        var finallyIndex = cs.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIndex > 0);
        Assert.True(cs.IndexOf("DestroyAndFreeExistential(_arg0Heap, 1, _arg0Owns)", StringComparison.Ordinal) > finallyIndex);
        Assert.True(cs.IndexOf("GC.KeepAlive(_arg0KeepAlive)", StringComparison.Ordinal) > finallyIndex);
        Assert.Contains("(nint)_arg0Heap", cs);
        Assert.Contains("internal unsafe", cs);
    }

    [Fact]
    public void ErrorArgument_CSharp_HandsOverTheWrapperContainerWithoutOwningIt()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(Error(), new NamedTypeSpec("Swift.Int32"));

        var cs = EmitCSharp(closure, handler);

        Assert.Contains("ISwiftExistentialConvertible<global::Swift.Runtime.ExistentialContainer1>)_arg0).GetExistentialContainer()", cs);
        Assert.Contains("DestroyAndFreeExistential(_arg0Heap, 1, false)", cs);
        Assert.DoesNotContain("GetOrCreate", cs);
    }

    #endregion

    #region Existential result

    [Fact]
    public void ExistentialResult_Swift_InitializesTheInvokerOutPointer()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Shape());

        var swift = EmitSwift(closure, handler);

        Assert.Contains("_ _resultOut: Swift.UnsafeMutableRawPointer", swift);
        Assert.Contains("_resultOut.initializeMemory(as: (any TestModule.Shape).self, repeating: _result, count: 1)", swift);
        // The thunk returns Void: the result leaves through the out-pointer.
        Assert.DoesNotContain(") -> ", swift.Substring(swift.IndexOf("public func", StringComparison.Ordinal)).Split('\n')[0]);
    }

    [Fact]
    public void ExistentialResult_CSharp_AdoptsTheContainerThroughTheProxy()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Shape());

        var cs = EmitCSharp(closure, handler);

        Assert.Contains("private static extern void _InvokeClosureThunk_TEST0001(nint funcPtr, nint ctx, int arg0, nint _resultOut)", cs);
        Assert.Contains("global::Swift.Runtime.ExistentialContainer1 _ret = default;", cs);
        Assert.Contains("(nint)(&_ret)", cs);
        Assert.Contains("ShapeProxy(_ret, ownsContainer: true)", cs);
    }

    [Fact]
    public void ClassBoundExistentialResult_CSharp_WidensTheTwoWordCell()
    {
        // A class-bound existential is [object][witness table]; the proxy takes the widened container.
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, ObjectShape());

        var cs = EmitCSharp(closure, handler);

        Assert.Contains("ClassExistentialContainer1.ReadHeapCell((nint)(&_ret))", cs);
        Assert.Contains("ownsContainer: true", cs);
    }

    [Fact]
    public void ThrowingErrorResult_CSharp_WrapsTheAdoptedErrorInTheSuccessCase()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Error()) { Throws = true };

        var cs = EmitCSharp(closure, handler);
        var swift = EmitSwift(closure, handler);

        // The out-pointer precedes the error out-pointer on both sides.
        Assert.Contains("nint _resultOut, IntPtr* errorOut", cs);
        Assert.Contains("_ _resultOut: Swift.UnsafeMutableRawPointer, _ _errorOut:", swift);
        Assert.Contains("FromSuccess(new global::Swift.Foundation.AnyError(_ret", cs);
        // The failure check runs before the result is read.
        Assert.True(cs.IndexOf("if (_err != IntPtr.Zero)", StringComparison.Ordinal)
                    < cs.IndexOf("FromSuccess(", StringComparison.Ordinal));
    }

    [Fact]
    public void SuppressedProxyResult_IsReportedAndThrowsAtTheMemberCheckpoint()
    {
        // When the result's proxy is suppressed the invoker cannot adopt it. The non-throwing
        // probe is what the pre-checkpoint helper emission reads; the emitter itself throws the
        // exception the member-body checkpoint turns into a restubbed member.
        var typeDatabase = CreateTypeDatabase();
        var context = new ModuleEmissionContext();
        context.RecordSuppressedProxy("ShapeProxy");
        var handler = new ClosureHandler(typeDatabase) { EmissionContext = context };

        Assert.True(handler.IsProxyReferenceSuppressed(Shape()));
        Assert.Throws<SuppressedProxyReferenceException>(() => handler.ThrowIfProxyReferenceSuppressed(Shape()));
    }

    [Fact]
    public void SuppressedProxyResult_SwiftThunkIsLeftOutWithTheStubbedMember()
    {
        // The stubbed C# member never calls the thunk, so the wrapper must not export it either.
        var context = new ModuleEmissionContext();
        context.RecordSuppressedProxy("ShapeProxy");
        var handler = new ClosureHandler(CreateTypeDatabase()) { EmissionContext = context };
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Shape());

        Assert.True(ClosureEmitter.CanUseInvokeThunk(closure, handler));
        Assert.False(ClosureEmitter.ShouldEmitReturnedClosureInvokeThunk(closure, handler, context));
    }

    [Fact]
    public void SuppressedProxyResult_ExplicitContextReachesAHandlerThatHasNone()
    {
        // A property getter's wrapper is emitted from a freshly marshaled environment whose
        // handler was never given the module's context; the caller's context must still count.
        var context = new ModuleEmissionContext();
        context.RecordSuppressedProxy("ShapeProxy");
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Shape());

        Assert.False(handler.IsProxyReferenceSuppressed(Shape()));
        Assert.False(ClosureEmitter.ShouldEmitReturnedClosureInvokeThunk(closure, handler, context));
    }

    [Fact]
    public void LiveProxyResult_SwiftThunkIsEmitted()
    {
        var handler = new ClosureHandler(CreateTypeDatabase()) { EmissionContext = new ModuleEmissionContext() };
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), Shape());

        Assert.True(ClosureEmitter.ShouldEmitReturnedClosureInvokeThunk(closure, handler, handler.EmissionContext));
    }

    #endregion

    #region Tuple result

    [Fact]
    public void TupleResult_BothSides_ReassembleTheTupleFromScalarOutPointers()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), ScalarTuple());

        var swift = EmitSwift(closure, handler);
        var cs = EmitCSharp(closure, handler);

        Assert.Contains("_ _resultOut0: Swift.UnsafeMutableRawPointer, _ _resultOut1: Swift.UnsafeMutableRawPointer, _ _resultOut2: Swift.UnsafeMutableRawPointer", swift);
        Assert.Contains("_resultOut0.storeBytes(of: _result.0, as: Swift.Int32.self)", swift);
        Assert.Contains("_resultOut1.storeBytes(of: _result.1, as: Swift.Bool.self)", swift);
        Assert.Contains("_resultOut2.storeBytes(of: Swift.Int32(_result.2.rawValue), as: Swift.Int32.self)", swift);

        Assert.Contains("int _r0 = default;", cs);
        Assert.Contains("byte _r1 = default;", cs);
        Assert.Contains("int _r2 = default;", cs);
        Assert.Contains("(nint)(&_r0), (nint)(&_r1), (nint)(&_r2)", cs);
        Assert.Contains("_r1 != 0", cs);
        Assert.Contains("(TestModule.Status)_r2", cs);
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec Shape() => new("TestModule.Shape") { IsAny = true };
    private static NamedTypeSpec ObjectShape() => new("TestModule.ObjectShape") { IsAny = true };
    private static NamedTypeSpec Error() => new("Swift.Error") { IsAny = true };

    private static TupleTypeSpec ScalarTuple() => new(new List<TypeSpec>
    {
        new NamedTypeSpec("Swift.Int32"),
        new NamedTypeSpec("Swift.Bool"),
        new NamedTypeSpec("TestModule.Status"),
    });

    private static string EmitSwift(ClosureTypeSpec closure, ClosureHandler handler)
    {
        var output = new StringWriter();
        ClosureEmitter.EmitSwiftInvokeThunk(new SwiftWriter(output), closure, handler, "SBW_Test_InvCR", "_sbw_inv_test");
        return output.ToString();
    }

    private static string EmitCSharp(ClosureTypeSpec closure, ClosureHandler handler)
    {
        var output = new StringWriter();
        ClosureEmitter.EmitCSharpInvokeThunkHelper(new CSharpWriter(output), closure, handler,
            "_InvokeClosureThunk_TEST0001", "SBW_Test_InvCR", "TestLib");
        return output.ToString();
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        foreach (var (name, csType, accessor) in new[]
        {
            ("Swift.Int", CSharpTypeName.NIntType, "$sSiMa"),
            ("Swift.Int32", CSharpTypeName.FromNamespaceAndName("System", "Int32"), "$ss5Int32VMa"),
            ("Swift.Bool", CSharpTypeName.FromNamespaceAndName("System", "Boolean"), "$sSbMa"),
            ("Swift.String", CSharpTypeName.FromNamespaceAndName("System", "String"), "$sSSMa"),
        })
        {
            swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName(name), new TypeRecord
            {
                CSharpTypeName = csType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(name),
                MetadataAccessor = accessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        }
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Shape"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IShape"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Shape"),
            MetadataAccessor = "$s10TestModule5ShapeMp",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol,
        });
        testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.ObjectShape"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IObjectShape"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ObjectShape"),
            MetadataAccessor = "$s10TestModule11ObjectShapeMp",
            Flags = TypeRecordFlags.ClassBound,
            Kind = TypeRecordKind.Protocol,
        });
        testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Status"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Status"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
            MetadataAccessor = "$s10TestModule6StatusOMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = "Int32",
        });
        testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Point"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MetadataAccessor = "$s10TestModule5PointVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion
}
