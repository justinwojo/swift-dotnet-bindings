// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using BindingsGeneration;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 60 (architecture review §2009–2022) golden grammar gate. Before F60 the Swift
/// symbol-mangling fragments the generator assumes (<c>Tq</c>, <c>Tu</c>, <c>TjTu</c>, <c>$s</c>,
/// <c>_$s</c>, <c>$ss</c>) were duplicated as bare string literals across the parser and emitters,
/// so a toolchain grammar change had no single audit point. <see cref="ManglingProbes"/> now owns
/// every fragment as a named constant.
///
/// This test pins (1) the literal value of each constant — so a typo or an upstream grammar change
/// is a one-file, one-test review; (2) the helper behavior; and (3) a parity guard that replicates
/// the OLD inline concatenation logic and asserts it is identical to the new helpers across a battery
/// of inputs, proving the F60 refactor was behavior-preserving.
/// </summary>
public class ManglingProbesTests
{
    #region Golden constant values

    [Fact]
    public void SuffixConstants_MatchSwiftManglingGrammar()
    {
        Assert.Equal("Tq", ManglingProbes.MethodDescriptorSuffix);
        Assert.Equal("Tu", ManglingProbes.AsyncFunctionSuffix);
        Assert.Equal("Tj", ManglingProbes.DispatchThunkSuffix);
        // The async-class-accessor suffix is the dispatch thunk followed by the async marker.
        Assert.Equal("TjTu", ManglingProbes.AsyncDispatchThunkSuffix);
        Assert.Equal(
            ManglingProbes.DispatchThunkSuffix + ManglingProbes.AsyncFunctionSuffix,
            ManglingProbes.AsyncDispatchThunkSuffix);
    }

    [Fact]
    public void PrefixConstants_MatchSwiftManglingGrammar()
    {
        Assert.Equal("$s", ManglingProbes.StablePrefix);
        Assert.Equal("_$s", ManglingProbes.StablePrefixUnderscored);
        Assert.Equal("$ss", ManglingProbes.StdlibPrefix);
        // The derived prefixes are composed from the stable prefix, not independent literals.
        Assert.Equal("_" + ManglingProbes.StablePrefix, ManglingProbes.StablePrefixUnderscored);
        Assert.Equal(ManglingProbes.StablePrefix + "s", ManglingProbes.StdlibPrefix);
    }

    #endregion

    #region Helper behavior

    [Theory]
    [InlineData("barTq", "bar", true)]
    [InlineData("bar", "bar", false)]   // descriptor symbol absent
    [InlineData("barTu", "bar", false)] // async marker is not a method descriptor
    public void HasMethodDescriptor_ChecksTqSuffix(string symbolInTbd, string mangled, bool expected)
    {
        var tbd = new HashSet<string> { symbolInTbd };
        Assert.Equal(expected, ManglingProbes.HasMethodDescriptor(tbd, mangled));
    }

    [Theory]
    [InlineData("fooTu", "foo", true)]    // free/struct accessor async marker
    [InlineData("fooTjTu", "foo", true)]  // class accessor async marker (through dispatch thunk)
    [InlineData("fooTj", "foo", false)]   // dispatch thunk alone is not async
    [InlineData("foo", "foo", false)]     // no async marker
    public void IsAsyncAccessor_ChecksTuAndTjTuSuffixes(string symbolInTbd, string mangled, bool expected)
    {
        var tbd = new HashSet<string> { symbolInTbd };
        Assert.Equal(expected, ManglingProbes.IsAsyncAccessor(tbd, mangled));
    }

