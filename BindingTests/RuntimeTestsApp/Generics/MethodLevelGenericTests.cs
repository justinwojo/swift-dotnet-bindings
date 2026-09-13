// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using DependencyStringCollection = SwiftBindingsTestLibDependency.DependencyStringCollection;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for method-level generic parameters bridged via implicit existential opening.
/// These methods have their own generic type parameters (e.g., func foo&lt;T: Describable&gt;)
/// and are emitted with @_cdecl wrappers that load existential containers.
/// </summary>
public class MethodLevelGenericTests : TestBase
{
    public MethodLevelGenericTests(TestResults results) : base(results) { }

    public void TestGenericMethodHost_VoidGenericMethod()
    {
        var host = new GenericMethodHost(label: "test");
        var item = new SimpleDescribable(description: "hello");
        // Should not crash — void method with generic param
        host.PrintDescription(item);
        TestLogger.Info("GenericMethodHost.PrintDescription completed without crash");
    }

    public void TestGenericMethodHost_ReturnsString()
    {
        var host = new GenericMethodHost(label: "host");
        var item = new SimpleDescribable(description: "world");
        var result = host.GetDescription(item);
        AssertEqual("host: world", result, "GetDescription<T: Describable>");
        TestLogger.Info($"GenericMethodHost.GetDescription = {result}");
    }

    public void TestGenericMethodHost_StaticMethod()
    {
        var item = new SimpleDescribable(description: "static-test");
        var result = GenericMethodHost.StaticDescribe(item);
        AssertEqual("static: static-test", result, "StaticDescribe<T: Describable>");
        TestLogger.Info($"GenericMethodHost.StaticDescribe = {result}");
    }

    public void TestGenericMethodHost_MixedParams()
    {
        var host = new GenericMethodHost(label: "tagged");
        var item = new SimpleDescribable(description: "item");
        var result = host.DescribeWithTag(item, tag: 42);
        AssertEqual("[42] tagged: item", result, "DescribeWithTag<T>(_, tag:)");
        TestLogger.Info($"GenericMethodHost.DescribeWithTag = {result}");
    }

    /// Calls the fully generic arm of `SimpleRowAdapter.layoutedAdapter&lt;T&gt;` at a type
    /// argument the concrete-specialization engine declines to specialize, so there is no
    /// closed overload to fall back on. This is the shape that aborts under Mono full-AOT
    /// when the binding dispatches directly on the Swift ABI thunk.
    public void TestLayoutedAdapter_GenericArm_NoClosedSpecialization()
    {
        var adapter = Functions.MakeSimpleRowAdapter();
        var layout = new FrozenRowLayout(columnCount: 7);
        var result = adapter.LayoutedAdapter(layout);
        AssertEqual("adapted:7", result, "layoutedAdapter<T: RowLayout>(from:) generic arm");
        TestLogger.Info($"SimpleRowAdapter.LayoutedAdapter<FrozenRowLayout> = {result}");
    }

    /// Adversarial: a type argument that satisfies the binding's managed constraint but whose
    /// Swift metadata carries no `RowLayout` conformance. The opening wrapper casts the metadata
    /// before it reads the payload, so the call must come back as a typed managed exception
    /// rather than reinterpreting the bytes at the wrong type.
    public void TestLayoutedAdapter_NonConformingTypeArgument_Refused()
    {
        var adapter = Functions.MakeSimpleRowAdapter();
        var impostor = new NotARowLayout(columnCount: 3);
        try
        {
            var result = adapter.LayoutedAdapter(impostor);
            AssertTrue(false, $"expected a refusal for a non-conforming type argument, got '{result}'");
        }
        catch (Swift.Runtime.SwiftRuntimeException ex)
        {
            AssertTrue(ex.Message.Contains("NotARowLayout"),
                $"refusal names the offending type argument (was: {ex.Message})");
            // Module-qualified, and paired with the "does not conform" wording: a bare "RowLayout"
            // is already a substring of the impostor's own name, so it would pass on any message
            // that merely mentions the type argument.
            AssertTrue(ex.Message.Contains("does not conform to Swift protocol 'SwiftBindingsTestLib.RowLayout'"),
                $"refusal names the unsatisfied constraint (was: {ex.Message})");
            TestLogger.Info($"SimpleRowAdapter.LayoutedAdapter<NotARowLayout> refused: {ex.Message}");
        }
    }

