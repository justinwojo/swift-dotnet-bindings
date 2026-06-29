// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Cross-module nested-type rename propagation.
///
/// The dep module's <c>DependencyContainer</c> has a nested enum
/// <c>AlertType</c> and a property <c>alertType: AlertType</c>. PascalCased
/// the property is <c>AlertType</c>, colliding with the nested type. The
/// producer's rename pass renames the nested type to <c>AlertType2</c>;
/// the property keeps the name <c>AlertType</c>.
///
/// The cross-module path: SwiftBindingsTestLib consumes
/// <c>DependencyContainer.AlertType</c> in parameters, returns, and
/// stored-property positions. Before the dep-loading rename pass was wired
/// into the generator, the consumer emitted bare <c>AlertType</c> which C#
/// resolved to the property — CS0426 at the type-reference sites.
///
/// These tests exercise every consumer-side reference shape (return, param,
/// stored property) end-to-end so a regression on the rename propagation
/// trips the runtime gate as well as the compile gate.
/// </summary>
public class NestedTypeRenameTests : TestBase
{
    public NestedTypeRenameTests(TestResults results) : base(results) { }

    public void TestGetDependencyContainerAlertType_RoundTripsRenamedEnum()
    {
        // Consumer-side factory returns a DependencyContainer with alertType = .warning.
        using var container = TestLibFunctions.MakeDependencyContainer(
            "Container1", DependencyContainer.AlertType2.Warning);

        // Consumer-side function returns DependencyContainer.AlertType — locks
        // that the cross-module return reference resolves to AlertType2,
        // not the AlertType property.
        using var alert = TestLibFunctions.GetDependencyContainerAlertType(container);

        AssertTrue(alert.Equals(DependencyContainer.AlertType2.Warning),
            "Round-tripped alert matches .warning");
    }

    public void TestMakeDependencyContainer_AcceptsRenamedEnumParameter()
    {
        // Locks the cross-module parameter position: consumer function
        // takes DependencyContainer.AlertType as an argument.
        using var container = TestLibFunctions.MakeDependencyContainer(
            "Container2", DependencyContainer.AlertType2.Critical);

        // Read the dep's property back through the dep-side accessor.
        using var alert = container.AlertType;
        AssertTrue(alert.Equals(DependencyContainer.AlertType2.Critical),
            "Container preserved .critical alert across constructor");
    }

    public void TestDependencyContainerHolder_StoredPropertyIsRenamedEnum()
    {
        // Locks the cross-module stored-property position: a struct in the
        // consumer module has a stored property whose type is the renamed
        // cross-module nested enum.
        using var holder = TestLibFunctions.MakeDependencyContainerHolder(
            "HolderA", DependencyContainer.AlertType2.Info);

        AssertEqual("HolderA", holder.Label.ToString(), "Label preserved");

        using var alert = holder.Alert;
        AssertTrue(alert.Equals(DependencyContainer.AlertType2.Info),
            "Holder.Alert property reads back .info");
    }

    public void TestDependencyContainer_AlertTypeProperty_PreservesPascalCasedName()
    {
        // Locks the property side of the rename: the producer kept the
        // C# property name "AlertType" (the nested type took the rename).
        // If the property accidentally got renamed too, this would fail to
        // compile rather than fail at runtime — but locking the access
        // pattern here documents the contract.
        using var container = TestLibFunctions.MakeDependencyContainer(
            "Container3", DependencyContainer.AlertType2.Info);

        using var alert = container.AlertType;
        AssertTrue(alert.Equals(DependencyContainer.AlertType2.Info),
            "DependencyContainer.AlertType property reads .info");
    }
}
