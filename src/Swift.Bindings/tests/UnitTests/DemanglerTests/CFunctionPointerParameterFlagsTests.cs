// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// ABI JSON prints a <c>@convention(c)</c> function pointer exactly like a Swift closure, so the
/// mangled name is the only per-parameter record of the convention. These pin which parameters the
/// demangler reports as C function pointers, and the parser stamping that onto each closure spec.
/// The manglings are real Swift 6 symbols for the fixture signatures named in each case.
/// </summary>
public class CFunctionPointerParameterFlagsTests
{
    // combineCFunctions(_: @convention(c) (Int32) -> Int32, _: @convention(c) (Int32) -> Int32, _: Int32) -> Int32
    private const string TwoCFunctions = "$s20SwiftBindingsTestLib17combineCFunctionsys5Int32VA2DXC_A2DXCADtF";

    // combineSwiftAndCClosures(_: (Int32) -> Int32, _: @convention(c) (Int32) -> Int32, _: Int32) -> Int32
    private const string SwiftThenC = "$s20SwiftBindingsTestLib07combineA12AndCClosuresys5Int32VA2DXE_A2DXCADtF";

    // optionalCAndSwift(_: (@convention(c) (Int32) -> Int32)?, _: (Int32) -> Int32) -> Int32
    private const string OptionalCThenSwift = "$s20SwiftBindingsTestLib012optionalCAndA0ys5Int32VA2DXCSg_A2DXEtF";

    // returnsCFunction(_: (Int32) -> Int32) -> @convention(c) (Int32) -> Int32
    private const string CFunctionOnlyInReturn = "$s20SwiftBindingsTestLib16returnsCFunctionys5Int32VADXCA2DXEF";

    // PInvoke_spike_callAsyncOp(_: @convention(c) (Int32, Int64) -> Void,
    //   _: @convention(c) (UnsafePointer<Int8>, Int32, Int64) -> Void, _: Int64,
    //   _: UnsafeMutableRawPointer, _: @convention(c) (raw ×4) -> Void)
    private const string AsyncSpike =
        "$s20SwiftBindingsTestLib25PInvoke_spike_callAsyncOpyyys5Int32V_s5Int64VtXC_ySPys4Int8VG_AdFtXCAFSvySv_S3vtXCtF";

    // callCFunction(_: @convention(c) (Int32) -> Int32) -> Int32
    private const string SingleCFunction = "$s20SwiftBindingsTestLib13callCFunctionys5Int32VA2DXCF";

    [Theory]
    [InlineData(TwoCFunctions, 3, new[] { true, true, false })]
    [InlineData(SwiftThenC, 3, new[] { false, true, false })]
    [InlineData(OptionalCThenSwift, 2, new[] { true, false })]
    [InlineData(CFunctionOnlyInReturn, 1, new[] { false })]
    [InlineData(AsyncSpike, 5, new[] { true, true, false, false, true })]
    [InlineData(SingleCFunction, 1, new[] { true })]
    public void Flags_MarkExactlyTheCFunctionPointerParameters(string mangledName, int parameterCount, bool[] expected)
    {
        var flags = new Swift5Demangler().GetCFunctionPointerParameterFlags(mangledName, parameterCount);

        Assert.NotNull(flags);
        Assert.Equal(expected, flags.ToArray());
    }

    [Theory]
    [InlineData(TwoCFunctions, 2)]
    [InlineData(SwiftThenC, 4)]
    [InlineData("", 1)]
    [InlineData("not a mangled name", 1)]
    public void Flags_UnknownWhenTheSignatureDoesNotMatch(string mangledName, int parameterCount)
    {
        Assert.Null(new Swift5Demangler().GetCFunctionPointerParameterFlags(mangledName, parameterCount));
    }

    [Fact]
    public void Parser_MarksOnlyTheFlaggedClosureParameters()
    {
        var swiftClosure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        var cClosure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        var optionalCClosure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        var optional = new NamedTypeSpec("Swift.Optional", optionalCClosure);
        var method = MethodWith(swiftClosure, cClosure, optional, new NamedTypeSpec("Swift.Int32"));

        SwiftABIParser.MarkConventionCClosureParameters(method, new[] { false, true, true, true });

        Assert.False(swiftClosure.IsConventionC);
        Assert.True(cClosure.IsConventionC);
        Assert.True(optionalCClosure.IsConventionC);
    }

    [Fact]
    public void Parser_LeavesEveryClosureAloneWhenTheFlagsAreUnknownOrMisaligned()
    {
        var first = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        var second = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"));
        var method = MethodWith(first, second);

        SwiftABIParser.MarkConventionCClosureParameters(method, null);
        SwiftABIParser.MarkConventionCClosureParameters(method, new[] { true });

        Assert.False(first.IsConventionC);
        Assert.False(second.IsConventionC);
    }

    [Fact]
    public void Parser_NeverClearsAConventionTheAttributeAlreadyCarried()
    {
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32"))
        {
            IsConventionC = true,
        };
        var method = MethodWith(closure);

        SwiftABIParser.MarkConventionCClosureParameters(method, new[] { false });

        Assert.True(closure.IsConventionC);
    }

    private static MethodDecl MethodWith(params TypeSpec[] parameterTypes)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var signature = new List<ArgumentDecl> { Argument("", new NamedTypeSpec("Swift.Int32"), moduleDecl) };
        for (int i = 0; i < parameterTypes.Length; i++)
            signature.Add(Argument($"p{i}", parameterTypes[i], moduleDecl));
        return new MethodDecl
        {
            Name = "f",
            MangledName = "",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
        };
    }

    private static ArgumentDecl Argument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = typeSpec,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl,
    };
}