    public void TestParameterizedCollection_GenericArm_SameAndDependencyCarriers()
    {
        var host = new CollectionHost(separator: "|");
        var local = new MlgStringCollection(first: "red", second: "green", third: "blue");
        var dependency = new DependencyStringCollection(first: "left", second: "middle", third: "right");

        Expression<Func<string>> localCall = () => host.JoinItems<MlgStringCollection>(local);
        AssertExplicitGenericArm(localCall, typeof(MlgStringCollection), typeof(CollectionHost),
            nameof(CollectionHost.JoinItems), expectClosedSibling: true);
        Expression<Func<string>> dependencyCall = () => host.JoinItems<DependencyStringCollection>(dependency);
        AssertExplicitGenericArm(dependencyCall, typeof(DependencyStringCollection), typeof(CollectionHost),
            nameof(CollectionHost.JoinItems), expectClosedSibling: false);

        AssertEqual("red|green|blue", host.JoinItems<MlgStringCollection>(local),
            "Collection<String> same-module explicit generic arm");
        AssertEqual("left|middle|right", host.JoinItems<DependencyStringCollection>(dependency),
            "Collection<String> dependency-module explicit generic arm");
    }

    public void TestParameterizedCollection_MismatchedElementMetadata_Refused()
    {
        var host = new CollectionHost(separator: ",");
        var values = new MlgIntCollection(first: 3, second: 7);

        try
        {
            _ = host.JoinItems<MlgIntCollection>(values);
            AssertTrue(false, "expected Collection<Int32> metadata to be refused by Collection<String>");
        }
        catch (SwiftRuntimeException ex)
        {
            AssertTrue(ex.Message.Contains("MlgIntCollection"),
                $"parameterized refusal names the carrier (was: {ex.Message})");
            AssertTrue(ex.Message.Contains("Swift.Collection") && ex.Message.Contains("Swift.String"),
                $"parameterized refusal names the full requirement (was: {ex.Message})");
        }
    }

    public void TestAssociatedProtocolCarrier_GenericArmAndRefusal()
    {
        var sink = new HashSink();
        var good = new MlgHashSequence(first: 11, second: 7, third: 3);
        var bad = new MlgNonHashSequence(first: 5, second: 9);

        Expression<Func<nint>> genericCall = () => sink.SumHashes<MlgHashSequence>(good);
        AssertExplicitGenericArm(genericCall, typeof(MlgHashSequence), typeof(HashSink),
            nameof(HashSink.SumHashes), expectClosedSibling: true);

        AssertEqual((nint)21, sink.SumHashes<MlgHashSequence>(good),
            "Sequence.Element: HashLike explicit generic arm");
        try
        {
            _ = sink.SumHashes<MlgNonHashSequence>(bad);
            AssertTrue(false, "expected non-HashLike Element metadata to be refused");
        }
        catch (SwiftRuntimeException ex)
        {
            AssertTrue(ex.Message.Contains("MlgNonHashSequence") && ex.Message.Contains("Element"),
                $"associated-type refusal identifies the failed conditional requirement (was: {ex.Message})");
        }
    }

    public void TestAssociatedProtocolCarrier_ThrowAndRefusalStayDistinct()
    {
        var sink = new HashSink();
        var success = new MlgHashSequence(first: 8, second: 5, third: 2);
        var throwing = new MlgHashSequence(first: -20, second: 3, third: 1);
        var refused = new MlgNonHashSequence(first: 1, second: 2);

        AssertEqual((nint)15, sink.SumHashesOrThrow<MlgHashSequence>(success),
            "throwing associated carrier success result");
        AssertThrows<SwiftException>(() => sink.SumHashesOrThrow<MlgHashSequence>(throwing),
            "satisfied conditional carrier propagates the Swift error channel");
        AssertThrows<SwiftRuntimeException>(() => sink.SumHashesOrThrow<MlgNonHashSequence>(refused),
            "failed conditional carrier uses refusal rather than the Swift error channel");
    }

