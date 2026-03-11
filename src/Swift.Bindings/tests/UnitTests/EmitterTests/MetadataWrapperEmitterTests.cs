// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MetadataWrapperEmitter: @_cdecl wrappers that return type metadata
/// as raw pointers, eliminating CallConvSwift from metadata P/Invokes.
/// </summary>
public class MetadataWrapperEmitterTests
{
    [Fact]
    public void GetMetadataSymbolName_TopLevelType_CorrectFormat()
    {
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("Nuke", "Nuke.ImagePipeline");
        Assert.StartsWith("SBW_GetMetadata_Nuke_Nuke_ImagePipeline_", symbol);
    }

    [Fact]
    public void GetMetadataSymbolName_NestedType_IncludesParent()
    {
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("Nuke", "Nuke.ImageRequest.Priority");
        Assert.StartsWith("SBW_GetMetadata_Nuke_Nuke_ImageRequest_Priority_", symbol);
        Assert.DoesNotContain(".", symbol.Substring("SBW_GetMetadata_".Length));
    }

    [Fact]
    public void GetMetadataSymbolName_DifferentTypes_DifferentSymbols()
    {
        var sym1 = MetadataWrapperEmitter.GetMetadataSymbolName("Mod", "Mod.TypeA");
        var sym2 = MetadataWrapperEmitter.GetMetadataSymbolName("Mod", "Mod.TypeB");
        Assert.NotEqual(sym1, sym2);
    }

    [Fact]
    public void EmitIfNeeded_EmitsSwiftWrapper()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_Nuke_Nuke_ImagePipeline_ABCD1234";

        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "Nuke", "Nuke.ImagePipeline", symbol, ctx);

        var output = sw.ToString();
        Assert.Contains($"@_cdecl(\"{symbol}\")", output);
        Assert.Contains("unsafeBitCast(Nuke.ImagePipeline.self as Any.Type, to: UnsafeMutableRawPointer.self)", output);
        Assert.Contains("-> UnsafeMutableRawPointer", output);
    }

    [Fact]
    public void EmitIfNeeded_Dedup_SecondCallIsNoop()
    {
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_Test_Test_Type_ABCD1234";

        var sw1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(sw1);
        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter1, "Test", "Test.Type", symbol, ctx);
        var output1 = sw1.ToString();

        var sw2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(sw2);
        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter2, "Test", "Test.Type", symbol, ctx);
        var output2 = sw2.ToString();

        Assert.Contains("@_cdecl", output1);
        Assert.DoesNotContain("@_cdecl", output2);
    }
}
