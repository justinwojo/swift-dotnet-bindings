// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the AppIntents 0.12.0 _SBW_PG_* conformance bug.
///
/// The fixture in <c>Generics/DependentMemberSameTypeConstraint.swift</c> declares
/// <c>extension DependentMemberHost where Value.ValueType == Bool { var displayName: String }</c>.
/// Pre-fix, the open-generic protocol-group emitter synthesized an
/// unsatisfiable <c>extension DependentMemberHost: _SBW_PG_… {}</c> to project
/// this property, which failed the Swift wrapper compile because the property
/// only exists when <c>Value.ValueType == Bool</c>. The fix gates these
/// properties in <c>MemberEmissionValidator.CanEmitProperty</c>: dependent-member
/// same-type constraints (constraints landing in
/// <c>GenericArgumentDecl.AssosiatedTypeConformances</c>) are dropped from
/// emission, since the closed-extension path cannot re-surface them either.
/// </summary>
public class DependentMemberSameTypeConstraintTests : TestBase
{
    public DependentMemberSameTypeConstraintTests(TestResults results) : base(results) { }

    public void TestHostTypeIsEmittedAndLoadable()
    {
        // Pre-fix the Swift wrapper file did not compile (synthesized
        // `extension DependentMemberHost: _SBW_PG_… {}` for a property that
        // only exists when `Value.ValueType == Bool` was unsatisfiable), so the
        // binding assembly never linked successfully and `typeof(...)` would
        // fail at JIT time. The reflection load proves the host type compiles
        // cleanly post-fix.
        //
        // Note: this fixture deliberately exercises only assembly-load /
        // reflection. The generated `DependentMemberHost(TValue)` constructor
        // is marked `[Obsolete(..., DiagnosticId = "SB0001")]` — a direct
        // Swift ABI P/Invoke without an @_cdecl wrapper or native thunk, so
        // any runtime invocation is unsafe and SIGSEGVs in practice. That
        // limitation is a pre-existing generator gap for generic-struct
        // constructors with associated-type protocol constraints and is
        // orthogonal to the dependent-member same-type fix this fixture
        // exists to cover.
        var hostType = typeof(DependentMemberHost<BoolCarrier>);
        AssertNotNull(hostType, "DependentMemberHost<BoolCarrier> is a loadable type");

        var wrappedProperty = hostType.GetProperty(
            "Wrapped",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);
        AssertNotNull(wrappedProperty,
            "Unconstrained `wrapped` accessor must still surface as `Wrapped` — " +
            "the dependent-member gate targets only the constrained property.");

        TestLogger.Info(
            "DependentMemberHost type compiled and loaded cleanly; unconstrained " +
            "Wrapped accessor preserved.");
    }

    public void TestDependentMemberConstrainedPropertyIsNotEmitted()
    {
        // `displayName` is the constrained-extension property — it MUST NOT be
        // emitted as a member of the host type. Assert via reflection so the
        // test verifies absence rather than runtime dispatch (a closed-extension
        // emit was never attempted; the property is genuinely dropped, not
        // silently stubbed).
        var hostType = typeof(DependentMemberHost<BoolCarrier>);

        var displayNameProperty = hostType.GetProperty(
            "DisplayName",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static);

        AssertTrue(displayNameProperty is null,
            "DisplayName must NOT be emitted on DependentMemberHost<BoolCarrier> — " +
            "the dependent-member same-type constraint makes it unsatisfiable at the " +
            "open-generic level and unreachable from a closed extension.");

        TestLogger.Info(
            "DependentMemberHost.DisplayName correctly absent — dependent-member same-type " +
            "constraint suppressed at the open-generic emission level.");
    }
}