    public void TestAssociatedSuperclassCarrier_MutatesInOrderAndPreservesIdentity()
    {
        var roster = Functions.MakeAnimalRoster(firstName: "alpha", secondName: "omega");
        var first = new Dog(name: "bravo", breed: "Collie");
        var second = new Dog(name: "charlie", breed: "Husky");
        var dogs = new MlgDogSequence(first: first, second: second);

        Expression<Action> genericCall = () => roster.Insert<MlgDogSequence>(dogs, i: 1);
        AssertExplicitGenericArm(genericCall, typeof(MlgDogSequence), typeof(AnimalRoster),
            nameof(AnimalRoster.Insert), expectClosedSibling: true);

        roster.Insert<MlgDogSequence>(dogs, i: 1);

        AssertEqual((nint)4, roster.CountNative, "associated superclass carrier persisted mutation");
        AssertTrue(Functions.AnimalsAreIdentical(roster[1], first), "first inserted Dog identity/order");
        AssertTrue(Functions.AnimalsAreIdentical(roster[2], second), "second inserted Dog identity/order");
    }

    public void TestAssociatedSuperclassCarrier_RefusalLeavesReceiverUnchanged()
    {
        var roster = Functions.MakeAnimalRoster(firstName: "alpha", secondName: "omega");
        var bad = new MlgNonAnimalSequence(first: 2, second: 4);

        AssertThrows<SwiftRuntimeException>(() => roster.Insert<MlgNonAnimalSequence>(bad, i: 1),
            "Sequence with non-Animal Element is refused");
        AssertEqual((nint)2, roster.CountNative, "refusal did not mutate the roster");
    }

    public void TestDirectSuperclassCarrier_BaseAndSubclassDispatch()
    {
        var host = new ClassBoundGenericHost(prefix: "instance");
        var animal = new Animal(name: "A", sound: "chirp");
        var dog = new Dog(name: "D", breed: "Retriever");

        AssertEqual("instance: Animal: A", host.Inspect<Animal>(animal), "direct base carrier");
        AssertEqual("instance: Dog: D (Retriever)", host.Inspect<Dog>(dog),
            "subclass carrier preserves override dispatch");
        AssertEqual("static: Dog: D (Retriever)", ClassBoundGenericHost.InspectStatic<Dog>(dog),
            "static direct-superclass carrier");
        AssertEqual("free: Dog: D (Retriever)", Functions.InspectAnimal<Dog>(dog),
            "free direct-superclass carrier");
    }

    private void AssertExplicitGenericArm(
        LambdaExpression expression,
        System.Type argument,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type declaringType,
        string methodName,
        bool expectClosedSibling)
    {
        var call = expression.Body as MethodCallExpression;
        AssertTrue(call != null, $"{methodName} expression is structurally a method call");
        AssertTrue(call!.Method.IsGenericMethod,
            $"explicit <{argument.Name}> syntax binds the generic {methodName} arm");
        AssertTrue(call.Method.GetGenericArguments().SequenceEqual(new[] { argument }),
            $"{methodName} generic MethodInfo carries the requested {argument.Name} argument");

        var hasClosedSibling = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == methodName && !m.IsGenericMethod)
            .Any(m => m.GetParameters().FirstOrDefault()?.ParameterType == argument);
        AssertEqual(expectClosedSibling, hasClosedSibling,
            expectClosedSibling
                ? $"CSM closed sibling for {argument.Name} exists, keeping the generic-arm assertion adversarial"
                : $"dependency carrier {argument.Name} remains outside the main-module CSM sibling set");
    }
}

internal static class MethodLevelGenericNativeProbeImports
{
    internal const ulong Refused = 1UL << 0;
    internal const ulong ResultUntouched = 1UL << 1;
    internal const ulong ErrorUntouched = 1UL << 2;
    internal const ulong NoEntry = 1UL << 3;
    internal const ulong NoMutation = 1UL << 4;
    internal const ulong ProtectedStorage = 1UL << 5;
    internal const ulong DirectSentinel = 1UL << 6;

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_MlgParameterizedRefusalProbe")]
    internal static extern ulong ParameterizedRefusal();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_MlgAssociatedProtocolRefusalProbe")]
    internal static extern ulong AssociatedProtocolRefusal();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_MlgAssociatedSuperclassRefusalProbe")]
    internal static extern ulong AssociatedSuperclassRefusal();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_MlgDirectSuperclassRefusalProbe")]
    internal static extern ulong DirectSuperclassRefusal();
}

