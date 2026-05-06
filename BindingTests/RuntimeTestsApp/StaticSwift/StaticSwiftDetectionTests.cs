// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.StaticSwift;

/// <summary>
/// Bundle 8 (`bug-0.10.0-mappedin-static-swift-framework`) regression test.
///
/// The fixture under <c>BindingTests/Fixtures/StaticSwift/StaticSwiftLib.xcframework/</c>
/// reproduces the Mappedin distribution shape — a static `ar archive` binary
/// paired with a complete <c>Modules/&lt;Module&gt;.swiftmodule/.swiftinterface</c>.
/// The SDK's pre-fix detector probes binary kind first and falls back to ObjC
/// when the binary is not a Mach-O dylib, so this fixture is misclassified.
/// Bundle 8's fix swaps the detection order to check <c>.swiftinterface</c>
/// presence before the binary-kind probe.
/// </summary>
/// <remarks>
/// <para>
/// This class ships <c>[Skip]</c>-attributed in the scaffolding commit so it does
/// not block Bundles 1–7 on an intentionally-failing Bundle 8 regression test.
/// Bundle 8's same-commit fix removes the <c>[Skip]</c> attribute as part of
/// landing the detection-order swap. Until then the assertion below is inert.
/// </para>
/// <para>
/// The test does NOT exercise binding generation end-to-end — that belongs to
/// the SDK build pipeline, not the runtime test app. Instead, the assertion
/// runs the SDK's classify entry point against the fixture and verifies the
/// resulting kind is Swift. Bundle 8 wires the actual SDK call when it
/// removes the <c>[Skip]</c>; the placeholder body documents the contract.
/// </para>
/// </remarks>
[Skip("Enabled by Bundle 8 detection-order fix (bug-0.10.0-mappedin-static-swift-framework)")]
public class StaticSwiftDetectionTests : TestBase
{
    public StaticSwiftDetectionTests(TestResults results) : base(results) { }

    /// <summary>
    /// Bundle 8 lands the implementation of this assertion in the same commit
    /// that removes the class-level <c>[Skip]</c>. The expected behavior:
    /// the SDK detects <c>StaticSwiftLib.xcframework</c> as Swift (not ObjC)
    /// because <c>Modules/StaticSwiftLib.swiftmodule/&lt;arch&gt;.swiftinterface</c>
    /// is present, regardless of whether the binary is a dylib or a static
    /// archive.
    /// </summary>
    public void TestStaticSwiftFrameworkDetectedAsSwift()
    {
        // Inert until Bundle 8 lands.
        // The same-commit fix replaces this body with:
        //   var fixturePath = …/BindingTests/Fixtures/StaticSwift/StaticSwiftLib.xcframework;
        //   var kind = SdkClassifier.Classify(fixturePath);
        //   AssertEqual(LibraryKind.Swift, kind, "static-Swift xcframework with .swiftinterface should classify as Swift");
        AssertTrue(true, "Bundle 8 placeholder — body lands with the detection-order fix");
    }
}
