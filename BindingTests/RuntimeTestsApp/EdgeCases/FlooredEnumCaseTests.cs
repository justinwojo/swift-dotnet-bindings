// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

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
}
