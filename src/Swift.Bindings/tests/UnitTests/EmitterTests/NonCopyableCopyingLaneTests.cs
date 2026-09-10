// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The emission-time decision for a <c>~Copyable</c> value that a member reaches through a lane
/// whose only materialisation step copies: a closure argument, a closure result, a tuple element,
/// or an async parameter's staging buffer.
///
/// <para>
/// Two predicates are under test and the split between them is the point. <c>IsNonCopyableType</c>
/// answers "is THIS value non-copyable" and has sixteen product callers that copy values, spell
/// <c>consuming</c>, or decide whether a wire buffer still needs destroying — every one of them is
/// right to see a closure value as copyable, because it is. <c>SignatureReachesNonCopyable</c>
/// answers the different question "can a non-copyable value be reached from here", which is the one
/// the binding decision needs. Widening the first instead of adding the second would have made
/// those sixteen callers wrong, so a test pins each predicate's answer for the same closure shape.
/// </para>
///
/// <para>
/// Every positive case is paired with the structurally identical copyable shape. Without the pair a
/// gate that had widened to refuse all closures — or all async parameters — would still satisfy the
/// positive assertion while silently costing copyable members their bindings.
/// </para>
/// </summary>
public class NonCopyableCopyingLaneTests
{
    private const string MoveOnly = "TestModule.MoveOnlyResource";
    private const string Copyable = "TestModule.CopyableResource";

    #region SignatureReachesNonCopyable — closure lanes