    [Theory]
    [InlineData("$ss8SendableP", true)]
    [InlineData("$sSH", false)]                         // well-known substitution, not the $ss root
    [InlineData("$s10Foundation13LocalizedErrorP", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStdlibMangledName_ChecksDollarSsPrefix(string? mangled, bool expected)
    {
        Assert.Equal(expected, ManglingProbes.IsStdlibMangledName(mangled));
    }

    [Theory]
    [InlineData("Foundation", "$s10Foundation")]
    [InlineData("RealityKit", "$s10RealityKit")]
    [InlineData("UIKit", "$s5UIKit")]
    public void ModulePrefix_EncodesLengthAndName(string module, string expected)
    {
        Assert.Equal(expected, ManglingProbes.ModulePrefix(module));
    }

    #endregion

    #region TryGetModuleFromMangledName (moved verbatim from SwiftABIParserTests under F60)

    // Unlike the USR (which records the CURRENT module), the stable mangled name carries the
    // ORIGINAL module of an @_originallyDefinedIn type — which is what the TBD's
    // protocol-conformance-descriptor symbols are mangled with. The conformance-descriptor
    // lookup falls back to this module when the current-module identity misses (e.g. RealityKit's
    // AnchorEntity re-exported as RealityFoundation.AnchorEntity, descriptor symbol
    // `$s10RealityKit12AnchorEntityC...Mc`).
    [Theory]
    [InlineData("$s10RealityKit12AnchorEntityC", "RealityKit")]                       // class, no underscore
    [InlineData("_$s10RealityKit12AnchorEntityC", "RealityKit")]                      // class, leading underscore
    [InlineData("_$s27SwiftBindingsTestLibPhantom15RelocatedEntityC", "SwiftBindingsTestLibPhantom")] // @_originallyDefinedIn phantom module
    [InlineData("$s10Foundation13LocalizedErrorP", "Foundation")]                     // protocol
    public void TryGetModuleFromMangledName_LengthPrefixed_ReturnsModule(string mangled, string expected)
    {
        Assert.True(ManglingProbes.TryGetModuleFromMangledName(mangled, out var module));
        Assert.Equal(expected, module);
    }

    [Theory]
    [InlineData("$ss8SendableP")]      // stdlib substitution — no length prefix
    [InlineData("$sSH")]               // stdlib well-known substitution
    [InlineData("c:objc(cs)NSObject")] // ObjC mangled — not a Swift stable name
    [InlineData("")]                   // empty
    [InlineData("$s")]                 // truncated
    [InlineData("$s99RealityKit")]     // length overruns string
    public void TryGetModuleFromMangledName_NonLengthPrefixedOrInvalid_ReturnsFalse(string mangled)
    {
        Assert.False(ManglingProbes.TryGetModuleFromMangledName(mangled, out var module));
        Assert.Null(module);
    }

    #endregion

    #region Parity guard — new helpers are identical to the OLD inline literal logic

    // The strings the parser/emitters fed through the OLD inline concatenation, gathered into one
    // TBD-shaped set plus a battery of probe base names. The parity tests below replicate the exact
    // pre-F60 expressions and assert the new helpers agree on every input.
    private static readonly HashSet<string> ParityTbd = new()
    {
        "barTq", "bazTq",
        "fooTu", "quxTjTu", "propTj",
        "$ss8SendableP", "$s10Foundation13LocalizedErrorP",
    };

    private static readonly string[] ParityProbes =
    {
        "bar", "baz", "foo", "qux", "prop", "missing", "$ss8SendableP", "",
    };

    [Fact]
    public void HasMethodDescriptor_MatchesOldInlineLogic()
    {
        foreach (var mangled in ParityProbes)
        {
            // OLD: !_demangledTbd.AllSymbols.Contains(method.MangledName + "Tq")  (negated at call site)
            bool oldHas = ParityTbd.Contains(mangled + "Tq");
            Assert.Equal(oldHas, ManglingProbes.HasMethodDescriptor(ParityTbd, mangled));
        }
    }

    [Fact]
    public void IsAsyncAccessor_MatchesOldInlineLogic()
    {
        foreach (var mangled in ParityProbes)
        {
            // OLD: AllSymbols.Contains(accessor.MangledName + "Tu") || ...Contains(... + "TjTu")
            bool oldAsync = ParityTbd.Contains(mangled + "Tu") || ParityTbd.Contains(mangled + "TjTu");
            Assert.Equal(oldAsync, ManglingProbes.IsAsyncAccessor(ParityTbd, mangled));
        }
    }

    [Theory]
    [InlineData("$ss8SendableP")]
    [InlineData("$s10Foundation13LocalizedErrorP")]
    [InlineData("$sSH")]
    [InlineData("c:objc(cs)NSObject")]
    [InlineData("")]
    public void IsStdlibMangledName_MatchesOldInlineLogic(string mangled)
    {
        // OLD: !string.IsNullOrEmpty(MangledName) && MangledName.StartsWith("$ss", Ordinal)
        bool oldStdlib = !string.IsNullOrEmpty(mangled)
            && mangled.StartsWith("$ss", System.StringComparison.Ordinal);
        Assert.Equal(oldStdlib, ManglingProbes.IsStdlibMangledName(mangled));
    }

    [Theory]
    [InlineData("Foundation")]
    [InlineData("RealityKit")]
    [InlineData("UIKit")]
    public void ModulePrefix_MatchesOldInlineLogic(string module)
    {
        // OLD: $"$s{moduleName.Length}{moduleName}"
        string oldPrefix = $"$s{module.Length}{module}";
        Assert.Equal(oldPrefix, ManglingProbes.ModulePrefix(module));
    }

    #endregion
}
