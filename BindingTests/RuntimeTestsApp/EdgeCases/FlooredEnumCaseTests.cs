// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// A simple enum's case floored above the running OS must be refused in managed code before it
/// reaches Swift. Swift has no such case there: the enum's own wrappers rebuild <c>self</c> through a
/// case-by-tag switch whose <c>if #available … else fatalError</c> arm traps, and no C# try/catch
/// survives that. Every simulator is below iOS 99, so the iOS 99 case must throw
/// <see cref="PlatformNotSupportedException"/>, the iOS 15 case must pass, and a floored case Swift
/// hands back must arrive as a plain value. Fixture: EdgeCases/RuntimeAvailabilityGuard.swift.
///
/// The floored constants are reached through <see cref="Enum.Parse{TEnum}(string)"/>: naming them
/// would raise CA1416 at this iOS 15 call site, and the test should not assume what integer the
/// binding gives a case.
/// </summary>
public class FlooredEnumCaseTests : TestBase
{
    public FlooredEnumCaseTests(TestResults results) : base(results) { }

    private static FlooredCaseMode Future => Enum.Parse<FlooredCaseMode>("Future");
    private static FlooredCaseMode Enhanced => Enum.Parse<FlooredCaseMode>("Enhanced");
    private static FlooredCaseTag Gamma => Enum.Parse<FlooredCaseTag>("Gamma");
    private static DependencyFlooredMode DependencyFuture => Enum.Parse<DependencyFlooredMode>("Future");

    private void AssertRefused(Action call, string what)
    {
        try
        {
            call();
            AssertTrue(false, $"{what} reached Swift with a case the OS lacks");
        }
        catch (PlatformNotSupportedException e)
        {
            AssertTrue(e.Message.Contains("99.0", StringComparison.Ordinal), $"{what} names the case's floor: {e.Message}");
        }
    }

    public void TestFlooredCaseAsSelf_RefusesBeforeSwift()
    {
        AssertRefused(() => Future.GetWeight(), "An instance property of the floored case");
        AssertRefused(() => FlooredCaseMode.Standard.Combined(Future), "An enum argument of the floored case");
        AssertRefused(() => Future.Combined(FlooredCaseMode.Standard), "An instance method of the floored case");
    }

    public void TestFlooredCaseAsArgument_RefusesBeforeSwift()
    {
        AssertRefused(() => TestLibFunctions.FlooredCaseModeRawValue(Future), "A raw-value enum argument");
        AssertRefused(() => TestLibFunctions.FlooredCaseTagOrdinal(Gamma), "A tag-only enum argument");
    }

    public void TestAvailableCases_PassThrough()
    {
        AssertEqual(10, FlooredCaseMode.Standard.GetWeight(), "An ungated case reaches Swift");
        AssertEqual(20, Enhanced.GetWeight(), "A case floored at a met version reaches Swift");
        AssertEqual(101, Enhanced.Combined(Enhanced), "Two met cases reach Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseModeRawValue(Enhanced), "A met case argument reaches Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseTagOrdinal(FlooredCaseTag.Beta), "A tag-only ungated case reaches Swift");
    }

    /// <summary>
    /// A floored case Swift returns (the test lib has it on every OS) is projected as a plain value:
    /// the return direction neither throws nor traps.
    /// </summary>
    public void TestFlooredCaseReturnedFromSwift_IsAPlainValue()
    {
        AssertEqual(Future, TestLibFunctions.GetFlooredCaseModeLatest(), "Swift returns the floored raw-value case");
        AssertEqual(Gamma, TestLibFunctions.GetFlooredCaseTagLatest(), "Swift returns the floored tag-only case");
    }

    /// <summary>
    /// An enum inside a container crosses as memory the runtime writes directly, with no raw-value
    /// conversion and no wrapper switch, so the walk before marshalling is the only refusal.
    /// Fixture: the container functions in EdgeCases/RuntimeAvailabilityGuard.swift.
    /// </summary>
    public void TestFlooredCaseInsideContainers_RefusesBeforeSwift()
    {
        AssertRefused(() => TestLibFunctions.FlooredCaseModeOptionalRawValue(Future), "An optional argument");
        AssertRefused(() => TestLibFunctions.FlooredCaseModeArraySum(new[] { FlooredCaseMode.Standard, Future }), "An array element");
        AssertRefused(() => TestLibFunctions.FlooredCaseModeSetSum(new HashSet<FlooredCaseMode> { Future }), "A set element");
        AssertRefused(() => TestLibFunctions.FlooredCaseModeKeyedSum(new Dictionary<FlooredCaseMode, int> { [Future] = 1 }), "A dictionary key");
        AssertRefused(() => TestLibFunctions.FlooredCaseModeValueSum(new Dictionary<string, FlooredCaseMode> { ["f"] = Future }), "A dictionary value");
        AssertRefused(() => TestLibFunctions.FlooredCaseTagArrayOrdinalSum(new[] { Gamma }), "A tag-only array element");
        AssertRefused(() => TestLibFunctions.FlooredCaseModeNestedSum(new IReadOnlyList<FlooredCaseMode>?[] { null, new[] { Future } }), "A nested element");
    }

