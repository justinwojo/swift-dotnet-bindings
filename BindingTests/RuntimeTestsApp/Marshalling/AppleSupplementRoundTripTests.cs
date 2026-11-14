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
/// End-to-end validation for the VWT-backed opaque storage emitter
/// (Swift.Bindings.Apple). Each test resolves the metadata accessor P/Invoke
/// emitted for a supplement type against the live SDK, proving the emitted
/// library path + mangled symbol round-trip correctly. These tests also
/// exercise the SwiftObjectHelper dispatch paths (reflection on Mono JIT,
/// direct static virtual on NativeAOT) against a real Apple Swift-only type.
/// </summary>
public class AppleSupplementRoundTripTests : TestBase
{
    public AppleSupplementRoundTripTests(TestResults results) : base(results) { }

    /// <summary>
    /// Swift.Foundation.Data's buffer initializer goes through the supplement's
    /// <c>SBW_Data_InitWithBytes</c> shim, a plain-C transition, so it builds the value on every
    /// runtime, the x86_64 CoreCLR interpreter included, rather than refusing: inline (short) and
    /// heap-backed payloads round-trip byte for byte, and a null buffer of length zero is the empty Data.
    /// </summary>
    public unsafe void TestDataBufferInitializerRoundTripsThroughTheSupplementShim()
    {
        foreach (var length in new[] { 3, 14, 15, 4096 })
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
                bytes[i] = (byte)(i * 7 + 1);
            global::Swift.Foundation.Data data;
            fixed (byte* pointer = bytes)
                data = new global::Swift.Foundation.Data(new global::Swift.UnsafeRawPointer(pointer), length);
            AssertEqual((nint)length, data.Count, $"Data({length}) count");
            AssertTrue(bytes.AsSpan().SequenceEqual(data.ToByteArray()), $"Data({length}) bytes");
        }

