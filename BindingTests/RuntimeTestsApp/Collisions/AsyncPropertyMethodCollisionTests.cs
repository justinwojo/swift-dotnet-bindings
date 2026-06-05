// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// P1-21 (property rename across the async + completion-handler paths): extends the property-rename
/// concern into emission paths that produce their OWN name shape. <c>AsyncPropertyMethodCollider</c>
/// has a stored property <c>data</c> and two overloads of <c>data(times:)</c> — a Swift-native
/// <c>async</c> one and a completion-handler one. Both must observe the <c>data</c> property rename,
/// so neither emits as bare <c>Data(...)</c> (which would collide with the property, CS0102).
///
/// Generated shapes verified against the wrappers:
///   - native async         -> <c>Task&lt;int&gt; DataMethodAsync(int times, CancellationToken)</c>
///                             Swift <c>data(times:) async</c>  -> data * times
///   - completion handler    -> <c>void DataMethod(int times, Action&lt;int&gt; completion)</c>
///                             Swift <c>data(times:completion:)</c> -> completion(data * times + 1)
/// The <c>Async</c> suffix already disambiguates the two, so no numeric suffix is needed — but BOTH
/// were renamed away from the <c>Data</c> property, which is the property-rename invariant under test.
/// </summary>
public class AsyncPropertyMethodCollisionTests : TestBase
{
    public AsyncPropertyMethodCollisionTests(TestResults results) : base(results) { }

    public void TestPropertyGetterUnaffected()
    {
        using var c = new AsyncPropertyMethodCollider(4);
        AssertEqual(4, c.Data, "stored property `data` projects to the `Data` getter");
    }

    public async Task TestNativeAsyncOverloadRenamedAndRoundTrips()
    {
        using var c = new AsyncPropertyMethodCollider(4);
        // DataMethodAsync is the renamed native-async `data(times:)` -> data * times.
        int result = await WithTimeout(c.DataMethodAsync(3), DefaultAsyncTimeout);
        AssertEqual(12, result, "DataMethodAsync(3) -> Swift data(times:) async = 4 * 3");
        TestLogger.Info($"DataMethodAsync = {result}");
    }

    public void TestCompletionHandlerOverloadRenamedAndRoundTrips()
    {
        using var c = new AsyncPropertyMethodCollider(4);
        // DataMethod is the renamed completion-handler `data(times:completion:)`.
        // The Swift body invokes completion synchronously with data * times + 1.
        int? captured = null;
        c.DataMethod(3, v => captured = v);
        AssertEqual(13, captured, "DataMethod(3, completion) -> Swift completion(data * times + 1) = 4*3 + 1");
        TestLogger.Info($"DataMethod completion = {captured}");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(AsyncPropertyMethodCollider))]
    public void TestNeitherOverloadCollidesWithDataProperty()
    {
        // The `Data` property exists; neither overload may be named `Data` (that would be CS0102).
        AssertNotNull(typeof(AsyncPropertyMethodCollider).GetProperty("Data"),
            "stored property projects to the Data getter");
        AssertNotNull(typeof(AsyncPropertyMethodCollider).GetMethod("DataMethodAsync"),
            "native-async overload renamed to DataMethodAsync");
        AssertNotNull(typeof(AsyncPropertyMethodCollider).GetMethod("DataMethod"),
            "completion-handler overload renamed to DataMethod");
        AssertNull(typeof(AsyncPropertyMethodCollider).GetMethod("Data"),
            "no method named Data — both overloads were renamed away from the property");
    }
}
