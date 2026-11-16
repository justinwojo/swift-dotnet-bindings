// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for generic methods of generic types called directly (<c>ParentContextHolder</c>,
/// <c>ParentContextPair</c>, <c>ParentContextChoice</c>, <c>ParentContextBox</c>). Swift takes only the generic
/// arguments it cannot recover from self or the parent's metadata, so V's metadata follows the
/// parent's metadata on a struct or enum and comes first on a class. The import once passed the
/// parent's other generic arguments and witness tables as well, which put the class's own metadata
/// or another parameter's metadata where Swift reads V's. Each call reads V's size from its
/// metadata, so a misplaced argument shows up as a wrong size; a bridge carries these members on
/// iOS, Mac Catalyst and tvOS and they are guarded on macOS.
/// </summary>
public class GenericParentContextArgumentTests : TestBase
{
    public GenericParentContextArgumentTests(TestResults results) : base(results) { }

    private const SwiftTransitionFacts MemberShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    // ParentContextWide is two words, ParentContextTag one: a size read from the wrong metadata differs.
    private const int WideSize = 16;

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever a bridge carries the member or the
    /// engine runs its direct transition, and must refuse before Swift runs everywhere else.
    /// </summary>
    private void Run(string member, Action call)
    {
        if (ImportRoutes.BridgesRouted || SwiftTransitionGuard.IsSupported(MemberShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    public void TestStructMethodReadsItsOwnGenericArgument()
    {
        using var holder = new ParentContextHolder<ParentContextTag>(seed: 3);
        using var wide = new ParentContextWide(low: 1, high: 2);
        Run("Store", () =>
        {
            AssertTrue(holder.Store(wide), "a seeded holder stores");
            AssertTrue(holder.Store(7L), "a seeded holder stores a primitive");
        });
        Run("SizeOf", () =>
        {
            for (var i = 0; i < 3; i++)
            {
                AssertEqual((nint)(WideSize + 3), holder.SizeOf(wide), "V's size comes from V's metadata");
                AssertEqual((nint)(8 + 3), holder.SizeOf(7L), "an Int64 V is one word");
            }
        });
    }

    public void TestTwoParameterStructPassesNothingForItsSecondParameter()
    {
        using var pair = new ParentContextPair<ParentContextTag, ParentContextTag>(seed: 5);
        using var wide = new ParentContextWide(low: 1, high: 2);
        Run("SizeOf", () =>
        {
            for (var i = 0; i < 3; i++)
                AssertEqual((nint)(WideSize + 5), pair.SizeOf(wide), "V's metadata follows the pair's own, not B's");
        });
    }

    public void TestEnumMethodReadsItsOwnGenericArgument()
    {
        using var choice = ParentContextChoice<ParentContextTag>.Make(seed: 4);
        using var wide = new ParentContextWide(low: 1, high: 2);
        Run("Store", () => AssertTrue(choice.Store(wide), "a seeded choice stores"));
        Run("SizeOf", () =>
        {
            for (var i = 0; i < 3; i++)
                AssertEqual((nint)(WideSize + 4), choice.SizeOf(wide), "V's size comes from V's metadata");
        });
    }

    public void TestClassMethodTakesOnlyItsOwnGenericArgument()
    {
        using var box = new ParentContextBox<ParentContextTag>(seed: 6);
        using var wide = new ParentContextWide(low: 1, high: 2);
        Run("Store", () => AssertTrue(box.Store(wide), "a seeded box stores"));
        Run("SizeOf", () =>
        {
            for (var i = 0; i < 3; i++)
            {
                AssertEqual((nint)(WideSize + 6), box.SizeOf(wide), "V's metadata is the class method's first metadata argument");
                AssertEqual((nint)(8 + 6), box.SizeOf(7L), "an Int64 V is one word");
            }
        });
    }
}