        var empty = new global::Swift.Foundation.Data(new global::Swift.UnsafeRawPointer(null), 0);
        AssertEqual((nint)0, empty.Count, "a null buffer of length zero is the empty Data");
        AssertEqual(0, empty.ToByteArray().Length, "the empty Data has no bytes");
    }

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
    // NewFromPayload copies the value out of the source buffer (the supplement's opaque types
    // declare PayloadConstructionSemantics.Copy), so the source keeps its own +1 and must be
    // VWT-destroyed before its raw allocation is freed.

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
            var initialized = false;
            try
            {
                CreateLocaleLanguage((IntPtr)buf);
                initialized = true;
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
                if (initialized)
                    metadata.ValueWitnessTable->Destroy(buf, metadata);
                NativeMemory.Free(buf);
            }
        }
    }

    public void TestFoundationLocaleLanguageArrayElementsMaterializeAcrossAssemblies()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("Foundation.Locale.Language requires iOS 16+; skipping.");
            return;
        }

        using var languages = AppleIdentity.ConsumerA.TypeProbe.CreateDefaultLanguages();
        AssertEqual(2, languages.Count, "Swift-vended Language array count");
        AssertEqual(
            2,
            AppleIdentity.ConsumerB.TypeProbe.MaterializeLanguages(languages),
            "ConsumerB materializes every Language element with canonical type identity");
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
            var initialized = false;
            try
            {
                CreateP256Signature((IntPtr)buf);
                initialized = true;
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
                if (initialized)
                    metadata.ValueWitnessTable->Destroy(buf, metadata);
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
            var initialized = false;
            try
            {
                CreateManagedSettingsApplication((IntPtr)buf);
                initialized = true;
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
                if (initialized)
                    metadata.ValueWitnessTable->Destroy(buf, metadata);
                NativeMemory.Free(buf);
            }
        }
    }
    // Known-value round trip for every type in the supplement manifest. Swift writes a fixed
    // sample (SBT_AppleSupplement_InitSample), C# materializes it into the managed projection,
    // marshals the projection into a fresh buffer, and Swift checks the value survived
    // (SBT_AppleSupplement_SampleMatches). Each manifest platform column claims the type's
    // metadata accessor, value witnesses and payload copy work on that OS; on Mac Catalyst,
    // these tests are the runtime evidence behind the maccatalyst column.

    /// <summary>Sample indices; the order matches <c>visitSupplementSample</c> in AppleSupplementFactory.swift.</summary>
    private enum AppleSupplementSample
    {
        LocaleLanguage = 0,
        LocaleRegion = 1,
        LocaleCurrency = 2,
        LocaleLanguageCode = 3,
        LocaleCollation = 4,
        ManagedSettingsApplication = 5,
        ManagedSettingsWebDomain = 6,
        ManagedSettingsActivityCategory = 7,
        P256Signature = 8,
        P384Signature = 9,
        P521Signature = 10,
        DateComponents = 11,
        PersonNameComponents = 12,
        AttributedString = 13,
    }

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_InitSample")]
    private static extern nint InitSample(nint kind, IntPtr bufferPtr);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBT_AppleSupplement_SampleMatches")]
    private static extern nint SampleMatches(nint kind, IntPtr bufferPtr);

    public void TestLocaleLanguageSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.Locale.Language>(AppleSupplementSample.LocaleLanguage, 16);
    public void TestLocaleRegionSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.Locale.Region>(AppleSupplementSample.LocaleRegion, 16);
    public void TestLocaleCurrencySampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.Locale.Currency>(AppleSupplementSample.LocaleCurrency, 16);
    public void TestLocaleLanguageCodeSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.Locale.LanguageCode>(AppleSupplementSample.LocaleLanguageCode, 16);
    public void TestLocaleCollationSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.Locale.Collation>(AppleSupplementSample.LocaleCollation, 16);
    public void TestManagedSettingsApplicationSampleRoundTrips() => AssertSampleRoundTrips<Swift.ManagedSettings.Application>(AppleSupplementSample.ManagedSettingsApplication, 15);
    public void TestManagedSettingsWebDomainSampleRoundTrips() => AssertSampleRoundTrips<Swift.ManagedSettings.WebDomain>(AppleSupplementSample.ManagedSettingsWebDomain, 15);
    public void TestManagedSettingsActivityCategorySampleRoundTrips() => AssertSampleRoundTrips<Swift.ManagedSettings.ActivityCategory>(AppleSupplementSample.ManagedSettingsActivityCategory, 15);
    public void TestP256SignatureSampleRoundTrips() => AssertSampleRoundTrips<Swift.CryptoKit.P256.Signing.ECDSASignature>(AppleSupplementSample.P256Signature, 13);
    public void TestP384SignatureSampleRoundTrips() => AssertSampleRoundTrips<Swift.CryptoKit.P384.Signing.ECDSASignature>(AppleSupplementSample.P384Signature, 13);
    public void TestP521SignatureSampleRoundTrips() => AssertSampleRoundTrips<Swift.CryptoKit.P521.Signing.ECDSASignature>(AppleSupplementSample.P521Signature, 13);
    public void TestDateComponentsSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.DateComponents>(AppleSupplementSample.DateComponents, 8);
    public void TestPersonNameComponentsSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.PersonNameComponents>(AppleSupplementSample.PersonNameComponents, 9);
    public void TestAttributedStringSampleRoundTrips() => AssertSampleRoundTrips<Swift.Foundation.AttributedString>(AppleSupplementSample.AttributedString, 15);

    // iosFloor is the type's iOS floor in the manifest; Mac Catalyst follows it, and
    // IsIOSVersionAtLeast is true on Mac Catalyst. tvOS and macOS do not run these tests.
    private void AssertSampleRoundTrips<T>(AppleSupplementSample sample, int iosFloor)
        where T : class, ISwiftObject
    {
        var name = typeof(T).FullName;
        if (!OperatingSystem.IsIOSVersionAtLeast(iosFloor))
        {
            TestLogger.Info($"{name} requires iOS {iosFloor}+; skipping.");
            return;
        }

        var metadata = SwiftObjectHelper<T>.GetTypeMetadata();
        AssertTrue(metadata.IsValid, $"{name} metadata accessor returned a valid TypeMetadata");
        AssertTrue(metadata.Size > 0, $"{name} size must be > 0; got {metadata.Size}");
        // The cleanup below destroys the source buffer, which is only right when
        // NewFromPayload copies out of it rather than adopting or moving it.
        AssertEqual(PayloadConstructionSemantics.Copy, Swift.Runtime.InteropServices.SwiftMarshal.GetPayloadSemantics<T>(),
            $"{name} declares copy payload semantics");

        var size = (int)metadata.Size;
        T value;
        unsafe
        {
            void* source = NativeMemory.Alloc((nuint)size);
            var initialized = false;
            try
            {
                AssertEqual((nint)1, InitSample((nint)sample, (IntPtr)source), $"{name} Swift sample initialized");
                initialized = true;
                value = (T)SwiftObjectHelper<T>.NewFromPayload((IntPtr)source);
            }
            finally
            {
                if (initialized)
                    metadata.ValueWitnessTable->Destroy(source, metadata);
                NativeMemory.Free(source);
            }
        }

        try
        {
            AssertEqual(typeof(T), value.GetType(), $"{name} materializes as its managed projection");
            unsafe
            {
                void* destination = NativeMemory.Alloc((nuint)size);
                var marshalled = false;
                try
                {
                    var span = new Span<byte>(destination, size);
                    AssertEqual(size, ((ISwiftObject)value).MarshalToSwift(ref span), $"{name} marshals its full payload");
                    marshalled = true;
                    AssertEqual((nint)1, SampleMatches((nint)sample, (IntPtr)destination),
                        $"{name} value survives Swift -> C# -> Swift");
                }
                finally
                {
                    if (marshalled)
                        metadata.ValueWitnessTable->Destroy(destination, metadata);
                    NativeMemory.Free(destination);
                }
            }
        }
        finally
        {
            ((IDisposable)value).Dispose();
        }
    }
}
