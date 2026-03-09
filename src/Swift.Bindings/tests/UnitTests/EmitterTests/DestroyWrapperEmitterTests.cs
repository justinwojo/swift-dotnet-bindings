// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for DestroyWrapperEmitter: per-type @_cdecl destroy wrappers that route
/// Dispose() through C calling convention to avoid CallConvSwift crashes on NativeAOT.
/// </summary>
public class DestroyWrapperEmitterTests
{
    #region Swift Emission Tests

    [Fact]
    public void EmitSwiftDestroyWrapper_EmitsCorrectCdeclFunction()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        var emitted = DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.ImageRequest", ctx);

        Assert.True(emitted);
        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Destroy_Nuke_ImageRequest\")", output);
        Assert.Contains("bufferPtr.assumingMemoryBound(to: Nuke.ImageRequest.self).deinitialize(count: 1)", output);
    }

    [Fact]
    public void EmitSwiftDestroyWrapper_NestedType_EmitsCorrectSymbol()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        var emitted = DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.ImageRequest.Priority", ctx);

        Assert.True(emitted);
        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Destroy_Nuke_ImageRequest_Priority\")", output);
        Assert.Contains("bufferPtr.assumingMemoryBound(to: Nuke.ImageRequest.Priority.self).deinitialize(count: 1)", output);
    }

    [Fact]
    public void EmitSwiftDestroyWrapper_SecondCallReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.ImageRequest", ctx);
        var emittedSecond = DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.ImageRequest", ctx);

        Assert.False(emittedSecond);
    }

    [Fact]
    public void EmitSwiftDestroyWrapper_DifferentTypes_EmitsBoth()
    {
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        var emitted1 = DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.ImageRequest", ctx);
        var emitted2 = DestroyWrapperEmitter.EmitSwiftDestroyWrapper(writer, "Nuke", "Nuke.DataCache", ctx);

        Assert.True(emitted1);
        Assert.True(emitted2);
        var output = sw.ToString();
        Assert.Contains("SBW_Destroy_Nuke_ImageRequest", output);
        Assert.Contains("SBW_Destroy_Nuke_DataCache", output);
    }

    #endregion

    #region C# Registration Emission Tests

    [Fact]
    public void EmitCSharpDestroyRegistration_EmitsPInvokeAndFieldInitializer()
    {
        var sw = new StringWriter();
        var writer = new CSharpWriter(sw);

        DestroyWrapperEmitter.EmitCSharpDestroyRegistration(
            writer, "ImageRequest", "ImageRequest", "Nuke", "Nuke.ImageRequest", "NukeSwiftBindings");

        var output = sw.ToString();
        Assert.Contains("private static readonly bool _sbw_destroyRegistered = _SBW_RegisterDestroy();", output);
        Assert.Contains("SwiftSafeHandle<ImageRequest>.RegisterDestroyAction(_SBW_Destroy);", output);
        Assert.Contains("DllImport(\"NukeSwiftBindings\"", output);
        Assert.Contains("EntryPoint = \"SBW_Destroy_Nuke_ImageRequest\"", output);
        Assert.Contains("private static extern void _SBW_Destroy(IntPtr handle);", output);
    }

    [Fact]
    public void EmitCSharpDestroyRegistration_DerivedClass_UsesRootBaseType()
    {
        var sw = new StringWriter();
        var writer = new CSharpWriter(sw);

        // For derived classes, safeHandleTypeName is the root base type
        DestroyWrapperEmitter.EmitCSharpDestroyRegistration(
            writer, "Dog", "Animal", "TestLib", "TestLib.Animal", "TestLibSwiftBindings");

        var output = sw.ToString();
        Assert.Contains("SwiftSafeHandle<Animal>.RegisterDestroyAction(_SBW_Destroy);", output);
    }

    #endregion

    #region EmitIfNeeded Integration Tests

    [Fact]
    public void EmitIfNeeded_NoWrapperLibrary_DoesNotEmit()
    {
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "ImageRequest", "ImageRequest",
            "Nuke", "Nuke.ImageRequest", null, ctx);

        Assert.Empty(csSw.ToString());
        Assert.Empty(swSw.ToString());
    }

    [Fact]
    public void EmitIfNeeded_EmptyWrapperLibrary_DoesNotEmit()
    {
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "ImageRequest", "ImageRequest",
            "Nuke", "Nuke.ImageRequest", "", ctx);

        Assert.Empty(csSw.ToString());
        Assert.Empty(swSw.ToString());
    }

    [Fact]
    public void EmitIfNeeded_GenericType_DoesNotEmit()
    {
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "SpikeBox", "SpikeBox<TElement>",
            "TestLib", "TestLib.SpikeBox", "TestLibSwiftBindings", ctx);

        Assert.Empty(csSw.ToString());
        Assert.Empty(swSw.ToString());
    }

    [Fact]
    public void EmitIfNeeded_ClosedGenericContainingType_DoesNotEmit()
    {
        // A closed generic containing type (Container<int>) should still be skipped.
        // Complements EmitIfNeeded_GenericType_DoesNotEmit which uses an open generic.
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "Container", "Container<int>",
            "TestLib", "TestLib.Container", "TestLibSwiftBindings", ctx);

        Assert.Empty(csSw.ToString());
        Assert.Empty(swSw.ToString());
    }

    [Fact]
    public void EmitIfNeeded_NonGenericTypeWithGenericRootSafeHandle_Emits()
    {
        // A non-generic derived type (IntContainer) whose root base is a closed generic
        // (Container<int>) should NOT be skipped — DllImport is legal in non-generic types.
        // This tests that the generic skip only fires when the containing type itself is generic.
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "IntContainer", "Container<int>",
            "TestLib", "TestLib.IntContainer", "TestLibSwiftBindings", ctx);

        var csOutput = csSw.ToString();
        var swiftOutput = swSw.ToString();

        // Should emit — the containing type IntContainer is not generic
        Assert.Contains("@_cdecl(\"SBW_Destroy_TestLib_IntContainer\")", swiftOutput);
        Assert.Contains("SwiftSafeHandle<Container<int>>.RegisterDestroyAction(_SBW_Destroy);", csOutput);
        Assert.Contains("DllImport(\"TestLibSwiftBindings\"", csOutput);
    }

    [Fact]
    public void EmitIfNeeded_WithWrapperLibrary_EmitsBothSides()
    {
        var ctx = new ModuleEmissionContext();
        var csSw = new StringWriter();
        var swSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swSw);

        DestroyWrapperEmitter.EmitIfNeeded(
            csWriter, swiftWriter, "ImageRequest", "ImageRequest",
            "Nuke", "Nuke.ImageRequest", "NukeSwiftBindings", ctx);

        var csOutput = csSw.ToString();
        var swiftOutput = swSw.ToString();

        // Swift side
        Assert.Contains("@_cdecl(\"SBW_Destroy_Nuke_ImageRequest\")", swiftOutput);
        Assert.Contains("deinitialize(count: 1)", swiftOutput);

        // C# side
        Assert.Contains("RegisterDestroyAction", csOutput);
        Assert.Contains("DllImport(\"NukeSwiftBindings\"", csOutput);
    }

    #endregion

    #region Symbol Naming Tests

    [Fact]
    public void GetDestroySymbolName_SimpleType()
    {
        var symbol = DestroyWrapperEmitter.GetDestroySymbolName("Nuke", "Nuke.ImageRequest");
        Assert.Equal("SBW_Destroy_Nuke_ImageRequest", symbol);
    }

    [Fact]
    public void GetDestroySymbolName_NestedType()
    {
        var symbol = DestroyWrapperEmitter.GetDestroySymbolName("Nuke", "Nuke.ImageRequest.Priority");
        Assert.Equal("SBW_Destroy_Nuke_ImageRequest_Priority", symbol);
    }

    [Fact]
    public void GetDestroySymbolName_StripsModulePrefix()
    {
        // When swiftTypeName already starts with moduleName, it should be stripped
        var symbol = DestroyWrapperEmitter.GetDestroySymbolName("SwiftBindingsTestLib", "SwiftBindingsTestLib.IntContainer");
        Assert.Equal("SBW_Destroy_SwiftBindingsTestLib_IntContainer", symbol);
        // Should NOT be "SBW_Destroy_SwiftBindingsTestLib_SwiftBindingsTestLib_IntContainer"
    }

    #endregion

    #region ModuleEmissionContext Tracking Tests

    [Fact]
    public void ModuleEmissionContext_TracksDestroyWrapperSymbols()
    {
        var ctx = new ModuleEmissionContext();

        Assert.False(ctx.HasDestroyWrapperSymbol("SBW_Destroy_Nuke_ImageRequest"));

        Assert.True(ctx.TryAddDestroyWrapperSymbol("SBW_Destroy_Nuke_ImageRequest"));
        Assert.True(ctx.HasDestroyWrapperSymbol("SBW_Destroy_Nuke_ImageRequest"));

        // Second add returns false (already tracked)
        Assert.False(ctx.TryAddDestroyWrapperSymbol("SBW_Destroy_Nuke_ImageRequest"));
    }

    #endregion
}