/// <summary>
/// Crash-isolated parameterized-existential refusal probe. The native fixture calls the generated
/// wrapper with inaccessible payload and receiver pages, so an ordering regression faults this
/// class and is recovered/reported by the Nuke class-level simulator harness.
/// </summary>
public sealed class MethodLevelGenericParameterizedNativeTests : TestBase
{
    public MethodLevelGenericParameterizedNativeTests(TestResults results) : base(results) { }

    public void TestActualWrapperRefusesBeforePayloadReceiverAndResult()
    {
        var report = MethodLevelGenericNativeProbeImports.ParameterizedRefusal();
        var expected = MethodLevelGenericNativeProbeImports.Refused
            | MethodLevelGenericNativeProbeImports.ResultUntouched
            | MethodLevelGenericNativeProbeImports.NoEntry
            | MethodLevelGenericNativeProbeImports.ProtectedStorage;
        AssertEqual(expected, report,
            "Collection<String> wrapper refused with protected payload/receiver and untouched result canary");
    }
}

/// <summary>Crash-isolated conditional-carrier probe for the protocol Element clause.</summary>
public sealed class MethodLevelGenericAssociatedProtocolNativeTests : TestBase
{
    public MethodLevelGenericAssociatedProtocolNativeTests(TestResults results) : base(results) { }

    public void TestProtocolClauseRefusesWithoutEntryErrorOrPayloadAccess()
    {
        var report = MethodLevelGenericNativeProbeImports.AssociatedProtocolRefusal();
        var expected = MethodLevelGenericNativeProbeImports.Refused
            | MethodLevelGenericNativeProbeImports.ErrorUntouched
            | MethodLevelGenericNativeProbeImports.NoEntry
            | MethodLevelGenericNativeProbeImports.ProtectedStorage
            | MethodLevelGenericNativeProbeImports.DirectSentinel;
        AssertEqual(expected, report,
            "Element: HashLike carrier refused before protected storage, error publication, or method entry");
    }
}

/// <summary>Crash-isolated conditional-carrier probe for the superclass Element clause.</summary>
public sealed class MethodLevelGenericAssociatedSuperclassNativeTests : TestBase
{
    public MethodLevelGenericAssociatedSuperclassNativeTests(TestResults results) : base(results) { }

    public void TestSuperclassClauseRefusesWithoutEntryOrMutation()
    {
        var report = MethodLevelGenericNativeProbeImports.AssociatedSuperclassRefusal();
        var expected = MethodLevelGenericNativeProbeImports.Refused
            | MethodLevelGenericNativeProbeImports.NoEntry
            | MethodLevelGenericNativeProbeImports.NoMutation
            | MethodLevelGenericNativeProbeImports.ProtectedStorage;
        AssertEqual(expected, report,
            "Element: Animal carrier refused before protected payload access or receiver mutation");
    }
}

/// <summary>
/// Crash-isolated direct-superclass negative that managed generic constraints cannot express.
/// </summary>
public sealed class MethodLevelGenericSuperclassNativeTests : TestBase
{
    public MethodLevelGenericSuperclassNativeTests(TestResults results) : base(results) { }

    public void TestActualWrapperRefusesNonAnimalMetadataBeforeStorageAccess()
    {
        var report = MethodLevelGenericNativeProbeImports.DirectSuperclassRefusal();
        var expected = MethodLevelGenericNativeProbeImports.Refused
            | MethodLevelGenericNativeProbeImports.ResultUntouched
            | MethodLevelGenericNativeProbeImports.NoEntry
            | MethodLevelGenericNativeProbeImports.ProtectedStorage;
        AssertEqual(expected, report,
            "T: Animal wrapper refused valid non-Animal metadata before protected payload/receiver access");
    }
}
