// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Bundle 05 #2 (unconditional IEquatable on conditional Swift generic)
/// regression coverage. The Swift fixture
/// <see cref="Bundle05CondEqBox{Item}"/> declares
/// <c>extension Bundle05CondEqBox: Equatable where Item: Equatable {}</c>
/// — the conformance is gated on the type parameter satisfying
/// <c>Equatable</c>. Pre-fix, the C# emit projected this as an
/// unconditional <c>IEquatable&lt;Bundle05CondEqBox&lt;TItem&gt;&gt;</c>
/// interface plus typed equality methods, without mirroring the
/// <c>Item: Equatable</c> constraint on <c>TItem</c>. That allowed
/// consumer code to dispatch <c>Bundle05CondEqBox&lt;NotEquatable&gt;.Equals(...)</c>
/// to a Swift specialization keyed on a missing witness table —
/// runtime trap.
///
/// The fix is conservative: when the parser cannot prove every generic
/// parameter carries a constraint that transitively refines the
/// conformance protocol, the typed equality surface is dropped. The
/// type still derives reference equality from <see cref="object"/> via
/// inherited <c>Equals(object?)</c>, which boxes-and-compares but never
/// traps. This test asserts the typed surface is absent.
/// </summary>
public class Bundle05ConditionalEquatableTests : TestBase
{
    public Bundle05ConditionalEquatableTests(TestResults results) : base(results) { }

    /// <summary>
    /// The generated <see cref="Bundle05CondEqBox{TItem}"/> class must NOT
    /// declare <c>IEquatable&lt;Bundle05CondEqBox&lt;TItem&gt;&gt;</c> in
    /// its interface set. Reflection over the open-generic type definition
    /// asserts no <c>IEquatable&lt;&gt;</c> closes back over the same type —
    /// the canonical signature of the over-broad emission.
    /// </summary>
    public void TestConditionalEquatable_TypedEqualitySurfaceDropped()
    {
        var openType = typeof(Bundle05CondEqBox<>);
        var ifaces = openType.GetInterfaces();
        TestLogger.Info($"Bundle05CondEqBox<> interfaces: [{string.Join(", ", ifaces.Select(i => i.Name))}]");

        var hasTypedIEquatable = ifaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>)
            && i.GetGenericArguments()[0].IsGenericType
            && i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Bundle05CondEqBox<>));

        AssertFalse(hasTypedIEquatable,
            "Bundle 05 #2: Bundle05CondEqBox<TItem> must NOT implement " +
            "IEquatable<Bundle05CondEqBox<TItem>> because the Swift Equatable " +
            "conformance is conditional on `Item: Equatable` and the C# generic " +
            "parameter set carries no such constraint. Emitting the typed surface " +
            "unconditionally would let `Bundle05CondEqBox<NotEquatable>.Equals(other)` " +
            "compile and trap at runtime against a missing Swift witness table.");
    }

    /// <summary>
    /// Symmetric assertion at the method level: the generated class must
    /// not surface a strongly-typed <c>Equals(Bundle05CondEqBox&lt;TItem&gt;?)</c>
    /// override either. Reference equality through
    /// <see cref="object.Equals(object?)"/> is fine — that path doesn't
    /// re-enter Swift's witness-table machinery.
    /// </summary>
    public void TestConditionalEquatable_TypedEqualsMethodAbsent()
    {
        var openType = typeof(Bundle05CondEqBox<>);
        var typedEqualsMethods = openType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Equals" && m.GetParameters().Length == 1)
            .Where(m =>
            {
                var p = m.GetParameters()[0].ParameterType;
                // Strip nullable wrapping.
                var t = Nullable.GetUnderlyingType(p) ?? p;
                return t.IsGenericType
                    && t.GetGenericTypeDefinition() == typeof(Bundle05CondEqBox<>);
            })
            .ToArray();

        TestLogger.Info($"Bundle05CondEqBox<> declared typed Equals overloads: {typedEqualsMethods.Length}");
        AssertEqual(0, typedEqualsMethods.Length,
            "Bundle 05 #2: Bundle05CondEqBox<TItem> must not declare a typed " +
            "Equals(Bundle05CondEqBox<TItem>?) override when the Swift Equatable " +
            "conformance is conditional. Inherited Equals(object?) is the safe path.");
    }

    /// <summary>
    /// Sanity check the closed-generic round-trip still compiles and the
    /// instance is reachable. Without this, a future regression that
    /// drops the entire type would silently pass the negative tests
    /// above (no IEquatable on a non-existent type).
    /// </summary>
    public void TestConditionalEquatable_FactoryProducesInstance()
    {
        using var box = TestLibFunctions.MakeBundle05CondEqBoxInt(42);
        AssertNotNull(box, "Factory must produce a non-null Bundle05CondEqBox<Int32> instance.");
    }
}
