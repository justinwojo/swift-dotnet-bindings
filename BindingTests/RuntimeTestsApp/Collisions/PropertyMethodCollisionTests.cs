// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Property-vs-method rename: C# forbids a property and a method sharing a name (CS0102).
/// <c>PropertyMethodCollider</c> has a stored property <c>conflict</c> AND a method
/// <c>conflict(_:)</c>, so the method is renamed away from <c>Conflict</c> to <c>ConflictMethod</c>.
/// A sibling already spelled <c>conflictMethod(_:)</c> then numerically collides with that rename, so
/// the dedup disambiguates it as <c>ConflictMethod2</c>. The root cause is that the dedup keys
/// must observe BOTH the property rename and the numeric suffix, or the two methods emit under the
/// same C# name (CS0111) and bind to the wrong Swift body.
///
/// Mapping verified against the generated P/Invoke entry points:
///   - <c>ConflictMethod(x)</c>  -> Swift <c>conflict(_:)</c>       -> conflict + x
///   - <c>ConflictMethod2(x)</c> -> Swift <c>conflictMethod(_:)</c> -> conflict * 10 + x
/// Distinct return shapes (a + x vs a*10 + x) make a wrong-slot binding observable.
///
/// <c>PropertyMethodControl</c> is the control: same two method names, NO colliding property. Without
/// the property the rename never runs, so the methods keep their natural names <c>Conflict(x)</c> and
/// <c>ConflictMethod(x)</c> — pinning that the rename is driven by the sibling property, not the
/// method names alone.
/// </summary>
public class PropertyMethodCollisionTests : TestBase
{
    public PropertyMethodCollisionTests(TestResults results) : base(results) { }

    #region Collider — property forces the rename + numeric suffix

    public void TestPropertyGetterUnaffected()
    {
        using var c = new PropertyMethodCollider(5);
        AssertEqual(5, c.Conflict, "stored property `conflict` projects to the `Conflict` getter");
    }

    public void TestRenamedMethodBindsToConflictBody()
    {
        using var c = new PropertyMethodCollider(5);
        // ConflictMethod is the renamed `conflict(_:)` (conflict + x), NOT the property and NOT conflictMethod.
        AssertEqual(8, c.ConflictMethod(3), "ConflictMethod(3) -> Swift conflict(_:) = 5 + 3");
    }

    public void TestSuffixedMethodBindsToConflictMethodBody()
    {
        using var c = new PropertyMethodCollider(5);
        // ConflictMethod2 is the numeric-suffixed `conflictMethod(_:)` (conflict*10 + x).
        AssertEqual(53, c.ConflictMethod2(3), "ConflictMethod2(3) -> Swift conflictMethod(_:) = 5*10 + 3");
    }

    public void TestBothOverloadsReachDistinctBodies()
    {
        using var c = new PropertyMethodCollider(2);
        // No wrong-slot aliasing: the two renamed overloads return their own distinct bodies.
        int a = c.ConflictMethod(4);    // conflict + x  = 2 + 4  = 6
        int b = c.ConflictMethod2(4);   // conflict*10+x = 20 + 4 = 24
        AssertEqual(6, a, "ConflictMethod -> conflict(_:)");
        AssertEqual(24, b, "ConflictMethod2 -> conflictMethod(_:)");
        AssertTrue(a != b, "the two renamed overloads bind to DIFFERENT Swift bodies, not the same slot");
    }

    #endregion

    #region Control — no property, so no rename

    public void TestControlKeepsNaturalNames()
    {
        using var ctrl = new PropertyMethodControl();
        // Without the colliding property the method keeps its natural `Conflict` name (x + 1)...
        AssertEqual(11, ctrl.Conflict(10), "control Conflict(10) -> conflict(_:) = 10 + 1 (no rename)");
        // ...and the sibling keeps its natural `ConflictMethod` name (x + 2), with no numeric suffix.
        AssertEqual(12, ctrl.ConflictMethod(10), "control ConflictMethod(10) -> conflictMethod(_:) = 10 + 2");
    }

    #endregion

    #region Emitted shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(PropertyMethodCollider))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(PropertyMethodControl))]
    public void TestEmittedNamesReflectTheRename()
    {
        // Collider: property `Conflict`, plus the two renamed/suffixed methods — and NO method named `Conflict`.
        AssertNotNull(typeof(PropertyMethodCollider).GetProperty("Conflict"),
            "Collider exposes the Conflict property");
        AssertNotNull(typeof(PropertyMethodCollider).GetMethod("ConflictMethod", new[] { typeof(int) }),
            "Collider exposes ConflictMethod(int) (renamed conflict(_:))");
        AssertNotNull(typeof(PropertyMethodCollider).GetMethod("ConflictMethod2", new[] { typeof(int) }),
            "Collider exposes ConflictMethod2(int) (suffixed conflictMethod(_:))");
        AssertNull(typeof(PropertyMethodCollider).GetMethod("Conflict", new[] { typeof(int) }),
            "Collider has NO method named Conflict — it was renamed to avoid the property (CS0102)");

        // Control: natural names, no property, no numeric suffix.
        AssertNotNull(typeof(PropertyMethodControl).GetMethod("Conflict", new[] { typeof(int) }),
            "Control keeps the natural Conflict(int) method (no property to collide with)");
        AssertNotNull(typeof(PropertyMethodControl).GetMethod("ConflictMethod", new[] { typeof(int) }),
            "Control keeps the natural ConflictMethod(int) method (no numeric suffix)");
        AssertNull(typeof(PropertyMethodControl).GetMethod("ConflictMethod2", new[] { typeof(int) }),
            "Control has NO ConflictMethod2 — without the rename there is no numeric collision");
    }

    #endregion
}