    /// <summary>
    /// The detection gap the whole lane rests on: the member-level oracle stops at
    /// <c>if (typeSpec is not NamedTypeSpec) return false;</c>, so a closure carrying a
    /// non-copyable argument reads as an ordinary copyable function value and the invoke thunk
    /// heap-materialises the argument with a copy witness that traps.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_ClosureTakingNonCopyable_ReturnsTrue()
    {
        var (db, module) = Fixture();

        var spec = new ClosureTypeSpec(new NamedTypeSpec(MoveOnly), new NamedTypeSpec("Swift.Int32"));

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    /// <summary>
    /// A <c>~Copyable</c> closure argument must carry an ownership specifier in Swift, and the
    /// parser renders that specifier as a separate tuple element — <c>(borrowing Token) -&gt; Int32</c>
    /// arrives as <c>(borrowing, TestModule.MoveOnlyResource) -&gt; Swift.Int32</c>. A walk that only
    /// handled a bare named argument would miss the shape that actually occurs.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_ClosureArgumentListRenderedAsATuple_ReturnsTrue()
    {
        var (db, module) = Fixture();

        var spec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("borrowing"), new NamedTypeSpec(MoveOnly) }),
            new NamedTypeSpec("Swift.Int32"));

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    /// <summary>
    /// The result position is the lane with no ownership specifier to give it away: <c>() -&gt; Token</c>
    /// is spelled exactly like any other producing closure, and the callback marshals the produced
    /// value back through the value witness.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_ClosureReturningNonCopyable_ReturnsTrue()
    {
        var (db, module) = Fixture();

        var spec = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec(MoveOnly));

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    /// <summary>
    /// A closure nested inside a generic argument still reaches the value. The named-type walk in
    /// <c>IsNonCopyableType</c> recurses over generic arguments but drops any that is not itself a
    /// named spec, so the closure arm has to be re-entered from the generic walk rather than only
    /// from the top.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_ClosureNestedInAGenericArgument_ReturnsTrue()
    {
        var (db, module) = Fixture();

        var spec = new NamedTypeSpec(
            "Swift.Array",
            new ClosureTypeSpec(new NamedTypeSpec(MoveOnly), new NamedTypeSpec("Swift.Int32")));

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    /// <summary>
    /// A bare tuple element is the sibling of the closure argument: the element extractor
    /// materialises each element out of the tuple's buffer with the same copy witness.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_TupleElement_ReturnsTrue()
    {
        var (db, module) = Fixture();

        var spec = new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec(MoveOnly) });

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    /// <summary>
    /// The superset property the doc comment claims: every shape the narrow predicate calls
    /// non-copyable is still non-copyable here, so the lane gate never has to consult both.
    /// </summary>
    [Fact]
    public void SignatureReachesNonCopyable_DirectlyNamedNonCopyable_ReturnsTrue()
    {
        var (db, module) = Fixture();

        Assert.True(WrapperValidation.SignatureReachesNonCopyable(new NamedTypeSpec(MoveOnly), db, module));
    }

    /// <summary>
    /// The blast-radius guard. Sixteen product callers ask <c>IsNonCopyableType</c> the VALUE
    /// question — may this be copied, must this be spelled <c>consuming</c>, does this wire buffer
    /// still need destroying — and a closure value carrying a non-copyable argument is copyable by
    /// all three. Answering true here would silently change the answer at every one of them, which
    /// is why the reachability question got its own predicate instead.
    /// </summary>
    [Fact]
    public void IsNonCopyableType_ClosureCarryingNonCopyable_StaysFalse()
    {
        var (db, module) = Fixture();

        var spec = new ClosureTypeSpec(new NamedTypeSpec(MoveOnly), new NamedTypeSpec("Swift.Int32"));

        Assert.False(WrapperValidation.IsNonCopyableType(spec, db, module));
    }

    #endregion

    #region Copyable siblings — nothing that binds today may stop binding

    /// <summary>
    /// The same five shapes over a copyable struct. Each is the structural twin of a positive case
    /// above, differing only in the copyability of the type inside, so a predicate that had widened
    /// to "any closure" or "any tuple" fails here rather than in a downstream gate weeks later.
    /// </summary>
    [Theory]
    [InlineData("closure argument")]
    [InlineData("closure argument rendered as a tuple")]
    [InlineData("closure result")]
    [InlineData("closure nested in a generic argument")]
    [InlineData("tuple element")]
    public void SignatureReachesNonCopyable_CopyableSiblings_ReturnFalse(string shape)
    {
        var (db, module) = Fixture();

        TypeSpec spec = shape switch
        {
            "closure argument" =>
                new ClosureTypeSpec(new NamedTypeSpec(Copyable), new NamedTypeSpec("Swift.Int32")),
            "closure argument rendered as a tuple" =>
                new ClosureTypeSpec(
                    new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("borrowing"), new NamedTypeSpec(Copyable) }),
                    new NamedTypeSpec("Swift.Int32")),
            "closure result" =>
                new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec(Copyable)),
            "closure nested in a generic argument" =>
                new NamedTypeSpec("Swift.Array",
                    new ClosureTypeSpec(new NamedTypeSpec(Copyable), new NamedTypeSpec("Swift.Int32"))),
            "tuple element" =>
                new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec(Copyable) }),
            _ => throw new System.ArgumentOutOfRangeException(nameof(shape), shape, "unhandled shape"),
        };

        Assert.False(WrapperValidation.SignatureReachesNonCopyable(spec, db, module));
    }

    #endregion

    #region ReachesNonCopyableThroughCopyingLane — the member-level refusal

    /// <summary>
    /// Adversarial pair, positive half: the refusal fires on the <c>~Copyable</c> shape and names
    /// the offending spelling, so the report row and the skip marker can attribute it.
    /// </summary>
    [Fact]
    public void ReachesNonCopyableThroughCopyingLane_ClosureCarryingOne_FiresAndNamesTheSpelling()
    {
        var (db, module) = Fixture();

        var signature = new TypeSpec?[]
        {
            new NamedTypeSpec("Swift.Int32"), // index 0 is the return slot
            new ClosureTypeSpec(new NamedTypeSpec(MoveOnly), new NamedTypeSpec("Swift.Int32")),
        };

        Assert.True(WrapperValidation.ReachesNonCopyableThroughCopyingLane(signature, db, module, out var offending));
        Assert.Contains("MoveOnlyResource", offending);
        Assert.Contains(offending, WrapperValidation.DescribeNonCopyableThroughCopyingLane(offending));
    }

    /// <summary>
    /// Adversarial pair, negative half: the copyable twin of the very same signature is untouched,
    /// with no offending spelling to report.
    /// </summary>
    [Fact]
    public void ReachesNonCopyableThroughCopyingLane_CopyableTwinOfTheSameSignature_DoesNotFire()
    {
        var (db, module) = Fixture();

        var signature = new TypeSpec?[]
        {
            new NamedTypeSpec("Swift.Int32"),
            new ClosureTypeSpec(new NamedTypeSpec(Copyable), new NamedTypeSpec("Swift.Int32")),
        };

        Assert.False(WrapperValidation.ReachesNonCopyableThroughCopyingLane(signature, db, module, out var offending));
        Assert.Equal(string.Empty, offending);
    }

    /// <summary>
    /// A directly named <c>~Copyable</c> parameter belongs to the pre-existing generic-slot gate,
    /// which reports it under its own reason. Claiming it here too would give one signature two
    /// competing skip reasons and move the skip-surface ledger for a shape whose disposition never
    /// changed.
    /// </summary>
    [Fact]
    public void ReachesNonCopyableThroughCopyingLane_DirectlyNamedNonCopyable_IsLeftToTheGenericSlotGate()
    {
        var (db, module) = Fixture();

        var signature = new TypeSpec?[] { new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec(MoveOnly) };

        Assert.False(WrapperValidation.ReachesNonCopyableThroughCopyingLane(signature, db, module, out _));
    }

    /// <summary>
    /// <c>Optional&lt;MoveOnly&gt;</c> is itself <c>~Copyable</c>, so it is also the generic-slot
    /// gate's, not this one's — the same one-reason-per-signature rule as the directly named case,
    /// applied to the shape that actually reaches it in the corpus.
    /// </summary>
    [Fact]
    public void ReachesNonCopyableThroughCopyingLane_OptionalOfNonCopyable_IsLeftToTheGenericSlotGate()
    {
        var (db, module) = Fixture();

        var signature = new TypeSpec?[]
        {
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(MoveOnly)),
        };

        Assert.False(WrapperValidation.ReachesNonCopyableThroughCopyingLane(signature, db, module, out _));
    }

    #endregion

    #region AsyncParameterCopiesNonCopyable — the staging-buffer lane

    /// <summary>
    /// A parameter has to outlive the suspension point, so the async wrapper duplicates every
    /// non-frozen parameter into a buffer the async holder owns while the caller keeps its own
    /// value — two owners of a value that permits one. There is no take to substitute on a
    /// <c>borrowing</c> parameter, so this lane refuses rather than takes.
    /// </summary>
    [Fact]
    public void AsyncParameterCopiesNonCopyable_NonFrozenNonCopyableParameter_ReturnsTrue()
    {
        var (db, module) = Fixture();
        var method = AsyncMethod(module, isAsync: true, parameter: new NamedTypeSpec(MoveOnly));

        Assert.True(WrapperValidation.AsyncParameterCopiesNonCopyable(method, db, out var offending));
        Assert.Contains("MoveOnlyResource", offending);
    }

    /// <summary>
    /// The copyable twin of the same async member keeps its staging buffer and its binding.
    /// </summary>
    [Fact]
    public void AsyncParameterCopiesNonCopyable_CopyableParameter_ReturnsFalse()
    {
        var (db, module) = Fixture();
        var method = AsyncMethod(module, isAsync: true, parameter: new NamedTypeSpec(Copyable));

        Assert.False(WrapperValidation.AsyncParameterCopiesNonCopyable(method, db, out _));
    }

    /// <summary>
    /// A synchronous member takes the same parameter through the supported directly-named lane —
    /// the <c>@_cdecl</c> wrapper borrows it in place — so the refusal must be keyed on the
    /// suspension point, not on the parameter type alone.
    /// </summary>
    [Fact]
    public void AsyncParameterCopiesNonCopyable_SynchronousMemberWithTheSameParameter_ReturnsFalse()
    {
        var (db, module) = Fixture();
        var method = AsyncMethod(module, isAsync: false, parameter: new NamedTypeSpec(MoveOnly));

        Assert.False(WrapperValidation.AsyncParameterCopiesNonCopyable(method, db, out _));
    }

    /// <summary>
    /// Index 0 of the signature is the return slot, and an async member RETURNING a non-copyable
    /// value is the lane that was fixed to take rather than refused. Sweeping the return slot into
    /// the parameter refusal would cost that member its binding after the take made it sound.
    /// </summary>
    [Fact]
    public void AsyncParameterCopiesNonCopyable_NonCopyableReturnSlot_ReturnsFalse()
    {
        var (db, module) = Fixture();
        var method = AsyncMethod(module, isAsync: true, parameter: new NamedTypeSpec("Swift.Int32"),
            returnSpec: new NamedTypeSpec(MoveOnly));

        Assert.False(WrapperValidation.AsyncParameterCopiesNonCopyable(method, db, out _));
    }

    /// <summary>
    /// The predicate the existential-bypass constructor factory declines on. That factory heap-
    /// allocates the result and pairs it with a free that unconditionally deinitializes, so it can
    /// hand C# neither a copy (there is no copy witness) nor a take (the free would destroy the
    /// taken-from value again) — it has to leave the constructor to ordinary emission. The decline
    /// reads this predicate, so the predicate is what pins it down; no corpus constructor reaches
    /// that factory with a <c>~Copyable</c> Self today, since a bare <c>any P</c> argument is not
    /// the containered, defaulted existential the factory claims.
    /// </summary>
    [Fact]
    public void IsNonCopyableStructParent_MoveOnlyStructParent_ReturnsTrue()
    {
        var (_, module) = Fixture();
        var moveOnly = module.Types.Single(t => t.Name == "MoveOnlyResource");

        Assert.True(WrapperValidation.IsNonCopyableStructParent(moveOnly));
    }

    [Fact]
    public void IsNonCopyableStructParent_CopyableStructParent_ReturnsFalse()
    {
        var (_, module) = Fixture();
        var copyable = module.Types.Single(t => t.Name == "CopyableResource");

        Assert.False(WrapperValidation.IsNonCopyableStructParent(copyable));
    }

    #endregion

    #region Fixture

    /// <summary>
    /// One non-copyable struct and its structural twin, declared the way the ABI describes them —
    /// <c>Escapable</c> listed and <c>Copyable</c> absent for the move-only one, both listed for the
    /// twin — and registered in the database with the flags ingestion derives. Both are non-frozen,
    /// which is what puts them on the async wrapper's staging path.
    /// </summary>
    private static (TypeDatabase Database, ModuleDecl Module) Fixture()
    {
        var moveOnly = StructNamed("MoveOnlyResource");
        Conform(moveOnly, "Swift.Escapable");

        var copyable = StructNamed("CopyableResource");
        Conform(copyable, "Swift.Copyable");
        Conform(copyable, "Swift.Escapable");

        var db = new TypeDatabase();
        Register(db, moveOnly, TypeRecordFlags.NonCopyable);
        Register(db, copyable, TypeRecordFlags.None);

        var module = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { moveOnly, copyable },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        return (db, module);
    }

    private static StructDecl StructNamed(string name) => new StructDecl
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}VN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = null,
        ModuleDecl = null,
        IsFrozen = false,
        MetadataAccessor = "",
    };

    private static void Conform(StructDecl structDecl, string protocolName)
        => structDecl.Conformances.Add(new TypeConformance(
            structDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            ProtocolConformanceDescriptor: string.Empty));

    private static void Register(TypeDatabase db, StructDecl structDecl, TypeRecordFlags flags)
        => db.AddOutOfModuleTypes(new[]
        {
            (structDecl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", structDecl.Name),
                SwiftTypeName = structDecl.SwiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Struct,
            }),
        });

    private static MethodDecl AsyncMethod(
        ModuleDecl module, bool isAsync, TypeSpec parameter, TypeSpec? returnSpec = null) => new()
        {
            Name = "peek",
            MangledName = "$s10TestModule4peek",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = returnSpec ?? new NamedTypeSpec("Swift.Void"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = module,
                },
                new()
                {
                    Name = "resource", PrivateName = "resource",
                    SwiftTypeSpec = parameter,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = module,
                },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = module,
            Throws = false,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false,
        };

    #endregion
}
