// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// A simple enum inside an async function's tuple result. The completion callback hands each
/// element over as a scalar, so the enum must cross as the value its synchronous return uses (the
/// case identity, or an @objc enum's raw value), not as a pointer to a heap copy that C# would read
/// as the case. Each function returns a different case for count 0, so a stale or misread value
/// cannot pass. Fixture: Async/AsyncEnumTupleResults.swift.
/// </summary>
public class AsyncEnumTupleResultTests : TestBase
{
    public AsyncEnumTupleResultTests(TestResults results) : base(results) { }

    public async Task TestCaseIdentityEnumWithHiddenFirstCase()
    {
        var hidden = TestLibFunctions.GetCaseShiftHidden();
        var (zeroCase, zeroCount) = await TestLibFunctions.CaseShiftPairAsync(0);
        AssertEqual(hidden, zeroCase, "count 0 returns the hidden case");
        AssertEqual((nint)0, zeroCount, "count 0 element");
        var (oneCase, oneCount) = await TestLibFunctions.CaseShiftPairAsync(5);
        AssertEqual(CaseShift.Second, oneCase, "count 5 returns Second");
        AssertEqual((nint)5, oneCount, "count 5 element");
    }

    public async Task TestPlainEnum()
    {
        var (zero, zeroCount) = await TestLibFunctions.TuplePlainPairAsync(0);
        AssertEqual(AsyncTuplePlain.West, zero, "count 0 returns West");
        AssertEqual((nint)0, zeroCount, "count 0 element");
        var (other, otherCount) = await TestLibFunctions.TuplePlainPairAsync(3);
        AssertEqual(AsyncTuplePlain.East, other, "count 3 returns East");
        AssertEqual((nint)3, otherCount, "count 3 element");
    }

    public async Task TestIntRawValueEnum()
    {
        var (zero, zeroCount) = await TestLibFunctions.TupleRawPairAsync(0);
        AssertEqual(AsyncTupleRaw.High, zero, "count 0 returns High");
        AssertEqual((nint)0, zeroCount, "count 0 element");
        var (other, otherCount) = await TestLibFunctions.TupleRawPairAsync(4);
        AssertEqual(AsyncTupleRaw.Mid, other, "count 4 returns Mid");
        AssertEqual((nint)4, otherCount, "count 4 element");
    }

    public async Task TestByteRawValueEnumBetweenIntegers()
    {
        var (before, value, after) = await TestLibFunctions.TupleSmallTripleAsync(8);
        AssertEqual((nint)8, before, "element before the enum");
        AssertEqual(AsyncTupleSmall.Big, value, "the enum is Big");
        AssertEqual((nint)9, after, "element after the enum");
    }

    public async Task TestObjCEnumCrossesAsItsRawValue()
    {
        var (code, count) = await TestLibFunctions.TupleObjCPairAsync(6);
        AssertEqual(AuthErrorCodeLike.NetworkError, code, "the @objc enum is NetworkError");
        AssertEqual((nint)6, count, "count element");
    }
}
