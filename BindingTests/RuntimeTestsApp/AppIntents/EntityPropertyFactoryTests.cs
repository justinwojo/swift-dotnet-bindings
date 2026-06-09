// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.AppIntents;

/// <summary>
/// Consumer-side EntityProperty factory end-to-end gate.
///
/// <para>
/// The dependency's <c>MiniEntityProperty&lt;Value&gt;</c> (a stand-in for AppIntents'
/// <c>EntityProperty&lt;Value&gt;</c>) only offers method-own-generic, KeyPath-keyed
/// inits — <c>init&lt;Entity: AppEntity&gt;(identifier:, getter: KeyPath&lt;Entity, Value&gt;)</c>
/// and the <c>getSetter: WritableKeyPath</c> variant. Those tombstone in the dependency's
/// own binding. <c>ConformerKeyPathInitFactoryEmitter</c> rescues them on the consumer
/// side by closing <c>Entity</c> to the local conformer <c>MockBook</c>, emitting
/// <c>MockBookMiniEntityPropertyFactory.CreateFrom{Getter,GetSetter}</c> overloads (one
/// per distinct value type) that build the dependency object through a Swift
/// <c>@_cdecl</c> trampoline and adopt the returned <c>+1</c> ARC handle.
/// </para>
///
/// <para>
/// What these tests prove that the compile gate cannot: the trampoline symbol is actually
/// exported (not silently stripped), the <c>identifier:</c> scalar and the KeyPath argument
/// marshal correctly, the constructed object adopts cleanly (no leak/double-free), and the
/// KeyPath that lands inside the dependency object is the <i>exact</i> singleton passed in
/// (via type-erased <c>AnyKeyPath</c> equality back through Swift), for both the
/// <c>KeyPath</c> (getter) and <c>WritableKeyPath</c> (getSetter) flavors and both the
/// <c>string</c> and <c>nint</c> value types.
/// </para>
/// </summary>
[global::System.Runtime.Versioning.SupportedOSPlatform("ios16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("maccatalyst16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("macos13.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("tvos16.0")]
public class EntityPropertyFactoryTests : TestBase
{
    public EntityPropertyFactoryTests(TestResults results) : base(results) { }

    // -------------------------------------------------------------------------------------
    // getter: KeyPath flavor → MiniEntityProperty.isWritable == false
    // -------------------------------------------------------------------------------------

    public void TestCreateFromGetter_StringValue_RoundTripsIdentifierAndFlavor()
    {
        // Title is a WritableKeyPath<MockBook, string>; it binds to the broader
        // KeyPath<MockBook, string> the getter overload takes (WritableKeyPath is-a KeyPath).
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "title-prop", MockBookAppEntityKeyPaths.Title);

        AssertEqual("title-prop", prop.Identifier,
            "identifier scalar round-trips through the @_cdecl trampoline into MiniEntityProperty");
        AssertFalse(prop.IsWritable,
            "constructed via getter: → init's WritableKeyPath flag is false");
        AssertNotNull(prop.CapturedKeyPath,
            "captured key path is a non-null AnyKeyPath (trampoline forwarded a real KeyPath)");
        AssertTrue(prop.CapturedKeyPathDescription.Length > 0,
            "captured key path describes a real path, not a null pointer");
    }

    public void TestCreateFromGetter_StringValue_CapturedPathEqualsSingleton()
    {
        // The strongest end-to-end check: the KeyPath captured inside the dependency
        // object must be the *exact* singleton we passed in, compared by value on the
        // Swift side via AnyKeyPath.==.
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "eq-check", MockBookAppEntityKeyPaths.Title);

        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.Title),
            "captured AnyKeyPath equals the Title singleton threaded into init<Entity>(getter:)");
    }

    public void TestCreateFromGetter_IntValue_RoundTripsAndCapturesPageCount()
    {
        // PageCount is WritableKeyPath<MockBook, nint>; the nint getter overload is selected,
        // returning MiniEntityProperty<nint>. Exercises the second value-type overload and
        // its distinct trampoline symbol.
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "page-prop", MockBookAppEntityKeyPaths.PageCount);

        AssertEqual("page-prop", prop.Identifier, "identifier round-trips for the nint overload");
        AssertFalse(prop.IsWritable, "getter: → not writable");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.PageCount),
            "captured AnyKeyPath equals the PageCount (nint) singleton");
    }

    public void TestCreateFromGetter_ComputedGetOnlyKeyPath()
    {
        // Summary is a read-only KeyPath<MockBook, string> (get-only computed property).
        // It must thread through the getter overload exactly like a stored-property KeyPath.
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "summary-prop", MockBookAppEntityKeyPaths.Summary);

        AssertEqual("summary-prop", prop.Identifier, "identifier round-trips for a computed-property KeyPath");
        AssertFalse(prop.IsWritable, "getter: → not writable");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.Summary),
            "captured AnyKeyPath equals the Summary computed-property singleton");
    }

    // -------------------------------------------------------------------------------------
    // getSetter: WritableKeyPath flavor → MiniEntityProperty.isWritable == true
    // -------------------------------------------------------------------------------------

    public void TestCreateFromGetSetter_StringValue_IsWritable()
    {
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetSetter(
            "ws-title", MockBookAppEntityKeyPaths.Title);

        AssertEqual("ws-title", prop.Identifier, "identifier round-trips for the getSetter overload");
        AssertTrue(prop.IsWritable,
            "constructed via getSetter: → init's WritableKeyPath flag is true");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.Title),
            "captured AnyKeyPath equals the Title singleton threaded into init<Entity>(getSetter:)");
    }

    public void TestCreateFromGetSetter_IntValue_IsWritable()
    {
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetSetter(
            "ws-page", MockBookAppEntityKeyPaths.PageCount);

        AssertEqual("ws-page", prop.Identifier, "identifier round-trips for the nint getSetter overload");
        AssertTrue(prop.IsWritable, "getSetter: → writable");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.PageCount),
            "captured AnyKeyPath equals the PageCount (nint) singleton");
    }

    public void TestCreateFromGetSetter_ComputedWritableKeyPath_DisplayTitle()
    {
        // DisplayTitle is a get/set computed property → WritableKeyPath<MockBook, string>.
        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetSetter(
            "ws-display", MockBookAppEntityKeyPaths.DisplayTitle);

        AssertTrue(prop.IsWritable, "computed get/set KeyPath threads as a writable path");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.DisplayTitle),
            "captured AnyKeyPath equals the DisplayTitle computed-property singleton");
    }

    // -------------------------------------------------------------------------------------
    // Instance independence — each factory call adopts its own ARC handle.
    // -------------------------------------------------------------------------------------

    public void TestFactory_DistinctCalls_ProduceIndependentInstances()
    {
        using var a = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "id-a", MockBookAppEntityKeyPaths.Title);
        using var b = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "id-b", MockBookAppEntityKeyPaths.Title);

        AssertFalse(ReferenceEquals(a, b), "two factory calls return distinct MiniEntityProperty instances");
        AssertEqual("id-a", a.Identifier, "instance a keeps its own identifier");
        AssertEqual("id-b", b.Identifier, "instance b keeps its own identifier");
    }

    // -------------------------------------------------------------------------------------
    // Value-type availability — a KeyPath Value gated above the conformer/dependency floor must
    // lift the trampoline's @available, or the wrapper build silently strips the @_cdecl against
    // the iOS 15 deployment target and the C# P/Invoke is left pointing at no symbol.
    // -------------------------------------------------------------------------------------

    [global::System.Runtime.Versioning.SupportedOSPlatform("ios18.0")]
    [global::System.Runtime.Versioning.SupportedOSPlatform("maccatalyst18.0")]
    [global::System.Runtime.Versioning.SupportedOSPlatform("macos15.0")]
    [global::System.Runtime.Versioning.SupportedOSPlatform("tvos18.0")]
    public void TestCreateFromGetter_GatedValueType_TrampolineSurvivesAndResolves()
    {
        // MockReleaseInfo is iOS 18 / macOS 15 / tvOS 18 — strictly above MockBook (iOS 16) and
        // MiniEntityProperty (iOS 16). Without the value-type-availability merge the SBW_EPF_…
        // trampoline closing MiniEntityProperty<MockReleaseInfo> would be emitted at the iOS 16
        // floor, fail to type-check against the iOS 15 wrapper deployment target, and be stripped —
        // so this call would hit a missing native symbol. Reaching the assertions at all proves the
        // trampoline survived and the symbol is exported under the lifted floor.
        if (!OperatingSystem.IsIOSVersionAtLeast(18)
            && !OperatingSystem.IsMacCatalystVersionAtLeast(18)
            && !OperatingSystem.IsMacOSVersionAtLeast(15)
            && !OperatingSystem.IsTvOSVersionAtLeast(18))
        {
            return;
        }

        using var prop = MockBookMiniEntityPropertyFactory.CreateFromGetter(
            "release-prop", MockBookAppEntityKeyPaths.ReleaseInfo);

        AssertEqual("release-prop", prop.Identifier,
            "gated-value-type factory trampoline is exported (not stripped) and round-trips identifier");
        AssertFalse(prop.IsWritable, "getter: → not writable");
        AssertTrue(
            TestLibFunctions.SameAnyKeyPath(prop.CapturedKeyPath, MockBookAppEntityKeyPaths.ReleaseInfo),
            "captured AnyKeyPath equals the gated ReleaseInfo singleton threaded into init<Entity>(getter:)");
    }
}
