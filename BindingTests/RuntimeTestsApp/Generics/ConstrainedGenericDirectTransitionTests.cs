// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for the members of a struct generic over a protocol-constrained parameter and an
/// Error (<c>DirectTransitionBox</c>). No <c>@_cdecl</c> wrapper carries them, so each is a direct
/// Swift-convention import with <c>self</c> in the self register, which a native bridge carries on
/// iOS, Mac Catalyst and tvOS and which is guarded on macOS. Each member here was once declared in
/// a shape Swift does not use: the resilient struct itself and a <c>Result</c> over an address-only
/// payload come back through the indirect-result register, not as a pointer in the first result
/// register, and an Optional of a sub-word integer or a Bool is narrower than a word. So every call
/// checks the value Swift computed, and repeats, since a misdeclared result can read right once.
/// </summary>
public class ConstrainedGenericDirectTransitionTests : TestBase
{
    public ConstrainedGenericDirectTransitionTests(TestResults results) : base(results) { }

    private const SwiftTransitionFacts MemberShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever a bridge carries the member or the
    /// engine runs its direct transition, and must refuse before Swift runs everywhere else.
    /// </summary>
    private void Run(string member, Action call)
    {
        if (ImportRoutes.BridgesRouted || SwiftTransitionGuard.IsSupported(MemberShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    private static DirectTransitionBox<KeyedToken, DirectTransitionFailure> MakeBox(int key)
    {
        using var token = new KeyedToken(key);
        return new DirectTransitionBox<KeyedToken, DirectTransitionFailure>(token);
    }

    public void TestProtocolRequirementReadThroughSelf()
    {
        using var box = MakeBox(11);
        Run("ElementKey", () =>
        {
            AssertEqual(11, box.ElementKey, "elementKey reads the element's key through the witness table");
            AssertEqual(11, box.ElementKey, "a second read sees the same key");
        });
    }

    public void TestResilientGenericStructReturnedIndirectly()
    {
        using var box = MakeBox(23);
        Run("Echoed", () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var copy = box.Echoed();
                AssertEqual((nint)23, TestLibFunctions.DirectTransitionBoxKey(copy), $"echoed() returns a box holding the same element (call {i})");
            }
        });
        AssertEqual((nint)23, TestLibFunctions.DirectTransitionBoxKey(box), "the original box is untouched");
    }

    public void TestResultOverGenericParametersReturnedIndirectly()
    {
        using var box = MakeBox(31);
        Run("GetKeyedResult", () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var result = box.GetKeyedResult();
                AssertTrue(result.IsSuccess, $"keyedResult() is .success (call {i})");
                using var element = result.Success;
                AssertEqual(31, element.Key, $"keyedResult() carries the box's element (call {i})");
            }
        });
    }

    public void TestResultOverResilientErrorReturnedIndirectly()
    {
        using var box = MakeBox(9);
        Run("CountResult", () =>
        {
            using (var success = box.CountResult(succeeding: true))
            {
                AssertTrue(success.IsSuccess, "countResult(succeeding: true) is .success");
                AssertEqual((nint)9, success.Success, "the success payload is the element's key");
            }
            using (var failure = box.CountResult(succeeding: false))
            {
                AssertTrue(failure.IsFailure, "countResult(succeeding: false) is .failure");
                using var error = failure.Failure;
                AssertEqual(-9, error.Code, "the failure payload carries the negated key");
            }
        });
    }

    public void TestOptionalUInt16LowersToFourBytes()
    {
        using var box = MakeBox(7);
        Run("EchoNarrow", () =>
        {
            AssertEqual((ushort?)107, box.EchoNarrow(100), "echoNarrow adds the key to a present value");
            AssertEqual((ushort?)6, box.EchoNarrow(ushort.MaxValue), "echoNarrow wraps on overflow");
            AssertEqual((ushort?)null, box.EchoNarrow(null), "echoNarrow keeps nil");
            AssertEqual((ushort?)7, box.EchoNarrow(0), "a present zero is not nil");
        });
    }

    public void TestOptionalUInt8LowersToTwoBytes()
    {
        using var box = MakeBox(1);
        Run("EchoByte", () =>
        {
            AssertEqual((byte?)201, box.EchoByte(200), "echoByte adds one to a present value");
            AssertEqual((byte?)0, box.EchoByte(byte.MaxValue), "echoByte wraps on overflow");
            AssertEqual((byte?)null, box.EchoByte(null), "echoByte keeps nil");
        });
    }

    public void TestOptionalBoolLowersToOneByte()
    {
        using var box = MakeBox(1);
        Run("EchoFlag", () =>
        {
            AssertEqual((bool?)false, box.EchoFlag(true), "echoFlag negates true");
            AssertEqual((bool?)true, box.EchoFlag(false), "echoFlag negates false");
            AssertEqual((bool?)null, box.EchoFlag(null), "echoFlag keeps nil");
        });
    }
}
