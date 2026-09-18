// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Reads witness-table-bearing generic requirements out of mangled symbols. The symbols were
/// emitted by the Swift 6.2 toolchain for a module named "M":
///   public class Base {}; public protocol P {}; public protocol Q {}; @objc public protocol O {}
///   public func sup&lt;T: Base&gt;(_: T)
///   public func pro&lt;T: P&gt;(_: T)
///   public func seq&lt;T: Sequence&gt;(_: T) where T.Element: Hashable
///   public func mix&lt;T: Base &amp; P, U: Collection&gt;(_: T, _: U) where U.Element == Int
///   public func objc&lt;T: O&gt;(_: T)
///   public func err&lt;E: Error&gt;(_: E)
///   public func anyobj&lt;T: AnyObject&gt;(_: T)
///   public struct G&lt;X: P&gt; { public func m&lt;Y: Q&gt;(_: Y) }
/// </summary>
public class GenericWitnessRequirementsTests
{
    [Theory]
    [InlineData("$s1M3supyyxAA4BaseCRbzlF", "")]
    [InlineData("$s1M3proyyxAA1PRzlF", "τ_0_0:M.P")]
    [InlineData("$s1M3seqyyxSTRzSH7ElementRpzlF", "τ_0_0:Swift.Sequence")]
    [InlineData("$s1M3mixyyx_q_tAA4BaseCRbzAA1PRzSlR_Si7ElementRt_r0_lF", "τ_0_0:M.P|τ_0_1:Swift.Collection")]
    [InlineData("$s1M3erryyxs5ErrorRzlF", "τ_0_0:Swift.Error")]
    [InlineData("$s1M6anyobjyyxRlzClF", "")]
    [InlineData("$s1M1GV1myyqd__AA1QRd__lF", "τ_1_0:M.Q")]
    public void Read_ReturnsProtocolRequirementsOnGenericParameters(string mangled, string expected)
    {
        var requirements = GenericWitnessRequirements.Read(mangled);

        Assert.NotNull(requirements);
        Assert.Equal(expected, string.Join("|", requirements!.Select(r => $"{r.GenericParameter}:{r.ProtocolModuleQualifiedName}")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a symbol")]
    public void Read_UndemangleableSymbol_ReturnsNull(string mangled) =>
        Assert.Null(GenericWitnessRequirements.Read(mangled));
}
