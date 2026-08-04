// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the NestedClosureBridge emitter — handles methods with closure-in-closure
/// parameters via a two-level bridge ABI (outer C# → Swift, inner Swift → C#).
/// </summary>
public class NestedClosureBridgeTests
{
    // ─── IsEligible ───────────────────────────────────────────────────

    [Fact]
    public void IsEligible_MethodWithNestedClosure_ReturnsTrue()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_RegularClosure_ReturnsFalse()
    {
        // A closure with no inner closure — not eligible for NCB
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    /// <summary>
    /// Adds a leading <c>Box&lt;…&gt;</c> parameter to the nested-closure fixture. A generic class
    /// is a payload-handle parameter, so the bridge passes it through and writes its type spec
    /// verbatim into the Swift wrapper signature.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndBoxParam(
        TypeSpec boxElement)
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var boxArg = CreateArgument("box", new NamedTypeSpec("TestModule.Box", boxElement), method.ModuleDecl!);
        method.CSSignature.Insert(1, boxArg);
        return (method, typeDatabase);
    }

    [Fact]
    public void IsEligible_ParamOverMethodOwnGeneric_ReturnsFalse()
    {
        // The wrapper is a @_cdecl free function; the method's own type parameter is not in its
        // scope, so a `Box<τ_0_0>` parameter renders into Swift as a type name that does not
        // resolve and fails the wrapper compile for the whole library.
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndBoxParam(new NamedTypeSpec("τ_0_0"));
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ParamOverConcreteGenericArgument_ReturnsTrue()
    {
        // Positive control: the same parameter shape with a concrete element stays eligible.
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndBoxParam(
            new NamedTypeSpec("Swift.Int"));
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsAsync = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.Throws = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ConstructorOnRootSwiftClass_ReturnsTrue()
    {
        // An initializer carrying a callback-bearing closure is the same ABI problem as the
        // equivalent method; on a plain root Swift class the bridge can construct it.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_FailableConstructor_ReturnsFalse()
    {
        // Optional<Self> cannot be carried by the wrapper's raw-pointer return.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        method.IsFailable = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ObjCRootedConstructor_ReturnsFalse()
    {
        // ObjC-rooted classes construct through a static native-handle helper feeding
        // `: base(handle)` — a shape the bridge does not emit.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        ((ClassDecl)method.ParentDecl!).IsObjCRooted = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_DerivedClassConstructor_ReturnsFalse()
    {
        // A derived class chains an inheritance token and stores into the ROOT base's handle
        // field; the bridge writes neither.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        ((ClassDecl)method.ParentDecl!).SuperclassNames.Add("TestModule.BaseRequest");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_GenericClassConstructor_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        ((ClassDecl)method.ParentDecl!).GenericParameters.Add(
            new GenericArgumentDecl("T", "T", new(), new()));
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MainActorConstructor_ReturnsFalse()
    {
        // The wrapper is a bare non-isolated free function, so an init that must be entered on
        // the main actor's executor has no correct call site here.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        method.IsActorIsolated = true;
        method.IsMainActorIsolated = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ConstructorWithDefaultedParam_ReturnsFalse()
    {
        // The bridge drops defaulted params; on an initializer the parameter list IS the
        // identity, so a reduced signature could collide with a sibling init.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
        var defaulted = CreateArgument("retries", new NamedTypeSpec("Swift.Int32"), method.ModuleDecl!);
        defaulted.HasDefaultArg = true;
        method.CSSignature.Add(defaulted);
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_StructConstructor_ReturnsFalse()
    {
        // A struct initializer constructs in place, not through a retained pointer.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;

        var moduleDecl = method.ModuleDecl!;
        method.ParentDecl = new StructDecl
        {
            Name = "DataRequestValue",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequestValue"),
            MangledName = "$s10TestModule16DataRequestValueVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule16DataRequestValueVMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultipleInnerClosures_ReturnsTrue()
    {
        // Outer closure with TWO inner closures — now supported
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure 1: (Int) -> Void
        var innerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        // Inner closure 2: (Bool) -> Void
        var innerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { innerClosure1, innerClosure2 }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_TwoOuterClosures_ReturnsTrue()
    {
        // Multi-outer nested-closure methods ARE eligible — the bridge emits a single Swift
        // wrapper covering all outer closures.
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // First outer closure: (NSObject, (ResponseDisposition) -> Void) -> Void
        var innerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        var outerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure1
            }),
            TupleTypeSpec.Empty);
        outerClosure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Second outer closure: (Int, (Bool) -> Void) -> Void
        var innerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);
        var outerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure2
            }),
            TupleTypeSpec.Empty);
        outerClosure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosure1, "handler", outerClosure2, "completion");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_Constructor_EmitsConstructorShapeWithNoSelf()
    {
        // A constructor builds the instance rather than receiving one: the @_cdecl wrapper takes
        // no self pointer and reconstructs nothing, the P/Invoke carries no SwiftSelf, and the C#
        // surface is a real constructor adopting the returned +1 reference into its own handle.
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Foundation.NSObject"), innerClosure }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("init", parentDecl, moduleDecl,
            new NamedTypeSpec("TestModule.DataRequest"), outerClosureType, "confirmHandler");
        method.IsConstructor = true;

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();

        Assert.True(NestedClosureBridge.TryEmit(
            new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env, parentDecl));

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // C#: a real constructor, not a named method returning a marshalled wrapper object.
        Assert.Contains("public unsafe DataRequest(", cs);
        Assert.Contains("_handle = new SwiftClassHandle<DataRequest>(__result);", cs);
        Assert.Contains("Swift.Runtime.SwiftDisposeScope.TryRegister(this);", cs);
        // Marshalling the returned pointer would build a SECOND wrapper object around the
        // instance this constructor is supposed to BE. (MarshalFromSwift still appears for the
        // closure's own arguments, so pin the receiver type rather than the bare call.)
        Assert.DoesNotContain("MarshalFromSwift<DataRequest", cs);
        Assert.DoesNotContain("SwiftSelf", cs);

        // Swift: constructs the type directly, with no self param or reconstruction.
        // (takeUnretainedValue still appears for the closure's own context box, so pin the self
        // reconstruction by its receiver type rather than the bare call.)
        Assert.Contains("TestModule.DataRequest(", swift);
        Assert.DoesNotContain("self_", swift);
        Assert.DoesNotContain("__self", swift);
        Assert.DoesNotContain("Unmanaged<TestModule.DataRequest>.fromOpaque", swift);
    }

    [Fact]
    public void TryEmit_ClosureNotLastParam_EmitsArgumentsInDeclarationOrder()
    {
        // Swift argument order follows the declaration. The single-outer layout emits every
        // non-closure argument first, so a closure declared BEFORE another parameter must fall to
        // the declaration-order emitter or the wrapper call fails to compile.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var trailing = CreateArgument("value", new NamedTypeSpec("Swift.Int32"), method.ModuleDecl!);
        method.CSSignature.Add(trailing);

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();

        Assert.True(NestedClosureBridge.TryEmit(
            new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env, method.ParentDecl as TypeDecl));

        var swift = swiftOutput.ToString();
        var callIndex = swift.IndexOf(".onHTTPResponse(", System.StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "expected the wrapper to call the bridged method");
        var call = swift.Substring(callIndex);
        Assert.True(
            call.IndexOf("_perform:", System.StringComparison.Ordinal) <
            call.IndexOf("value:", System.StringComparison.Ordinal),
            "closure argument must precede the parameter declared after it");
    }

    [Fact]
    public void TryEmit_TwoInnerClosures_EmitsIndexedNames()
    {
        // Two inner closures in one outer closure — should get indexed names
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        var innerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var innerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { innerClosure1, innerClosure2 }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();
        // Indexed inner names: innerFuncPtr0/innerContext0, innerFuncPtr1/innerContext1
        Assert.Contains("innerFuncPtr0", cs);
        Assert.Contains("innerContext0", cs);
        Assert.Contains("innerFuncPtr1", cs);
        Assert.Contains("innerContext1", cs);
        // Swift should have indexed trampolines and boxes
        Assert.Contains("__innerBox0", swift);
        Assert.Contains("__innerBox1", swift);
        Assert.Contains("__closureBox0", swift);
        Assert.Contains("__closureBox1", swift);
    }

    [Theory]
    [InlineData(null, "OnEvent(")]
    [InlineData("onEventWithHandler", "OnEventWithHandler(")]
    [InlineData("onEventWithObserver", "OnEventWithObserver(")]
    public void TryEmit_AppliesDisambiguatedNameToPublicMethodName(string? disambiguatedNameInput, string expectedSignaturePrefix)
    {
        // Mirror of MethodClosureBridgeTests — when IHandler.HandleBaseDecl hands a
        // label-derived name input to one of two Swift overloads that project to the same
        // C# parameter list, the nested-closure-bridge path must read env.CSharpMethodName
        // (which applies that input), not recompute the bare name.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { innerClosure }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        var env = new MethodEnvironment(method, typeDatabase) { DisambiguatedNameInput = disambiguatedNameInput };
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var result = NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        Assert.Contains(expectedSignaturePrefix, cs);
    }

    [Fact]
    public void TryEmit_TwoOuterClosures_EmitsSingleWrapperWithOuterIndexedNames()
    {
        // Multi-outer support: a single Swift wrapper should cover both outer closures,
        // with outer-index-namespaced inner trampolines/boxes so the two outers don't collide.
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        var innerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        var outerClosure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure1
            }),
            TupleTypeSpec.Empty);
        outerClosure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var innerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);
        var outerClosure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure2
            }),
            TupleTypeSpec.Empty);
        outerClosure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosure1, "handler", outerClosure2, "completion");

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(result);

        var swift = swiftOutput.ToString();
        // Exactly one wrapper covers both outers (not one per outer).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(swift, @"@_cdecl\(""SBW_NCB_"));
        // Outer-index-namespaced trampoline names prevent collisions between outer 0 and outer 1.
        Assert.Contains("innerTrampoline_0_0", swift);
        Assert.Contains("innerTrampoline_1_0", swift);
    }

    [Fact]
    public void IsEligible_InnerClosureWithPrimitiveReturn_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: (Int) -> Bool (primitive return — allowed)
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            new NamedTypeSpec("Swift.Bool"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_InnerClosureWithIntReturn_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: () -> Int (primitive return)
        var innerClosure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_InnerClosureWithEnumReturn_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: (Int) -> ResponseDisposition (enum return — not allowed)
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            new NamedTypeSpec("TestModule.ResponseDisposition"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_InnerClosureWithClassReturn_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: () -> DataRequest (class return — not allowed)
        var innerClosure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("TestModule.DataRequest"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_InnerClosureWithBoolReturn_EmitsFuncDelegate()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (Int) -> Bool
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            new NamedTypeSpec("Swift.Bool"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();
        // C# should use Func<...> instead of Action<...> for the inner delegate
        Assert.Contains("Func<", cs);
        // Swift inner trampoline should return non-void
        Assert.Contains("-> UInt8", swift);
    }

    [Fact]
    public void TryEmit_OuterCallback_FailsFastOnManagedException()
    {
        // The outer-closure UCO callback invokes the managed delegate. A non-throwing nested
        // closure has no error channel, so a managed exception escaping the delegate must route
        // to the fail-fast contract — never unwind into Swift (SIGABRT) and never be swallowed
        // by a bare `catch { }`.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Outer (Int, (Int) -> Void) -> Void — outer callback returns void (the hardened path).
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int"), innerClosure }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var cs = csOutput.ToString();
        Assert.Contains("catch (global::System.Exception", cs);
        Assert.Contains("FailFastUnhandledClosureException", cs);
        Assert.DoesNotContain("catch { }", cs);
    }

    [Fact]
    public void IsEligible_InnerClosureWithNonCdeclArg_ReturnsFalse()
    {
        // Inner closure with String arg — String is not cdecl-compatible
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Inner closure: (Swift.String) -> Void — String needs marshalling
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncOuterClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        outerClosureType.IsAsync = true;

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncInnerClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        innerClosure.IsAsync = true;

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingInnerClosure_ReturnsFalse()
    {
        // The inner trampoline force-casts the boxed Swift closure to a non-throwing
        // function type, so a throwing inner closure would compile and then trap when the
        // callback fires. The bridge must refuse the shape rather than emit the trap.
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        innerClosure.Throws = true;

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_OptionalRefOuterArg_ReturnsTrue()
    {
        // Optional<NSObject> as outer arg — nil-pointer ABI is now supported
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        var optionalNSObject = new NamedTypeSpec("Swift.Optional",
            new[] { new NamedTypeSpec("Foundation.NSObject") });

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                optionalNSObject,
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_OptionalRefInnerArg_ReturnsTrue()
    {
        // Inner closure with Optional<NSObject> arg — nil-pointer ABI is now supported
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var optionalNSObject = new NamedTypeSpec("Swift.Optional",
            new[] { new NamedTypeSpec("Foundation.NSObject") });

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { optionalNSObject }),
            TupleTypeSpec.Empty);

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_OptionalRefOuterArg_EmitsNilCheckPattern()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        var optionalNSObject = new NamedTypeSpec("Swift.Optional",
            new[] { new NamedTypeSpec("Foundation.NSObject") });

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                optionalNSObject,
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();
        // C# should have null-check for Optional<ref> marshalling
        Assert.Contains("IntPtr.Zero", cs);
        // Swift should use nullable pointer type
        Assert.Contains("UnsafeMutableRawPointer?", swift);
    }

    // ─── TryEmit: Swift Wrapper ───────────────────────────────────────

    [Fact]
    public void TryEmit_EmitsSwiftSilgenName()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        Assert.Contains("@_cdecl(\"SBW_NCB_", swift);
    }

    [Fact]
    public void TryEmit_EmitsInnerConventionCTrampoline()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("@convention(c)", swift);
        Assert.Contains("innerTrampoline", swift);
        // Verify trampoline uses parameter names (__ip0, __closureBox), not type names
        Assert.Contains("__ip0, __closureBox in", swift);
    }

    [Fact]
    public void TryEmit_EmitsUnmanagedPassRetainedBoxing()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("Unmanaged.passRetained", swift);
        Assert.Contains("as AnyObject", swift);
    }

    [Fact]
    public void TryEmit_InnerTrampolineUsesUnretainedValue()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        // The escaping-closure owner-token preamble is module-shared: a real module emits it once
        // ahead of every per-method bridge, and it consumes a takeRetainedValue for a reason that
        // has nothing to do with the trampoline. Emit it elsewhere against this context so this
        // writer holds only the per-method output the assertions below are about.
        var ctx = new ModuleEmissionContext();
        ClosureContextHelperEmitter.EmitIfNeeded(new SwiftWriter(new StringWriter()), ctx);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl, ctx);

        var swift = swiftOutput.ToString();
        // The trampoline borrows the box (takeUnretainedValue) so the inner closure can be
        // called multiple times during the outer invocation. The adapter's passRetained(+1)
        // keeps the box alive for that window and is balanced by an explicit release after
        // cdecl() returns for non-escaping inner closures (see TryEmit_ReleasesNonEscapingInnerBox).
        Assert.Contains("takeUnretainedValue", swift);
        Assert.DoesNotContain("takeRetainedValue", swift);
    }

    [Fact]
    public void TryEmit_ReleasesNonEscapingInnerBox()
    {
        // The inner closure in CreateMethodWithNestedClosure is non-escaping, so the +1 box
        // retain minted in the outer adapter must be balanced by a release after cdecl() returns
        // Without this the binding leaks one AnyObject box per outer-closure invocation.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(__innerBox", swift);
        Assert.Contains(".release()", swift);
    }

    [Fact]
    public void TryEmit_DoesNotReleaseEscapingInnerBox()
    {
        // An @escaping inner closure may be invoked after the outer call returns, so releasing
        // its box on the synchronous Swift path would be a use-after-free. The Swift side never
        // releases __innerBox for escaping inners — the +1 ownership transfers to the C# side,
        // whose generated box owner releases it when the managed delegate is collected (see
        // TryEmit_EscapingInner_CallbackAdoptsBoxViaFinalizableOwner).
        var (method, typeDatabase) = CreateMethodWithEscapingInnerClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Box is still minted (passRetained), but never synchronously released on this path.
        Assert.Contains("Unmanaged.passRetained", swift);
        Assert.DoesNotContain("Unmanaged<AnyObject>.fromOpaque(__innerBox", swift);
    }

    [Fact]
    public void TryEmit_EscapingInner_CallbackAdoptsBoxViaFinalizableOwner()
    {
        // The Swift adapter mints a +1 AnyObject box (passRetained) per escaping inner closure
        // and cannot release it on the synchronous path. The C# callback is the last owner that
        // knows the inner delegate's lifetime, so it must adopt the +1: a finalizable owner
        // object captured by the inner delegate releases the box (via the wrapper's own
        // release symbol, single Cdecl boundary — finalizer-thread safe) once the delegate
        // becomes unreachable. Without this the box leaks per outer-closure invocation.
        var (method, typeDatabase) = CreateMethodWithEscapingInnerClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        // Fresh per-test context: the release helper is emitted once per module context, so
        // sharing the static Default with other escaping-inner tests would swallow it here.
        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl, new ModuleEmissionContext());

        var cs = csOutput.ToString();
        // Owner adopts the box pointer and is kept alive across every native invocation.
        Assert.Contains("__innerBoxOwner", cs);
        Assert.Contains("GC.KeepAlive(__innerBoxOwner", cs);
        // The release routes through the wrapper's per-module @_cdecl release helper.
        Assert.Contains("SBW_NCB_ReleaseInnerBox_TestModule", cs);

        var swift = swiftOutput.ToString();
        Assert.Contains("@_cdecl(\"SBW_NCB_ReleaseInnerBox_TestModule\")", swift);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(box).release()", swift);
    }

    [Fact]
    public void TryEmit_NonEscapingInner_CallbackDoesNotAdoptBox()
    {
        // Non-escaping inner closures are balanced Swift-side (release after cdecl returns).
        // A C# owner here would double-release the box.
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        Assert.DoesNotContain("__innerBoxOwner", cs);
        Assert.DoesNotContain("SBW_NCB_ReleaseInnerBox", cs);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("SBW_NCB_ReleaseInnerBox", swift);
    }

    [Fact]
    public void TryEmit_EscapingInner_SwiftReleaseHelperEmittedOncePerModule()
    {
        // Two NCB methods sharing one ModuleEmissionContext must not redeclare the
        // per-module release helper (duplicate @_cdecl symbol = Swift compile error).
        var (method1, typeDatabase) = CreateMethodWithEscapingInnerClosure();
        var (method2, _) = CreateMethodWithEscapingInnerClosure();
        var ctx = new ModuleEmissionContext();
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var env1 = new MethodEnvironment(method1, typeDatabase);
        NestedClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env1, env1.ParentDecl as TypeDecl, ctx);
        var env2 = new MethodEnvironment(method2, typeDatabase);
        NestedClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env2, env2.ParentDecl as TypeDecl, ctx);

        var swift = swiftOutput.ToString();
        var occurrences = swift.Split("@_cdecl(\"SBW_NCB_ReleaseInnerBox_TestModule\")").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void TryEmit_EscapingInner_DistinctParentModules_EachGetReleaseHelper()
    {
        // The release symbol is namespaced by the PARENT decl's module, so two cross-module
        // extension methods with parents in different foreign modules need TWO helpers in one
        // emission context. A coarser once-per-context gate leaves the second module's C#
        // DllImport pointing at a symbol the wrapper never defines — the finalizer's catch
        // swallows the EntryPointNotFoundException and the box silently leaks.
        var (methodA, typeDatabase) = CreateMethodWithEscapingInnerClosure("ModuleA");
        var (methodB, _) = CreateMethodWithEscapingInnerClosure("ModuleB");
        var ctx = new ModuleEmissionContext();
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var envA = new MethodEnvironment(methodA, typeDatabase);
        var csA = new StringWriter();
        NestedClosureBridge.TryEmit(new CSharpWriter(csA), swiftWriter, envA, envA.ParentDecl as TypeDecl, ctx);
        var envB = new MethodEnvironment(methodB, typeDatabase);
        var csB = new StringWriter();
        NestedClosureBridge.TryEmit(new CSharpWriter(csB), swiftWriter, envB, envB.ParentDecl as TypeDecl, ctx);

        var swift = swiftOutput.ToString();
        // Every EntryPoint the C# side imports must be defined in the wrapper.
        Assert.Contains("@_cdecl(\"SBW_NCB_ReleaseInnerBox_ModuleA\")", swift);
        Assert.Contains("@_cdecl(\"SBW_NCB_ReleaseInnerBox_ModuleB\")", swift);
        Assert.Contains("SBW_NCB_ReleaseInnerBox_ModuleA", csA.ToString());
        Assert.Contains("SBW_NCB_ReleaseInnerBox_ModuleB", csB.ToString());
        // The two helpers must not collide as same-named Swift declarations either.
        var funcNames = swift.Split('\n')
            .Where(l => l.Contains("public func SBW_NCB_ReleaseInnerBox"))
            .Select(l => l.Trim()).Distinct().Count();
        Assert.Equal(2, funcNames);
    }

    [Fact]
    public void TryEmit_CallbackHasInnerFuncPtrAndContext()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Callback should have IntPtr params for inner funcPtr + context + outer context
        Assert.Contains("IntPtr innerFuncPtr", cs);
        Assert.Contains("IntPtr innerContext", cs);
        Assert.Contains("IntPtr outerContext", cs);
    }

    [Fact]
    public void TryEmit_PublicMethodHasNestedActionDelegate()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Public method should have Action<..., Action<...>> parameter type
        Assert.Contains("Action<", cs);
    }

    [Fact]
    public void TryEmit_PublicMethodConstructsInnerActionFromFuncPtr()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Callback should construct inner Action from delegate* unmanaged[Cdecl]
        Assert.Contains("delegate* unmanaged[Cdecl]", cs);
    }

    [Fact]
    public void TryEmit_PInvokeHasSwiftSelfForInstanceMethod()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        Assert.Contains("SwiftSelf", cs);
    }

    [Fact]
    public void TryEmit_DynamicSelfReturn_EmitsClassMarshal()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndDynamicSelfReturn();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // DynamicSelf return should emit direct SwiftMarshal — no buffer allocation
        Assert.DoesNotContain("NativeMemory.Alloc", cs);
        Assert.Contains("SwiftMarshal.MarshalFromSwift", cs);
    }

    [Fact]
    public void TryEmit_SimpleEnumInnerArg_UsesCorrectSwiftScalarType()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndSimpleEnumInnerArg();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Simple enum args should use unsafeBitCast in the trampoline
        Assert.Contains("unsafeBitCast", swift);
    }

    [Fact]
    public void TryEmit_ObjCOuterArg_UsesUnmanagedPassUnretained()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosureAndObjCOuterArg();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // ObjC outer args → Unmanaged.passUnretained().toOpaque()
        Assert.Contains("Unmanaged.passUnretained", swift);
    }

    [Fact]
    public void TryEmit_SetsWasEmitted()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(method.WasEmitted);
        Assert.True(method.UsesWrapperLibrary);
        Assert.True(method.UsesFreeFunctionWrapper);
    }

    // ─── Throw-window + _SBClosureCtx Owner Token ───

    [Fact]
    public void TryEmit_EscapingOuter_PreDeclaresGCHandleAndTransferredFlag()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Pre-declared GCHandle at method scope so finally can free it after a throw between
        // alloc and the P/Invoke returning successfully (e.g. ObjectDisposedException on a
        // previous arg, DllNotFoundException on entry-point resolution).
        Assert.Contains("GCHandle __gcHandle_0 = default;", cs);
        // For escaping outer closures the transferred flag is set only after a successful
        // P/Invoke return; the finally only frees handles whose ownership never moved into Swift.
        Assert.Contains("bool __transferred_0 = false;", cs);
    }

    [Fact]
    public void TryEmit_EscapingOuter_WrapsCallInTryFinallyWithConditionalFree()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        Assert.Contains("try", cs);
        Assert.Contains("finally", cs);
        Assert.Contains("__transferred_0 = true;", cs);
        Assert.Contains("if (!__transferred_0 && __gcHandle_0.IsAllocated) __gcHandle_0.Free();", cs);
    }

    [Fact]
    public void TryEmit_NonEscapingOuter_FreesGCHandleUnconditionallyInFinally()
    {
        // Theme C: a non-escaping OUTER closure is invoked synchronously inside the call and Swift
        // never owns it, so its per-call GCHandle must be freed on EVERY path. Pre-fix the
        // try/finally was gated on `anyEscaping`, so a method whose only outer closure is
        // non-escaping emitted no finally and leaked the handle (rooting the managed delegate and
        // its captured graph) for the process lifetime. The fix wraps every outer closure's alloc
        // in try/finally; the non-escaping branch frees unconditionally (no `__transferred` flag,
        // which is the escaping ownership-transfer gate).
        var (method, typeDatabase) = CreateMethodWithNonEscapingNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // GCHandle still pre-declared at method scope (consistent with the escaping layout)...
        Assert.Contains("GCHandle __gcHandle_0 = default;", cs);
        // ...with no ownership-transfer flag (that gate is escaping-only)...
        Assert.DoesNotContain("__transferred_0", cs);
        // ...and the handle is freed in a finally regardless of how the call exits...
        Assert.Contains("finally", cs);
        // ...unconditionally (not behind the `!__transferred` escaping gate).
        Assert.Contains("if (__gcHandle_0.IsAllocated) __gcHandle_0.Free();", cs);
    }

    [Fact]
    public void TryEmit_EscapingOuter_AllocsHappenInsideTryBlock()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // GCHandle.Alloc must live inside the try so an OOM mid-loop frees any
        // handle already taken — leaving allocs outside the try would leak the
        // earlier handle when a later alloc throws.
        var tryIdx = cs.IndexOf("try");
        var allocIdx = cs.IndexOf("__gcHandle_0 = GCHandle.Alloc");
        Assert.True(tryIdx >= 0, "Expected try block in escaping NCB output");
        Assert.True(allocIdx >= 0, "Expected GCHandle.Alloc in NCB output");
        Assert.True(tryIdx < allocIdx, $"Expected try block (index {tryIdx}) to precede GCHandle.Alloc (index {allocIdx})");
    }

    [Fact]
    public void TryEmit_EscapingOuter_SwiftWrapperConstructsClosureContextBox()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // For each escaping outer the Swift wrapper wraps the GCHandle pointer in an
        // _SBClosureCtx box (whose deinit upcalls C# and frees the handle exactly once).
        Assert.Contains("_sbWrapClosureContext", swift);
        Assert.Contains("let _box_0:", swift);
        // The synthesized adapter closure must explicitly capture _box_0 to track its
        // lifetime via Swift ARC.
        Assert.Contains("[_box_0]", swift);
        // Body observes the captured box so the optimizer cannot release it before the
        // closure runs (capture-list values are otherwise unused locals).
        Assert.Contains("_ = _box_0", swift);
    }

    // ─── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a method with a nested closure shape: outer closure takes an HTTPURLResponse
    /// and an inner completion closure; the inner closure takes a simple enum disposition.
    /// Outer closure: (HTTPURLResponse, (ResponseDisposition) -> Void) -> Void
    /// Inner closure: (ResponseDisposition) -> Void where ResponseDisposition is a simple enum.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosure()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        // Using NSObject as a stand-in for HTTPURLResponse (ObjC-bridged)
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Same shape as <see cref="CreateMethodWithNestedClosure"/> but the OUTER closure is NOT
    /// marked @escaping. The outer fires synchronously inside the call, so Swift never owns its
    /// GCHandle and the wrapper must free it unconditionally in finally (Theme C regression).
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNonEscapingNestedClosure()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void — note: NO @escaping on the outer.
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Same shape as <see cref="CreateMethodWithNestedClosure"/> but the inner closure is
    /// marked @escaping. An escaping inner closure may be stored and called after the outer
    /// invocation returns, so the box must outlive the call — the adapter must NOT release it.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithEscapingInnerClosure(
        string parentModuleName = "TestModule")
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        // A cross-module extension shape puts the PARENT in a foreign module while the
        // method itself belongs to the emitted module — the release symbol keys off the parent.
        var parentModuleDecl = parentModuleName == "TestModule" ? moduleDecl : CreateModuleDecl(parentModuleName);
        var parentDecl = CreateClassDecl("DataRequest", parentModuleDecl);

        // Inner closure: @escaping (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);
        innerClosure.Attributes.Add(new TypeSpecAttribute("escaping"));

        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure that returns Self (DynamicSelf).
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndDynamicSelfReturn()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Return type: Self (DynamicSelf) — IsDynamicSelf is computed from Name == "Self"
        var returnType = new NamedTypeSpec("Self");

        var method = CreateMethodDecl("onHTTPResponse", parentDecl, moduleDecl,
            returnType, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure where the inner closure takes a simple enum arg.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndSimpleEnumInnerArg()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (ResponseDisposition) -> Void (ResponseDisposition is a simple enum)
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.ResponseDisposition") }),
            TupleTypeSpec.Empty);

        // Outer closure: (Int, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Swift.Int"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onEvent", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with nested closure where the outer closure has an ObjC-bridged arg.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithNestedClosureAndObjCOuterArg()
    {
        var typeDatabase = CreateTypeDatabaseWithEnumTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("DataRequest", moduleDecl);

        // Inner closure: (Int) -> Void
        var innerClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);

        // Outer closure: (NSObject, innerClosure) -> Void
        var outerClosureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("Foundation.NSObject"),
                innerClosure
            }),
            TupleTypeSpec.Empty);
        outerClosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, outerClosureType, "_perform");

        return (method, typeDatabase);
    }

    // ─── Type/Declaration Factory Methods ─────────────────────────────

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSObject"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
                MetadataAccessor = "$s10TestModule11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnumTypes()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "UInt32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt32"),
                MetadataAccessor = "$ss6UInt32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSObject"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSObject"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
                MetadataAccessor = "$s10TestModule11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ResponseDisposition"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ResponseDisposition"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ResponseDisposition"),
                MetadataAccessor = "$s10TestModule19ResponseDispositionOMa",
                Flags = TypeRecordFlags.SimpleEnum | TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "UInt32"
            });
        // A generic class, so a parameter can be spelled Box<T> — a payload-handle param whose
        // spec still carries the method's own type parameter.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static MethodDecl CreateMethodDecl(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule11DataRequestC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateMethodDeclWithTwoClosures(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType1, string closureParamName1,
        ClosureTypeSpec closureType2, string closureParamName2)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule11DataRequestC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closureParamName1, closureType1, moduleDecl),
                CreateArgument(closureParamName2, closureType2, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }
}
