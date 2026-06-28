// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

// EdCurve is a caseless Swift enum used as a namespace with a nested `Signing` enum, so the
// generator emits it as a nested `namespace EdCurve` under SwiftBindingsTestLib. The key type
// is therefore SwiftBindingsTestLib.EdCurve.Signing.PrivateKey, aliased here.
using PrivateKey = global::SwiftBindingsTestLib.EdCurve.Signing.PrivateKey;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// CSM coverage for method-level <c>MessageBytes</c> generics on a non-generic, deeply
/// nested key type — the Ed25519 signing shape
/// (<c>Curve25519.Signing.PrivateKey.signature&lt;D: DataProtocol&gt;(for:) throws -&gt; Data</c>).
/// The signing method whose <em>return</em> is <c>Foundation.Data</c> is the case that fell
/// through to a generic-only SB0001 stub: Data projects to the C# value type <c>byte[]</c>,
/// so the CSM return-type preflight's indirect-result-must-be-ISwiftObject gate rejected it
/// even though Data marshals through the standard indirect-result path as the ISwiftObject
/// <c>Swift.Foundation.Data</c>. With the gate consulting the InlineSwiftStruct allowlist,
/// <c>Sign(PlainMessage)</c>/<c>Sign(ContextTag)</c> now emit concrete overloads and C# can
/// finally <em>produce</em> a signature, not just verify one. <c>SignBlob</c> (ISwiftObject
/// struct return) and <c>Verify</c> (Bool return, two method-level generics) are the controls
/// that already worked — they keep both already-passing return shapes in the regression set.
/// </summary>
public class SigningSpecializationTests : TestBase
{
    public SigningSpecializationTests(TestResults results) : base(results) { }

    private static byte[] ExpectedSignature(string seed, string messageText) =>
        System.Text.Encoding.UTF8.GetBytes(messageText + "ed[" + seed + "]");

    private static byte[] ExpectedContextSignature(string seed, string messageText, string contextText) =>
        System.Text.Encoding.UTF8.GetBytes(messageText + "|" + contextText + "ed[" + seed + "]");

    private void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        if (actual is null)
            throw new AssertionException($"{label}: expected {expected.Length} bytes, got null");
        if (actual.Length != expected.Length)
            throw new AssertionException(
                $"{label}: expected {expected.Length} bytes, got {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
            if (actual[i] != expected[i])
                throw new AssertionException(
                    $"{label}: byte[{i}] expected 0x{expected[i]:X2}, got 0x{actual[i]:X2}");
    }

    // --- Foundation.Data return (the fixed case): byte[]-projecting indirect result ---

    public void TestPrivateKey_Sign_PlainMessage_RoundTripsBytes()
    {
        // Before the gate fix this method had no concrete overload — only the open-generic
        // `byte[] Sign<D>(D)` stub, which CSM cannot dispatch. The concrete overload marshals
        // the owned Swift.Foundation.Data through the indirect-result path and projects it to
        // byte[]; the returned bytes witness that the @_cdecl wrapper produced the signature.
        using var key = new PrivateKey("k1");
        using var message = new PlainMessage("hi");
        byte[] signature = key.Sign(message);
        AssertBytesEqual(ExpectedSignature("k1", "hi"), signature,
            "Sign(PlainMessage) — Foundation.Data return must round-trip the signature bytes");
    }

    public void TestPrivateKey_Sign_ContextTag_RoundTripsBytes()
    {
        // Second conformer of the same constraint — proves the InlineSwiftStruct-return
        // relaxation emits a per-conformer overload, not just one hard-wired to PlainMessage.
        using var key = new PrivateKey("k1");
        using var message = new ContextTag("ctx");
        byte[] signature = key.Sign(message);
        AssertBytesEqual(ExpectedSignature("k1", "ctx"), signature,
            "Sign(ContextTag) — second conformer's Foundation.Data return must round-trip");
    }

    public void TestPrivateKey_Sign_DistinctSeeds_ProduceDistinctSignatures()
    {
        // Payload observability: the signature suffix is derived from `self.seed`, so two
        // keys must sign the same message differently. Distinguishes "return marshalling
        // works" from "the @_cdecl never reads self" — a self-pointer regression would make
        // both signatures identical (or empty).
        using var keyA = new PrivateKey("alpha");
        using var keyB = new PrivateKey("beta");
        using var message = new PlainMessage("msg");
        byte[] sigA = keyA.Sign(message);
        byte[] sigB = keyB.Sign(message);
        AssertBytesEqual(ExpectedSignature("alpha", "msg"), sigA,
            "Sign with seed 'alpha' — self.seed must reach the @_cdecl body");
        AssertBytesEqual(ExpectedSignature("beta", "msg"), sigB,
            "Sign with seed 'beta' — self.seed must reach the @_cdecl body");
    }

    // --- Context-string: TWO method-level generics, Foundation.Data return (Signature<D,C>) ---

