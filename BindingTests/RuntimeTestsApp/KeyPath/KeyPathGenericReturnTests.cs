// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// Runtime coverage for the AppIntents 0.12.0 EntityQuerySort&lt;Entity&gt;.by shape:
/// a generic host type whose instance accessor returns
/// <see cref="global::Swift.PartialKeyPath{TRoot}"/> parameterised by the host's
/// own generic argument. Two surfaces exercised end-to-end:
///
/// <list type="bullet">
///   <item><c>KeyPathGenericSort&lt;TElement&gt;</c> — generic struct host;
///     mirrors <c>EntityQuerySort</c> exactly (non-frozen struct → C# class with
///     SafeHandle).</item>
///   <item><c>KeyPathGenericContainer&lt;TElement&gt;</c> — generic class host.</item>
/// </list>
///
/// <para>Both hosts ship a <c>by:</c> stored property + <c>lookup</c> computed
/// accessor returning <c>PartialKeyPath&lt;TElement&gt;</c>. The constructors
/// previously routed through the <c>[Obsolete(SB0001)]</c> direct-<c>CallConvSwift</c>
/// fallback and seeded heap corruption that crashed the second call to
/// <c>KeyPathFactory.MakeReferenceWritableBoxNPath()</c>; the static-factory dispatch
/// path now widened in <c>GenericDispatchEmitter.CanEmitStaticDispatch</c> for the
/// KeyPath family routes the construction through a normalized
/// <c>(resultPtr, by: UnsafeRawPointer, T_metadata)</c> @_cdecl wrapper, eliminating
/// the SB0001 warning and the runtime corruption.</para>
/// </summary>
public class KeyPathGenericReturnTests : TestBase
{
    public KeyPathGenericReturnTests(TestResults results) : base(results) { }

    public void TestKeyPathGenericSort_ByAccessorReturnsTypedPartialKeyPath()
    {
        using var seedPath = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var sort = new KeyPathGenericSort<BoxKP>(seedPath);
        using var by = sort.By;
        AssertNotNull(by, "Sort.By returns non-null PartialKeyPath");
        AssertTrue(by is global::Swift.PartialKeyPath<BoxKP>, "Sort.By is typed PartialKeyPath<BoxKP>");
    }

    public void TestKeyPathGenericSort_LookupAccessorReturnsTypedPartialKeyPath()
    {
        using var seedPath = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var sort = new KeyPathGenericSort<BoxKP>(seedPath);
        using var lookup = sort.Lookup;
        AssertNotNull(lookup, "Sort.Lookup returns non-null PartialKeyPath");
        AssertTrue(lookup is global::Swift.PartialKeyPath<BoxKP>, "Sort.Lookup is typed PartialKeyPath<BoxKP>");
    }

    public void TestKeyPathGenericContainer_ByPropertyReturnsTypedPartialKeyPath()
    {
        using var seedPath = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var container = new KeyPathGenericContainer<BoxKP>(seedPath);
        using var by = container.By;
        AssertNotNull(by, "Container.By returns non-null PartialKeyPath");
        AssertTrue(by is global::Swift.PartialKeyPath<BoxKP>, "Container.By is typed PartialKeyPath<BoxKP>");
    }

    public void TestKeyPathGenericContainer_LookupAccessorReturnsTypedPartialKeyPath()
    {
        using var seedPath = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var container = new KeyPathGenericContainer<BoxKP>(seedPath);
        using var lookup = container.Lookup;
        AssertNotNull(lookup, "Container.Lookup returns non-null PartialKeyPath");
        AssertTrue(lookup is global::Swift.PartialKeyPath<BoxKP>, "Container.Lookup is typed PartialKeyPath<BoxKP>");
    }

    public void TestKeyPathGenericTypedSort_KeyPathArityTwoCtor()
    {
        using var seedPath = KeyPathFactory.MakePointXPath();
        using var sort = new KeyPathGenericTypedSort<PointKP>(seedPath);
        using var kp = sort.Kp;
        AssertNotNull(kp, "TypedSort.Kp returns non-null KeyPath");
        AssertTrue(kp is global::Swift.KeyPath<PointKP, nint>, "TypedSort.Kp is typed KeyPath<PointKP, nint>");
    }

    public void TestKeyPathGenericWritableSort_WritableKeyPathArityTwoCtor()
    {
        using var seedPath = KeyPathFactory.MakeWritablePointXPath();
        using var sort = new KeyPathGenericWritableSort<PointKP>(seedPath);
        using var kp = sort.Kp;
        AssertNotNull(kp, "WritableSort.Kp returns non-null WritableKeyPath");
        AssertTrue(kp is global::Swift.WritableKeyPath<PointKP, nint>, "WritableSort.Kp is typed WritableKeyPath<PointKP, nint>");
    }

    public void TestKeyPathGenericRefSort_ReferenceWritableKeyPathArityTwoCtor()
    {
        using var seedPath = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var sort = new KeyPathGenericRefSort<BoxKP>(seedPath);
        using var kp = sort.Kp;
        AssertNotNull(kp, "RefSort.Kp returns non-null ReferenceWritableKeyPath");
        AssertTrue(kp is global::Swift.ReferenceWritableKeyPath<BoxKP, nint>, "RefSort.Kp is typed ReferenceWritableKeyPath<BoxKP, nint>");
    }
}