    public void TestAvailableCasesInsideContainers_PassThrough()
    {
        AssertEqual(1, TestLibFunctions.FlooredCaseModeOptionalRawValue(Enhanced), "An optional met case reaches Swift");
        AssertEqual(-1, TestLibFunctions.FlooredCaseModeOptionalRawValue(null), "An absent optional reaches Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseModeArraySum(new[] { FlooredCaseMode.Standard, Enhanced }), "An array of met cases reaches Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseModeSetSum(new HashSet<FlooredCaseMode> { FlooredCaseMode.Standard, Enhanced }), "A set of met cases reaches Swift");
        AssertEqual(105, TestLibFunctions.FlooredCaseModeKeyedSum(new Dictionary<FlooredCaseMode, int> { [Enhanced] = 5 }), "A met dictionary key reaches Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseModeValueSum(new Dictionary<string, FlooredCaseMode> { ["e"] = Enhanced }), "A met dictionary value reaches Swift");
        AssertEqual(1, TestLibFunctions.FlooredCaseTagArrayOrdinalSum(new[] { FlooredCaseTag.Alpha, FlooredCaseTag.Beta }), "A tag-only array of met cases reaches Swift");
        AssertEqual(2, TestLibFunctions.FlooredCaseModeNestedSum(new IReadOnlyList<FlooredCaseMode>?[] { null, new[] { Enhanced, Enhanced } }), "Nested met cases reach Swift");
    }

    /// <summary>
    /// A lazily computed sequence is enumerated once: the refusal walk and the container read the
    /// same elements, so the Swift array holds exactly what the sequence produced.
    /// </summary>
    public void TestLazySequenceOfAvailableCases_IsReadOnce()
    {
        var enumerations = 0;
        IEnumerable<FlooredCaseMode> Lazy()
        {
            enumerations++;
            yield return Enhanced;
            yield return Enhanced;
        }
        AssertEqual(2, TestLibFunctions.FlooredCaseModeArraySum(Lazy()), "The lazy sequence's elements reach Swift");
        AssertEqual(1, enumerations, "The lazy sequence is enumerated once");
    }

    /// <summary>The initializer and the property setters of a class taking containers of the enum.</summary>
    public void TestFlooredCaseInsideMemberContainers_RefusesBeforeSwift()
    {
        AssertRefused(() => new FlooredCaseModeHolder(new[] { Future }), "An initializer's array argument");
        using var holder = new FlooredCaseModeHolder(new[] { Enhanced });
        AssertEqual(1, holder.Total, "The initializer's met cases reach Swift");
        AssertRefused(() => holder.Modes = new[] { Future }, "An array property setter");
        AssertRefused(() => holder.Preferred = Future, "An optional property setter");
        holder.Preferred = Enhanced;
        AssertEqual(101, holder.Total, "The setters' met cases reach Swift, and a refusal changed nothing");
    }

    /// <summary>
    /// A floored case a throwing callback returns is refused before Swift rebuilds the enum from it
    /// (below the floor its init(rawValue:) finds no such case and the adapter's force unwrap would
    /// trap): the refusal reaches Swift as the error the closure throws, which the fixture catches.
    /// </summary>
    public void TestFlooredCaseReturnedByAThrowingCallback_ReachesSwiftAsAnError()
    {
        AssertEqual(-1, TestLibFunctions.FlooredCaseModeFromThrowingCallback(() => Future), "Swift caught the refusal as an error");
        AssertEqual(1, TestLibFunctions.FlooredCaseModeFromThrowingCallback(() => Enhanced), "A met case returns to Swift");
    }

    /// <summary>
    /// A dependency enum's floored case is refused by the bound module, which read the dependency
    /// from its xcframework alone: the floor comes from the dependency's interface. Without it the
    /// consumer's wrapper rebuilds the case with init(rawValue:), which returns nil below the floor,
    /// and its precondition traps. Fixture: CrossModule/CrossModuleUsage.swift.
    /// </summary>
    public void TestDependencyEnumFlooredCase_RefusesBeforeSwift()
    {
        AssertRefused(() => TestLibFunctions.DependencyFlooredModeRawValue(DependencyFuture), "A dependency enum argument");
        AssertRefused(() => TestLibFunctions.DependencyFlooredModeArraySum(new[] { DependencyFuture }), "A dependency enum array element");
        AssertEqual(0, TestLibFunctions.DependencyFlooredModeRawValue(DependencyFlooredMode.Basic), "A met dependency case reaches Swift");
        AssertEqual(0, TestLibFunctions.DependencyFlooredModeArraySum(new[] { DependencyFlooredMode.Basic }), "A met dependency array reaches Swift");
    }
}
