// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SelfReconstructionEmitter — shared self-reconstruction patterns for @_cdecl wrappers.
/// </summary>
public class SelfReconstructionEmitterTests
{
    [Fact]
    public void Emit_Class_EmitsUnmanagedFromOpaque()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.Emit(swiftWriter, isClass: true, isMutating: false, "TestModule.MyClass");

        var result = output.ToString();
        Assert.Contains("let obj = Unmanaged<TestModule.MyClass>.fromOpaque(self_).takeUnretainedValue()", result);
    }

    [Fact]
    public void Emit_StructImmutable_EmitsAssumingMemoryBoundWithLet()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.Emit(swiftWriter, isClass: false, isMutating: false, "TestModule.MyStruct");

        var result = output.ToString();
        Assert.Contains("let obj = self_.assumingMemoryBound(to: TestModule.MyStruct.self).pointee", result);
    }

    [Fact]
    public void Emit_StructMutating_EmitsNothing()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.Emit(swiftWriter, isClass: false, isMutating: true, "TestModule.MyStruct");

        var result = output.ToString();
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Emit_ClassIgnoresMutating()
    {
        // For classes, isMutating is irrelevant — classes always use Unmanaged
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.Emit(swiftWriter, isClass: true, isMutating: true, "TestModule.MyClass");

        var result = output.ToString();
        Assert.Contains("Unmanaged<TestModule.MyClass>.fromOpaque(self_).takeUnretainedValue()", result);
    }

    [Fact]
    public void EmitProtocolCast_Immutable_EmitsLetWithAnyObjectCast()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, "_SBW_MyProtocol", isMutable: false);

        var result = output.ToString();
        Assert.Contains("let obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_MyProtocol", result);
    }

    [Fact]
    public void EmitProtocolCast_Mutable_EmitsVarWithAnyObjectCast()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, "_SBW_SetterProtocol", isMutable: true);

        var result = output.ToString();
        Assert.Contains("var obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_SetterProtocol", result);
    }

    [Fact]
    public void EmitProtocolCast_DefaultImmutable()
    {
        // Default isMutable parameter should be false (let)
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, "_SBW_Proto");

        var result = output.ToString();
        Assert.StartsWith("let ", result.TrimStart());
    }

    [Fact]
    public void Emit_NestedType_PreservesModuleQualification()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        SelfReconstructionEmitter.Emit(swiftWriter, isClass: true, isMutating: false, "Nuke.ImageRequest.Priority");

        var result = output.ToString();
        Assert.Contains("Unmanaged<Nuke.ImageRequest.Priority>", result);
    }
}