    public void TestPrivateKey_SignWithContext_RoundTripsBytes()
    {
        // The cartesian analog of Sign: signWithContext<D, C>(for:context:) -> Data. Both type
        // params are independent conformers, so the engine must emit a concrete overload for the
        // (D, C) pair and route its byte[]-projecting return through the same relaxed gate. A
        // single passing round-trip here proves the gate fix is pairing-count-independent.
        using var key = new PrivateKey("k1");
        using var message = new PlainMessage("hi");
        using var context = new ContextTag("ctx");
        byte[] signature = key.SignWithContext(message, context);
        AssertBytesEqual(ExpectedContextSignature("k1", "hi", "ctx"), signature,
            "SignWithContext(PlainMessage, ContextTag) — 2-generic Data return must round-trip");
    }

    public void TestPrivateKey_SignWithContext_MixedConformerPair_RoundTrips()
    {
        // Swaps the conformer in each generic slot (ContextTag as message, PlainMessage as
        // context) to exercise a different cell of the D×C cartesian than the test above —
        // distinct overloads, not one overload reused.
        using var key = new PrivateKey("k9");
        using var message = new ContextTag("aa");
        using var context = new PlainMessage("bbb");
        byte[] signature = key.SignWithContext(message, context);
        AssertBytesEqual(ExpectedContextSignature("k9", "aa", "bbb"), signature,
            "SignWithContext(ContextTag, PlainMessage) — opposite cartesian cell must round-trip");
    }

    // --- Context-string verify: THREE method-level generics, Bool return (IsValidSignature<S,D,C>) ---

    public void TestPrivateKey_VerifyWithContext_LongEnough_ReturnsTrue()
    {
        // 3-way cartesian, Bool return: sig.count >= message.count + context.count.
        using var key = new PrivateKey("k4");
        using var signature = new PlainMessage("abcdef"); // 6
        using var message = new PlainMessage("ab");        // 2
        using var context = new ContextTag("cd");          // 2  -> 6 >= 4 true
        if (!key.VerifyWithContext(signature, message, context))
            throw new AssertionException(
                "VerifyWithContext(6, 2, 2) — should be true (6 >= 2+2)");
    }

    public void TestPrivateKey_VerifyWithContext_TooShort_ReturnsFalse()
    {
        using var key = new PrivateKey("k4");
        using var signature = new ContextTag("ab");        // 2
        using var message = new PlainMessage("abc");       // 3
        using var context = new PlainMessage("de");        // 2  -> 2 >= 5 false
        if (key.VerifyWithContext(signature, message, context))
            throw new AssertionException(
                "VerifyWithContext(2, 3, 2) — should be false (2 < 3+2); mixed conformers exercise the 3-way cartesian");
    }

    // --- ISwiftObject struct return (control that already worked) ---

    public void TestPrivateKey_SignBlob_PlainMessage_RoundTripsDescriptor()
    {
        // SignatureBlob is an ISwiftObject — its indirect-result return was already admitted
        // by the TypeRecord flag check (non-frozen/memory-managed arm). Keeps the
        // already-passing ISwiftObject return shape in the regression set so the gate
        // relaxation doesn't perturb it.
        using var key = new PrivateKey("k2");
        using var message = new PlainMessage("hi");
        using var blob = key.SignBlob(message);
        AssertEqual("ed[k2]:2", blob.Descriptor,
            "SignBlob(PlainMessage) — ISwiftObject struct return descriptor must round-trip");
    }

    // --- Bool return, two method-level generics (control that already worked) ---

    public void TestPrivateKey_Verify_LongerSignature_ReturnsTrue()
    {
        using var key = new PrivateKey("k3");
        using var signature = new PlainMessage("hello");
        using var message = new PlainMessage("hi");
        if (!key.Verify(signature, message))
            throw new AssertionException(
                "Verify(len 5, len 2) — Bool return should be true (signature.rawBytes.count >= message.rawBytes.count)");
    }

    public void TestPrivateKey_Verify_ShorterSignature_ReturnsFalse()
    {
        using var key = new PrivateKey("k3");
        using var signature = new ContextTag("hi");
        using var message = new PlainMessage("hello");
        if (key.Verify(signature, message))
            throw new AssertionException(
                "Verify(len 2, len 5) — Bool return should be false; mixed conformers (ContextTag, PlainMessage) exercise the 2x2 cartesian overload");
    }

    // --- Frozen-TRIVIAL value-struct return + param (the P256 ECDSASignature shape) ---
    //
    // FixedSignature is @frozen with only UInt64 fields — no RequiresMemoryManagement, so it
    // projects to a C# value `struct : ISwiftObject` (not the class-with-buffer that
    // SignatureBlob's String field forces). The old CSM return gate admitted Data, non-frozen,
    // and frozen-WITH-memory struct returns but rejected this pure-value shape, so
    // FrozenSignatureNamespace.Sign emitted only an uncallable open-generic SB0001 stub. The
    // matching gap on the param side: a frozen-trivial value struct as a non-generic argument
    // (IsValidSignature's first parameter) was not passable. These tests pin both halves —
    // the indirect-result return marshals via GetSwiftTypeSize/MarshalFromSwift, and the param
    // crosses pinned as (IntPtr)(&v) → assumingMemoryBound(to:).pointee.
    //
    // Known answer for message bytes {1,2,3}: sum=6, xor=0 (1^2^3), count=3, w3 = sum*31+xor = 186.

