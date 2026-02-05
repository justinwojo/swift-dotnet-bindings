// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Protocol witness dispatch tests — DEFERRED.
///
/// Investigation results (Phase B):
/// The generated bindings (Swift.SwiftBindingsTestLib.cs) contain NO protocol
/// interfaces, proxy implementations, or witness dispatch classes. All types
/// implement only ISwiftObject (from Swift.Runtime).
///
/// What's needed for protocol witness dispatch testing:
/// 1. Swift test library must define protocols (e.g., Describable, Identifiable)
/// 2. Types must explicitly conform to those protocols
/// 3. Generator must emit:
///    - C# interface for each Swift protocol (e.g., IDescribable)
///    - Protocol proxy class for C# → Swift conformance
///    - Witness dispatch P/Invoke declarations
/// 4. Generated types must implement the protocol interfaces
///
/// The Swift source files for protocols exist in:
///   - TestFramework/Sources/SwiftBindingsTestLib/Protocols/Conformance.swift.disabled
///   - TestFramework/Sources/SwiftBindingsTestLib/Protocols/NonBlittableProtocols.swift.disabled
///
/// These are currently disabled (compiled out). When protocol conformance
/// emission is implemented in the generator and these sources are re-enabled,
/// implement the following tests:
///
/// Tier 2 tests:
///   - Protocol method dispatch (call method via protocol interface)
///   - Protocol property dispatch (get/set via protocol interface)
///   - Protocol conformance check (type conforms to expected protocol)
///   - Protocol with string properties (non-blittable witness dispatch)
///   - Protocol with enum properties (non-blittable witness dispatch)
///   - Multiple protocol conformance on single type
///
/// Tier 3 tests:
///   - Protocol dispatch under GC pressure
///   - Parallel protocol method calls
///   - Protocol proxy callback from Swift → C#
/// </summary>
public class WitnessDispatchTests : TestBase
{
    public WitnessDispatchTests(TestResults results) : base(results) { }

    // TODO: No protocol interfaces in generated bindings yet.
    // When protocol conformance emission is implemented, add tests here.
    // See class-level XML doc for detailed requirements.
}
