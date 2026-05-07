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
    public void IsEligible_Constructor_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithNestedClosure();
        method.IsConstructor = true;
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

        NestedClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // takeUnretainedValue — bounded leak is safe for multi-call inner closures.
        // passRetained(+1) keeps box alive for escaping closures; leak is bounded.
        Assert.Contains("takeUnretainedValue", swift);
        Assert.DoesNotContain("takeRetainedValue", swift);
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

    // ─── Throw-window + _SBClosureCtx Owner Token (Bug 1 Cat 3 / Bug 3 Case 2) ───

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
    /// Creates a method shaped like Alamofire's onHTTPResponse(on:perform:).
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
