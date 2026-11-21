// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLibDependency;
using Type = System.Type;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// End-to-end coverage for load-safety P2: a member the generating SDK exports without a floor that
/// an older OS does not ship. The harness generates the dependency module's binding against an OS
/// symbol inventory that omits the symbols of <c>InventoryProbe.absentOnInventoryOs</c>,
/// <c>InventoryProbe.absentMethodOnInventoryOs()</c> and the whole <c>InventoryAbsentValue</c> type,
/// at a floor of 99.0, and the symbols of <c>InventoryRawProbe.init(rawValue:)</c>, the
/// <c>(Self, Self)</c> <c>==</c> of <c>InventoryEqualityProbe</c> and the <c>==</c> of
/// <c>InventoryEquatableValue</c>, which every equality and raw-value route of the binding reaches
/// whichever public member it starts from. The generator must infer that floor for exactly those declarations (no Swift
/// <c>@available</c> names it), the binding must throw <see cref="PlatformNotSupportedException"/>
/// before the call reaches native code, and the siblings the inventory lists must work.
///
/// Members are reached by reflection, as in <see cref="RuntimeAvailabilityGuardTests"/>, so the 99.0
/// <c>[SupportedOSPlatform]</c> does not raise CA1416 here; a thrown guard surfaces as the
/// <see cref="TargetInvocationException.InnerException"/>.
/// </summary>
public class OsSymbolInventoryFloorTests : TestBase
{
    public OsSymbolInventoryFloorTests(TestResults results) : base(results) { }

    public void TestListedMembers_Run()
    {
        using var probe = new InventoryProbe(10);
        AssertEqual(11L, (long)probe.PresentOnInventoryOs, "presentOnInventoryOs is listed by the inventory and must run.");
        var method = Method(typeof(InventoryProbe), "PresentMethod");
        AssertEqual(20L, ToLong(method.Invoke(probe, null)), "presentMethod() is listed by the inventory and must run.");
        AssertFalse(HasInferredFloor(typeof(InventoryProbe)), "InventoryProbe itself ships on the inventory's OS and must carry no inferred floor.");
        AssertFalse(HasInferredFloor(method), "presentMethod() must carry no inferred floor.");
    }

    public void TestAbsentProperty_InferredFloorThrows()
    {
        using var probe = new InventoryProbe(10);
        var property = typeof(InventoryProbe).GetProperty("AbsentOnInventoryOs", BindingFlags.Public | BindingFlags.Instance);
        AssertTrue(property is not null, "InventoryProbe.AbsentOnInventoryOs must be bound.");
        AssertTrue(HasInferredFloor(property!), "AbsentOnInventoryOs must carry the inferred 99.0 floor.");
        AssertGuardThrows(() => property!.GetValue(probe), "InventoryProbe.AbsentOnInventoryOs");
    }

    public void TestAbsentMethod_InferredFloorThrows()
    {
        using var probe = new InventoryProbe(10);
        // The binding names a parameterless value-returning method Get… (absentMethodOnInventoryOs() → GetAbsentMethodOnInventoryOs).
        var method = Method(typeof(InventoryProbe), "GetAbsentMethodOnInventoryOs");
        AssertTrue(HasInferredFloor(method), "absentMethodOnInventoryOs() must carry the inferred 99.0 floor.");
        AssertGuardThrows(() => method.Invoke(probe, null), "InventoryProbe.GetAbsentMethodOnInventoryOs");
        // The sibling still works after the refused call.
        AssertEqual(11L, (long)probe.PresentOnInventoryOs, "a refused call must leave the receiver usable.");
    }

