// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins <see cref="Swift5Demangler.HasVariadicParameterMarker(string)"/>, the
/// per-overload-exact variadic detector used for symbols whose top-level node has
/// no reducer rule (constructors/allocators). A variadic <c>init(x: T...)</c> and a
/// plain <c>init(x: [T])</c> share the same Array ABI shape and printedName; only the
/// mangled-name <c>d</c> marker tells them apart. Getting this right is what keeps the
/// generator from emitting a C# P/Invoke for a variadic constructor whose @_cdecl
/// wrapper can't compile (it can't splat an array into Swift varargs), which would be
/// stripped and leave a dangling entry point.
/// </summary>
public class VariadicMarkerTests
{
    // Real symbols from BindingTests/SwiftBindingsTestLib.
    // VariadicHolder.init(values: Int32...) — the variadic constructor.
    private const string VariadicCtor =
        "$s20SwiftBindingsTestLib14VariadicHolderV6valuesACs5Int32Vd_tcfc";

    // IntContainer.init(items: [Int32]) — a plain-array constructor that MUST keep
    // emitting; if the detector over-fired on it the binding would silently lose the ctor.
    private const string PlainArrayCtor =
        "$s20SwiftBindingsTestLib12IntContainerV5itemsACSays5Int32VG_tcfc";

    [Fact]
    public void VariadicConstructor_HasMarker()
    {
        var demangler = new Swift5Demangler();
        Assert.True(demangler.HasVariadicParameterMarker(VariadicCtor),
            "variadic init(values: Int32...) must be detected via the mangled 'd' marker");
    }

    [Fact]
    public void PlainArrayConstructor_HasNoMarker()
    {
        var demangler = new Swift5Demangler();
        Assert.False(demangler.HasVariadicParameterMarker(PlainArrayCtor),
            "plain init(items: [Int32]) must NOT be flagged variadic (no over-skip)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a mangled name")]
    public void Malformed_ReturnsFalse(string mangledName)
    {
        var demangler = new Swift5Demangler();
        Assert.False(demangler.HasVariadicParameterMarker(mangledName));
    }
}
