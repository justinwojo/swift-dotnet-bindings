// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Optional parameters kept by a reduced-arity (default-argument) overload. The overload's
/// wrapper hands an optional existential to the Swift shim by address and decodes an optional
/// small primitive to a value first; each case runs with nil and with a value, and the existential
/// with both a Swift and a C# conformer.
/// </summary>
public class DefaultOptionalExistentialParamTests : TestBase
{
    public DefaultOptionalExistentialParamTests(TestResults results) : base(results) { }

    public void TestInitOptionalExistential_Nil()
    {
        using var session = new DefaultOptionalAdjustedSession(adjuster: null);
        AssertEqual(-1, session.Adjusted, "no adjuster");
        AssertEqual(-1, session.Code, "code defaulted");
        AssertEqual("nil", session.TagDescription, "tag defaulted");
        AssertFalse(session.Verbose, "flag defaulted");
    }

    public void TestInitOptionalExistential_SwiftConformer()
    {
        using var doubler = new DefaultOptionalDoubler();
        using var session = new DefaultOptionalAdjustedSession(adjuster: doubler);
        AssertEqual(42, session.Adjusted, "Swift conformer applied to 21");
        AssertEqual(-1, session.Code, "code defaulted");
    }

    public void TestInitOptionalExistential_CSharpConformer()
    {
        using var session = new DefaultOptionalAdjustedSession(adjuster: new CSharpAdjuster(100));
        AssertEqual(121, session.Adjusted, "C# conformer applied to 21");
    }

    public void TestInitOptionalExistentialAndSmallOptional()
    {
        using var withCode = new DefaultOptionalAdjustedSession(new CSharpAdjuster(1), 7);
        AssertEqual(22, withCode.Adjusted, "C# conformer beside a code");
        AssertEqual(7, withCode.Code, "code passed by value");

        using var nilCode = new DefaultOptionalAdjustedSession(null, (int?)null);
        AssertEqual(-1, nilCode.Adjusted, "no adjuster beside a nil code");
        AssertEqual(-1, nilCode.Code, "nil code");
    }

    public void TestInitOptionalExistentialSmallOptionalAndAny()
    {
        using var doubler = new DefaultOptionalDoubler();
        using var withTag = new DefaultOptionalAdjustedSession(doubler, 3, "hello");
        AssertEqual(42, withTag.Adjusted, "Swift conformer beside a code and a tag");
        AssertEqual(3, withTag.Code, "code");
        AssertEqual("hello", withTag.TagDescription, "tag carried as Any");

        using var nilTag = new DefaultOptionalAdjustedSession(null, (int?)null, null);
        AssertEqual(-1, nilTag.Adjusted, "no adjuster");
        AssertEqual(-1, nilTag.Code, "nil code");
        AssertEqual("nil", nilTag.TagDescription, "nil tag");
    }

    public void TestMethodOptionalExistential()
    {
        using var session = new DefaultOptionalAdjustedSession();
        using var doubler = new DefaultOptionalDoubler();
        AssertEqual(5, session.Apply(5, null), "no adjuster");
        AssertEqual(10, session.Apply(5, doubler), "Swift conformer");
        AssertEqual(8, session.Apply(5, new CSharpAdjuster(3)), "C# conformer");
    }

    public void TestArraySliceBesideOptionals()
    {
        AssertEqual(6, Functions.SumArraySliceWithBias(new[] { 1, 2, 3 }, null), "nil bias");
        AssertEqual(16, Functions.SumArraySliceWithBias(new[] { 1, 2, 3 }, 10), "bias passed by value");

        using var doubler = new DefaultOptionalDoubler();
        AssertEqual(6, Functions.SumArraySliceAdjusted(new[] { 1, 2, 3 }, null), "no adjuster");
        AssertEqual(12, Functions.SumArraySliceAdjusted(new[] { 1, 2, 3 }, doubler), "Swift conformer");
        AssertEqual(7, Functions.SumArraySliceAdjusted(new[] { 1, 2, 3 }, new CSharpAdjuster(1)), "C# conformer");
    }
}

sealed class CSharpAdjuster : IDefaultOptionalAdjuster
{
    private readonly int _offset;
    public CSharpAdjuster(int offset) => _offset = offset;
    public int Adjust(int value) => value + _offset;
}
