// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

// CA1416: runtime OS-version guards at the top of each test method narrow the
// reachability, but the analyzer does not always flow those checks through the
// discovery-generator invocation shim. The guards are authoritative at runtime;
// suppress the analyzer here rather than duplicating them in attribute form.
#pragma warning disable CA1416

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end validation for the Phase 2 VWT-backed opaque storage emitter
/// (Swift.Bindings.Apple). Each test resolves the metadata accessor P/Invoke
/// emitted for a supplement type against the live SDK, proving the emitted
/// library path + mangled symbol round-trip correctly. These tests also
/// exercise the SwiftObjectHelper dispatch paths (reflection on Mono JIT,
/// direct static virtual on NativeAOT) against a real Apple Swift-only type.
/// </summary>
public class AppleSupplementRoundTripTests : TestBase
{
    public AppleSupplementRoundTripTests(TestResults results) : base(results) { }

    public void TestFoundationLocaleLanguageMetadataResolves()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping on this simulator.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        AssertTrue(metadata.IsValid, "Foundation.Locale.Language metadata accessor returned a valid TypeMetadata");
        AssertTrue(metadata.Size > 0, $"Foundation.Locale.Language size must be > 0; got {metadata.Size}");
        unsafe
        {
            AssertTrue(metadata.ValueWitnessTable != null, "Foundation.Locale.Language VWT pointer is non-null");
        }
        TestLogger.Info($"Foundation.Locale.Language size={metadata.Size}");
    }

    public void TestCryptoKitP256SignatureMetadataResolves()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(13))
        {
            TestLogger.Info("CryptoKit.P256.Signing.ECDSASignature requires iOS 13+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.CryptoKit.P256.Signing.ECDSASignature>.GetTypeMetadata();
        AssertTrue(metadata.IsValid, "P256.Signing.ECDSASignature metadata accessor returned a valid TypeMetadata");
        AssertTrue(metadata.Size > 0, $"P256.Signing.ECDSASignature size must be > 0; got {metadata.Size}");
        TestLogger.Info($"CryptoKit.P256.Signing.ECDSASignature size={metadata.Size}");
    }

    public void TestManagedSettingsApplicationMetadataResolves()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(15))
        {
            TestLogger.Info("ManagedSettings.Application requires iOS 15+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.ManagedSettings.Application>.GetTypeMetadata();
        AssertTrue(metadata.IsValid, "ManagedSettings.Application metadata accessor returned a valid TypeMetadata");
        AssertTrue(metadata.Size > 0, $"ManagedSettings.Application size must be > 0; got {metadata.Size}");
        TestLogger.Info($"ManagedSettings.Application size={metadata.Size}");
    }

    public void TestMetadataIsCachedAcrossCalls()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Requires iOS 16+; skipping.");
            return;
        }

        var first = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        var second = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        AssertEqual(first, second, "Metadata handle is cached and stable across calls");
    }

    // Value ABI coverage — exercise Create → NewFromPayload → Dispose for each
    // supplement type. Without these, a broken NewFromPayload path (bad VWT
    // interop, mis-sized buffer, missing metadata registration) would still
    // show green above since the metadata-only tests never touch a payload.
    //
    // NewFromPayload moves the value out of the source buffer, so we free the
    // raw allocation but must NOT call VWT.Destroy on it afterwards.

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_CreateLocaleLanguage")]
    private static extern void CreateLocaleLanguage(IntPtr bufferPtr);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_CreateP256Signature")]
    private static extern void CreateP256Signature(IntPtr bufferPtr);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_CreateManagedSettingsApplication")]
    private static extern void CreateManagedSettingsApplication(IntPtr bufferPtr);

    public void TestFoundationLocaleLanguageValueRoundTrip()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)metadata.Size);
            try
            {
                CreateLocaleLanguage((IntPtr)buf);
                // ISwiftObject has static abstract members, so it can't be a generic type
                // argument to AssertNotNull<T>. Cast to object for the null check, then down
                // to the concrete supplement type for Dispose.
                object boxed = SwiftObjectHelper<Swift.Foundation.Locale.Language>.NewFromPayload((IntPtr)buf);
                AssertNotNull(boxed, "Foundation.Locale.Language materialized from payload");
                var value = (Swift.Foundation.Locale.Language)(ISwiftObject)boxed;
                try
                {
                    AssertEqual(typeof(Swift.Foundation.Locale.Language), value.GetType(), "Unboxed type matches");
                }
                finally
                {
                    value.Dispose();
                }
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
    }

    public void TestCryptoKitP256SignatureValueRoundTrip()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(13))
        {
            TestLogger.Info("CryptoKit.P256.Signing.ECDSASignature requires iOS 13+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.CryptoKit.P256.Signing.ECDSASignature>.GetTypeMetadata();
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)metadata.Size);
            try
            {
                CreateP256Signature((IntPtr)buf);
                object boxed = SwiftObjectHelper<Swift.CryptoKit.P256.Signing.ECDSASignature>.NewFromPayload((IntPtr)buf);
                AssertNotNull(boxed, "P256.Signing.ECDSASignature materialized from payload");
                var value = (Swift.CryptoKit.P256.Signing.ECDSASignature)(ISwiftObject)boxed;
                try
                {
                    AssertEqual(typeof(Swift.CryptoKit.P256.Signing.ECDSASignature), value.GetType(), "Unboxed type matches");
                }
                finally
                {
                    value.Dispose();
                }
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
    }

    public void TestManagedSettingsApplicationValueRoundTrip()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(15))
        {
            TestLogger.Info("ManagedSettings.Application requires iOS 15+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<Swift.ManagedSettings.Application>.GetTypeMetadata();
        unsafe
        {
            void* buf = NativeMemory.Alloc((nuint)metadata.Size);
            try
            {
                CreateManagedSettingsApplication((IntPtr)buf);
                object boxed = SwiftObjectHelper<Swift.ManagedSettings.Application>.NewFromPayload((IntPtr)buf);
                AssertNotNull(boxed, "ManagedSettings.Application materialized from payload");
                var value = (Swift.ManagedSettings.Application)(ISwiftObject)boxed;
                try
                {
                    AssertEqual(typeof(Swift.ManagedSettings.Application), value.GetType(), "Unboxed type matches");
                }
                finally
                {
                    value.Dispose();
                }
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
    }
}
