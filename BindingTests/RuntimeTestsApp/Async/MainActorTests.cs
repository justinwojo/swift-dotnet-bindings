// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for @MainActor isolation sync gate lift.
/// @MainActor members are exposed as synchronous C# APIs following the Xamarin.iOS precedent.
/// Consumer is responsible for calling from the main thread.
///
/// MainActorViewModel is a class — constructor and all instance access triggers
/// SwiftObjectHelper&lt;T&gt;.GetTypeMetadata() which crashes Mono JIT.
/// MainActorMethods is a struct — also uses GetTypeMetadata() for payload size.
/// Free function uses Cdecl wrapper — works on simulator.
/// </summary>
public class MainActorTests : TestBase
{
    public MainActorTests(TestResults results) : base(results) { }

    #region MainActorViewModel (class)

    public void TestMainActorViewModel_Constructor()
    {
        var vm = new MainActorViewModel("Test");
        AssertNotNull(vm, "MainActorViewModel constructor should succeed");
        vm.Dispose();
    }

    public void TestMainActorViewModel_Increment()
    {
        var vm = new MainActorViewModel("Test");
        var result = vm.Increment();
        AssertEqual(1, result, "First increment should return 1");
        vm.Dispose();
    }

    public void TestMainActorViewModel_SummaryProperty()
    {
        var vm = new MainActorViewModel("Hello");
        var summary = vm.Summary;
        AssertEqual("Hello: 0", summary, "Summary should be 'title: count'");
        vm.Dispose();
    }

    public void TestMainActorViewModel_TitleProperty()
    {
        var vm = new MainActorViewModel("MyTitle");
        var title = vm.Title;
        AssertEqual("MyTitle", title, "Title should match constructor arg");
        vm.Dispose();
    }

    public void TestMainActorViewModel_CountProperty()
    {
        var vm = new MainActorViewModel("Test");
        var count = vm.Count;
        AssertEqual(0, count, "Initial count should be 0");
        vm.Dispose();
    }

    #endregion

    #region MainActorMethods (struct)

    public void TestMainActorMethods_MainActorMethod()
    {
        var methods = new MainActorMethods(42);
        var result = methods.GetMainActorMethod();
        AssertEqual("MainActor: 42", result, "MainActorMethod should include value");
        methods.Dispose();
    }

    public void TestMainActorMethods_RegularMethod()
    {
        var methods = new MainActorMethods(42);
        var result = methods.GetRegularMethod();
        AssertEqual("Regular: 42", result, "RegularMethod should include value");
        methods.Dispose();
    }

    #endregion

    #region MainActorService (class with nonisolated + subscript)

    public void TestMainActorService_Constructor()
    {
        var svc = new MainActorService("Test");
        AssertNotNull(svc, "MainActorService constructor should succeed");
        svc.Dispose();
    }

    public void TestMainActorService_Describe()
    {
        var svc = new MainActorService("MyService");
        var result = svc.GetDescribe();
        AssertEqual("Service: MyService", result, "Describe should include name");
        svc.Dispose();
    }

    public void TestMainActorService_NonisolatedIdentifier()
    {
        var svc = new MainActorService("Test");
        var result = svc.GetIdentifier();
        AssertEqual("service", result, "nonisolated identifier should return 'service'");
        svc.Dispose();
    }

    public void TestMainActorService_Subscript()
    {
        var svc = new MainActorService("Items");
        var result = svc[2];
        AssertEqual("Items[2]", result, "Subscript should return 'name[index]'");
        svc.Dispose();
    }

    public void TestMainActorService_ClosureMethod()
    {
        var svc = new MainActorService("Hello");
        var result = svc.ApplyTransform((n) => n * 2);
        // "Hello" has 5 chars, transform doubles it
        AssertEqual(10, result, "Closure method on @MainActor class should work");
        svc.Dispose();
    }

    #endregion

    #region MainActorGuard (F41 — Debug-only main-thread guard)

    public void TestMainActorGuard_OnMainThread_DoesNotThrow()
    {
        // The runtime harness drives tests on the main thread, so a @MainActor member must succeed —
        // the emitted MainActorGuard.AssertMainThread() passes and the call returns normally.
        var vm = new MainActorViewModel("Guard");
        var result = vm.Increment();
        AssertEqual(1, result, "@MainActor member should succeed on the main thread with the guard present");
        vm.Dispose();
    }

    // The guard is [Conditional("DEBUG")] in Swift.Runtime; the Simulator app builds Debug, so it is
    // active here, but the NativeAOT device app builds Release and compiles the guard out entirely.
    // The Mono full-AOT device lane (--device --mono-aot) builds Debug, so the guard IS live there
    // and this test runs — [SkipOnDevice] is scoped to the NativeAOT lane precisely so it isn't
    // suppressed on a lane where it works.
    [SkipOnDevice("MainActorGuard is [Conditional(\"DEBUG\")]; the device app is a Release build with the guard compiled out, so an off-main-thread call does not throw.")]
    public void TestMainActorGuard_OffMainThread_Throws()
    {
        var vm = new MainActorViewModel("Guard");

        // Drive the @MainActor member from a dedicated background thread and capture whatever it
        // raises. The guard's pthread_main_np() check must observe the non-main thread and throw.
        Exception? captured = null;
        var worker = new Thread(() =>
        {
            try { vm.Increment(); }
            catch (Exception ex) { captured = ex; }
        });
        worker.Start();
        worker.Join();

        AssertNotNull(captured, "Calling a @MainActor member off the main thread should trip the Debug guard");
        AssertTrue(
            captured is InvalidOperationException,
            $"Off-main-thread @MainActor call should throw InvalidOperationException, got {captured?.GetType().Name ?? "null"}");
        vm.Dispose();
    }

    #endregion

    #region Free function — works on simulator

    public void TestMainActorFreeFunction()
    {
        var result = Functions.GetMainActorFreeFunction();
        AssertEqual("MainActor free function", result, "Free function should return expected string");
    }

    #endregion
}