    public void TestFrozenSignature_Sign_ByteArray_KnownAnswer()
    {
        // Part A: frozen-trivial value-struct return via the byte[] (RawBuffer) conformer.
        FixedSignature sig = FrozenSignatureNamespace.Sign(new byte[] { 1, 2, 3 });
        AssertEqual(6UL, sig.W0, "Sign(byte[]) W0 (sum) must round-trip the value-struct return");
        AssertEqual(0UL, sig.W1, "Sign(byte[]) W1 (xor) must round-trip");
        AssertEqual(3UL, sig.W2, "Sign(byte[]) W2 (count) must round-trip");
        AssertEqual(186UL, sig.W3, "Sign(byte[]) W3 (sum*31+xor) must round-trip");
    }

    public void TestFrozenSignature_Sign_Data_KnownAnswer()
    {
        // Part A: same value-struct return via the Foundation.Data (InlineSwiftStruct) conformer,
        // proving the relaxed return gate emits a per-conformer overload, not one hard-wired form.
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        FixedSignature sig = FrozenSignatureNamespace.Sign(data);
        AssertEqual(6UL, sig.W0, "Sign(Data) W0 (sum) must round-trip the value-struct return");
        AssertEqual(0UL, sig.W1, "Sign(Data) W1 (xor) must round-trip");
        AssertEqual(3UL, sig.W2, "Sign(Data) W2 (count) must round-trip");
        AssertEqual(186UL, sig.W3, "Sign(Data) W3 must round-trip");
    }

    public void TestFrozenSignature_Sign_DistinctMessages_ProduceDistinctSignatures()
    {
        // Observability: distinct inputs must yield distinct value-struct returns. A
        // result-pointer regression (never writing the indirect result) would make both equal.
        FixedSignature a = FrozenSignatureNamespace.Sign(new byte[] { 1, 2, 3 });
        FixedSignature b = FrozenSignatureNamespace.Sign(new byte[] { 9, 9 });
        // {9,9}: sum=18, xor=0, count=2, w3=18*31=558.
        AssertEqual(18UL, b.W0, "Sign({9,9}) W0 (sum) must reflect the actual input bytes");
        AssertEqual(2UL, b.W2, "Sign({9,9}) W2 (count) must reflect the actual input length");
        if (a.W0 == b.W0 && a.W2 == b.W2 && a.W3 == b.W3)
            throw new AssertionException(
                "Sign of distinct messages produced identical signatures — indirect result not written per call");
    }

    public void TestFrozenSignature_Verify_ConstructedSignature_ReturnsTrue()
    {
        // Part B: a C#-CONSTRUCTED FixedSignature crosses as a non-generic frozen-trivial value
        // param. IsValidSignature recomputes Sign(message) Swift-side and compares fields, so a
        // correct round-trip proves the struct's bytes (6,0,3,186) survive the pin-and-pass.
        var sig = new FixedSignature(6, 0, 3, 186);
        if (!FrozenSignatureNamespace.IsValidSignature(sig, new byte[] { 1, 2, 3 }))
            throw new AssertionException(
                "IsValidSignature(constructed sig, {1,2,3}) — value-struct param bytes must reach Swift intact (true expected)");
    }

    public void TestFrozenSignature_Verify_WrongSignature_ReturnsFalse()
    {
        // Part B negative: a value-struct param whose bytes do NOT match the recomputed signature
        // must verify false — guards against the param being read as garbage/zeroed (which could
        // spuriously match an all-zero recompute).
        var wrong = new FixedSignature(1, 2, 3, 4);
        if (FrozenSignatureNamespace.IsValidSignature(wrong, new byte[] { 1, 2, 3 }))
            throw new AssertionException(
                "IsValidSignature(wrong sig, {1,2,3}) — mismatched value-struct param must verify false");
    }

    public void TestFrozenSignature_SignThenVerify_RoundTrip()
    {
        // Part A + Part B end-to-end: take the value struct Sign RETURNS and pass it straight
        // back as the value-struct PARAM. Exercises both the return-marshal and pin-and-pass
        // paths on the same bytes with no C# reconstruction in between.
        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 7, 8, 9, 10 });
        FixedSignature produced = FrozenSignatureNamespace.Sign(data);
        var verifyData = global::Swift.Foundation.Data.FromByteArray(new byte[] { 7, 8, 9, 10 });
        if (!FrozenSignatureNamespace.IsValidSignature(produced, verifyData))
            throw new AssertionException(
                "Sign-then-verify round-trip — Sign's value-struct return must verify against the same message");
    }
}