    public void TestAbsentType_InferredFloorThrows()
    {
        AssertTrue(HasInferredFloor(typeof(InventoryAbsentValue)), "InventoryAbsentValue must carry the inferred 99.0 floor.");
        var ctor = typeof(InventoryAbsentValue).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 1);
        var seedType = ctor.GetParameters()[0].ParameterType;
        // Box each branch on its own: a single conditional would promote every arm to long.
        object seed = seedType == typeof(nint) ? (object)(nint)3 : seedType == typeof(int) ? (object)3 : (object)3L;
        AssertGuardThrows(() => ctor.Invoke(new[] { seed }), "InventoryAbsentValue.init(seed:)");
    }

    public void TestRawValueFactory_InferredFloorThrows_CasesAndRawValuesWork()
    {
        // The inventory omits only init(rawValue:): FromRawValue must refuse, the cases must not.
        var fromRawValue = typeof(InventoryRawProbe).GetMethod("FromRawValue", BindingFlags.Public | BindingFlags.Static);
        AssertTrue(fromRawValue is not null, "InventoryRawProbe.FromRawValue must be bound.");
        AssertTrue(HasInferredFloor(fromRawValue!), "FromRawValue must carry init(rawValue:)'s inferred 99.0 floor.");
        AssertFalse(HasInferredFloor(typeof(InventoryRawProbe)), "InventoryRawProbe itself ships on the inventory's OS.");
        AssertGuardThrows(() => fromRawValue!.Invoke(null, new object?[] { "LOW" }), "InventoryRawProbe.FromRawValue");
        AssertEqual("LOW", InventoryRawProbe.Low.RawValue, "a case's raw value must still read after the refused factory.");
        AssertEqual("HIGH", InventoryRawProbe.High.RawValue, "every case must keep working.");
        AssertTrue(InventoryRawProbe.Low.Equals(InventoryRawProbe.Low), "the synthesized == ships, so equality must run.");
        AssertFalse(InventoryRawProbe.Low.Equals(InventoryRawProbe.High), "equality must compare the cases.");
    }

    public void TestEnumEquality_RefusesAtTheFloorOfTheOperatorItCalls()
    {
        // The (Self, Self) == is omitted; the (Self, Int) == declared before it ships. Every equality
        // route of the binding calls the omitted one, so every route must refuse.
        using var tagged = InventoryEqualityProbe.Tagged(5);
        using var other = InventoryEqualityProbe.Tagged(5);
        AssertEqual(InventoryEqualityProbe.CaseTag.Tagged, tagged.Tag, "the cases ship and must construct.");
        AssertEqual(InventoryEqualityProbe.CaseTag.Plain, InventoryEqualityProbe.Plain.Tag, "the payload-free case must load.");
        AssertEqualityRefuses(typeof(InventoryEqualityProbe), tagged, other, "InventoryEqualityProbe");
        AssertEqual(InventoryEqualityProbe.CaseTag.Tagged, tagged.Tag, "a refused comparison must leave the value usable.");
    }

    public void TestStructEquality_RefusesAtTheOperatorsFloor()
    {
        using var value = new InventoryEquatableValue(4);
        using var other = new InventoryEquatableValue(4);
        AssertEqual(4L, (long)value.Seed, "the struct's other members ship and must run.");
        AssertFalse(HasInferredFloor(typeof(InventoryEquatableValue)), "InventoryEquatableValue itself ships on the inventory's OS.");
        AssertEqualityRefuses(typeof(InventoryEquatableValue), value, other, "InventoryEquatableValue");
        AssertEqual(4L, (long)other.Seed, "a refused comparison must leave both operands usable.");
    }

    /// <summary>Equals(object), Equals(T), == and != each refuse, and the ones a consumer calls carry the floor.</summary>
    private void AssertEqualityRefuses(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, object left, object right, string label)
    {
        var equalsObject = type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance, new[] { typeof(object) });
        var equalsTyped = type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance, new[] { type });
        var opEquality = type.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static, new[] { type, type });
        var opInequality = type.GetMethod("op_Inequality", BindingFlags.Public | BindingFlags.Static, new[] { type, type });
        AssertTrue(equalsObject is not null && equalsTyped is not null && opEquality is not null && opInequality is not null,
            $"{label} must bind Equals(object), Equals({type.Name}), == and !=.");
        foreach (var member in new MemberInfo[] { equalsTyped!, opEquality!, opInequality! })
            AssertTrue(HasInferredFloor(member), $"{label}.{member.Name} must carry the == operator's inferred 99.0 floor.");
        AssertGuardThrows(() => equalsObject!.Invoke(left, new[] { right }), $"{label}.Equals(object)");
        AssertGuardThrows(() => equalsTyped!.Invoke(left, new[] { right }), $"{label}.Equals({type.Name})");
        AssertGuardThrows(() => opEquality!.Invoke(null, new[] { left, right }), $"{label} ==");
        AssertGuardThrows(() => opInequality!.Invoke(null, new[] { left, right }), $"{label} !=");
    }

    private static long ToLong(object? value) => value switch
    {
        nint n => n,
        int i => i,
        long l => l,
        _ => throw new AssertionException($"unexpected integer result {value?.GetType().Name ?? "null"}"),
    };

    private static bool HasInferredFloor(MemberInfo member) =>
        member.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false)
            .Any(a => a.PlatformName.EndsWith("99.0", StringComparison.Ordinal));

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, string name) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == name && m.GetParameters().Length == 0);

    private void AssertGuardThrows(Func<object?> invoke, string memberLabel)
    {
        try
        {
            invoke();
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException;
            TestLogger.Info($"{memberLabel} threw {inner?.GetType().Name}: {inner?.Message}");
            AssertTrue(inner is PlatformNotSupportedException,
                $"{memberLabel} must throw PlatformNotSupportedException from its inferred-floor guard; got {inner?.GetType().Name ?? "null"}.");
            return;
        }
        throw new AssertionException(
            $"Expected {memberLabel} to throw PlatformNotSupportedException from the floor inferred from the OS symbol " +
            "inventory, but it returned normally: the floor was not inferred or its guard did not fire.");
    }
}
