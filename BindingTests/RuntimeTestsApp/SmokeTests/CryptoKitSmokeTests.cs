// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if CRYPTOKIT_SMOKE
using System;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 2 end-to-end smoke test for the Apple-framework direct-mode pipeline on
/// CryptoKit. Consumes the externally-built <c>CryptoKit.Swift.iOS.dll</c> +
/// <c>CryptoKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/CryptoKitSnapshot/</c> and calls a handful of hermetic,
/// metadata-only CryptoKit APIs to prove the whole chain resolves and marshals:
/// wrapper dylib → system <c>CryptoKit.framework</c> via dyld → <c>@_cdecl</c> thunk
/// → real CryptoKit type → back through the wrapper → C# consumer.
///
/// Gated by the <c>CRYPTOKIT_SMOKE</c> compile symbol, which the csproj sets only
/// when every prerequisite (snapshot csproj, simulator wrapper slice, ProjectReference
/// targets file, iossimulator-arm64 RID, explicit <c>EnableCryptoKitSmoke=true</c>
/// opt-in) is satisfied. Regenerate the snapshot with
/// <c>nuke regenerate-apple-snapshot --framework CryptoKit</c>.
///
/// No extern alias is required (unlike <see cref="StoreKitSmokeTests"/>): CryptoKit
/// is a pure Swift framework and Microsoft.iOS does not ship an ObjC <c>CryptoKit</c>
/// namespace, so the Swift-side types do not collide with default discovery.
///
/// <b>Deliberately excluded:</b> <c>SHA256.hash(data:)</c>, <c>SHA3</c>, any hashing
/// API, and anything else that takes a <c>Data</c> / <c>UnsafeRawBufferPointer</c>
/// parameter. Those route through the very <c>UnsafeRawBufferPointer</c> parameter
/// path that the generator deliberately skips (fix #11 in the session plan), so
/// using them would surface a known generator gap rather than an integration bug.
/// </summary>
public class CryptoKitSmokeTests : TestBase
{
    public CryptoKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Exercises the metadata-only value-type round-trip on CryptoKit's
    /// <c>SymmetricKey(size:)</c> constructor plus the <c>bitCount</c> property:
    ///
    ///   1. <c>CryptoKit.SymmetricKeySize.Bits256</c> — static getter that goes
    ///      through the wrapper thunk <c>SBW_Get_CryptoKit_SymmetricKeySize_bits256</c>
    ///      and returns an indirect-result <c>SymmetricKeySize</c> struct.
    ///   2. <c>new SymmetricKey(size)</c> — wrapper thunk
    ///      <c>SBW_CryptoKit_SymmetricKey_init_20F2C83E</c> takes the
    ///      <c>SymmetricKeySize</c> <c>SafeHandle</c> and emits an indirect-result
    ///      <c>SymmetricKey</c>.
    ///   3. <c>symKey.BitCount</c> — <c>SBW_Get_CryptoKit_SymmetricKey_bitCount</c>
    ///      returns a plain <c>nint</c> read back through the C# layer.
    ///
    /// Assertion: <c>BitCount == 256</c>. Unlike the StoreKit primitive-bool smoke
    /// which accepts either true or false (the simulator legitimately reports
    /// either), this test can assert the exact value because <c>bitCount</c> is a
    /// deterministic function of the <c>SymmetricKeySize</c> enum case — any other
    /// value would indicate a marshalling bug rather than environment variance.
    ///
    /// This is the Session 2 equivalent of <see cref="StoreKitSmokeTests.TestAppStoreCanMakePayments"/>:
    /// the minimum viable success signal for end-to-end Apple-framework direct-mode
    /// pipeline on a framework other than StoreKit (fix #16 in the session plan).
    /// </summary>
    public void TestSymmetricKeyBitCount()
    {
        try
        {
            using var size = CryptoKit.SymmetricKeySize.Bits256;
            using var symKey = new CryptoKit.SymmetricKey(size);
            int bitCount = symKey.BitCount;
            TestLogger.Info($"CryptoKit.SymmetricKey(size: .bits256).BitCount = {bitCount}");
            AssertEqual(256, bitCount, "SymmetricKey(size: .bits256).bitCount must equal 256");
        }
        catch (System.Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Exercises the indirect-result Swift-class-return path on
    /// <c>Curve25519.Signing.PrivateKey</c>:
    ///
    ///   1. <c>new PrivateKey()</c> — wrapper thunk
    ///      <c>SBW_CryptoKit_PrivateKey_init_3ADDF6AA</c> constructs a fresh
    ///      signing key. The generator resolves the class metadata via the
    ///      wrapper then falls back to the real CryptoKit metadata PInvoke
    ///      (<c>$s9CryptoKit10Curve25519O7SigningO10PrivateKeyVMa</c>) if the
    ///      wrapper metadata is not found.
    ///   2. <c>privateKey.PublicKey</c> — <c>SBW_Get_CryptoKit_Curve25519_Signing_PrivateKey_publicKey</c>
    ///      returns a freshly-constructed <c>PublicKey</c> struct through the
    ///      indirect-result buffer protocol, exactly the code path that fix #9
    ///      (closure class return) hardened. The public key is also
    ///      <c>IDisposable</c> — disposing it must not double-free the
    ///      still-live private key.
    ///
    /// Assertion: the public key is non-null and dispose completes cleanly. We
    /// deliberately do NOT call <c>RawRepresentation</c> or any API that returns
    /// a <c>byte[]</c> derived from <c>Swift.Data</c>; while return-position
    /// <c>Data</c> works (the wrapper packs it into the indirect-result buffer),
    /// we stick to the narrowest possible metadata-only surface for this smoke
    /// test so a regression here is unambiguous about which code path broke.
    /// </summary>
    public void TestCurve25519SigningPrivateKeyRoundTrip()
    {
        try
        {
            using var privateKey = new CryptoKit.Curve25519.Signing.PrivateKey();
            using var publicKey = privateKey.PublicKey;
            AssertTrue(publicKey is not null,
                "Curve25519.Signing.PrivateKey().PublicKey must return a non-null PublicKey instance");
            TestLogger.Info("Curve25519.Signing.PrivateKey().PublicKey round-trip completed without error");
        }
        catch (System.Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Dumps the full exception chain to <see cref="TestLogger"/> so reflection
    /// wrapping (<see cref="System.Reflection.TargetInvocationException"/>) does
    /// not obscure the real failure. Matches the error-logging shape used in
    /// <see cref="StoreKitSmokeTests"/>.
    /// </summary>
    private static void LogExceptionChain(System.Exception ex)
    {
        var inner = ex;
        var depth = 0;
        while (inner != null)
        {
            TestLogger.Info($"  [ex{depth}] {inner.GetType().FullName}: {inner.Message}");
            if (inner.StackTrace != null)
                TestLogger.Info($"  [ex{depth}] stack: {inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }
    }
}

#endif
