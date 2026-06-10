// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Numerics;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Reproduces the RealityFoundation <c>MeshBuffers.Semantic&lt;Vector3&gt;</c>
/// NativeAOT metadata gap in-repo: a reference-type (resilient, class-backed)
/// generic <c>ISwiftObject</c> over a SIMD element, reached ONLY through a bare
/// <c>SwiftObjectHelper&lt;Closed&gt;.GetTypeMetadata()</c> probe — never through a
/// factory/return call that would root the closed instantiation's metadata
/// specialization on its own.
///
/// On Mono/sim the probe resolves via the reflection path. On NativeAOT/device it
/// dispatches through the constrained static-abstract <c>T.GetTypeMetadata()</c> in
/// canonical/shared generic code, which calls the closed instantiation's Swift
/// <c>...VMa</c> metadata accessor. The consuming app's own probe call is the one
/// consumer-side reference to the closed form; this test asserts that reference
/// alone yields a valid metadata handle on NativeAOT — i.e. the closed
/// instantiation's accessor is reachable on every runtime, with no producer call
/// (return/marshal path) masking the bare-probe path.
///
/// <c>AotRootedMetadataBuffer&lt;simd_float3&gt;</c> is recorded by the generator
/// because <c>makeAotRootedFloat3Buffer</c> returns it; that producer is
/// deliberately not called from C#, so the closed form is reachable here only via
/// the module-init factory registration and this probe.
/// </summary>
public class GenericMetadataAotRootingTests : TestBase
{
    public GenericMetadataAotRootingTests(TestResults results) : base(results) { }

    public void TestBareMetadataProbeOnSimdClosedGeneric()
    {
        // Bare metadata probe — the exact shape the RealityFoundation per-package
        // test uses (SwiftObjectHelper<MeshBuffers.Semantic<Vector3>>.GetTypeMetadata()).
        // No call to Functions.MakeAotRootedFloat3Buffer anywhere in this app.
        var metadata = SwiftObjectHelper<AotRootedMetadataBuffer<Vector3>>.GetTypeMetadata();
        AssertTrue(
            metadata.Handle != IntPtr.Zero,
            "AotRootedMetadataBuffer<Vector3> bare-probe metadata handle is non-zero");
        TestLogger.Info(
            $"AotRootedMetadataBuffer<Vector3> bare-probe metadata handle = 0x{metadata.Handle:X}");
    }
}
