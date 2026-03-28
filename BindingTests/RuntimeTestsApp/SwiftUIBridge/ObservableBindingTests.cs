// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Tests for Session 6 observable binding: INotifyPropertyChanged ViewModel
/// auto-dispatches property changes to Swift state updates.
/// </summary>
public class ObservableBindingTests : TestBase
{
    public ObservableBindingTests(TestResults results) : base(results) { }

    public unsafe void TestBindTo_PropertyChange_DispatchesToSwift()
    {
        var labelBytes = Encoding.UTF8.GetBytes("Start");
        fixed (byte* labelPtr = labelBytes)
        {
            var session = SwiftBindingsTestLib.UpdatableCounterViewSession.Create(
                count: 0, label: "Start");

            var vm = new CounterViewModel { Count = 0, Label = "Start" };
            session.BindTo(vm);

            // Change count — should auto-dispatch to UpdateCount
            vm.Count = 42;
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.2));

            var count = BridgeTestHelpers.UpdatableCounterView_GetCount(session.Handle);
            AssertEqual(42, count, "BindTo count dispatched to Swift");

            session.Dispose();
        }
        TestLogger.Info("BindTo property dispatch: passed");
    }

    public unsafe void TestBindTo_StringProperty_DispatchesToSwift()
    {
        var session = SwiftBindingsTestLib.UpdatableCounterViewSession.Create(
            count: 0, label: "old");

        var vm = new CounterViewModel { Count = 0, Label = "old" };
        session.BindTo(vm);

        // Change label — should auto-dispatch to UpdateLabel
        vm.Label = "new label";
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.2));

        var labelLen = BridgeTestHelpers.UpdatableCounterView_GetLabelLength(session.Handle);
        AssertEqual(9, labelLen, "BindTo label dispatched to Swift");

        session.Dispose();
        TestLogger.Info("BindTo string dispatch: passed");
    }

    public void TestUnbind_StopsDispatching()
    {
        var session = SwiftBindingsTestLib.UpdatableCounterViewSession.Create(
            count: 0, label: "test");

        var vm = new CounterViewModel { Count = 0, Label = "test" };
        session.BindTo(vm);

        // Update via binding
        vm.Count = 10;
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.2));
        var count = BridgeTestHelpers.UpdatableCounterView_GetCount(session.Handle);
        AssertEqual(10, count, "Unbind: bound update works");

        // Unbind, then change — should NOT dispatch
        session.Unbind();
        vm.Count = 99;
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.2));
        count = BridgeTestHelpers.UpdatableCounterView_GetCount(session.Handle);
        AssertEqual(10, count, "Unbind: no dispatch after unbind");

        session.Dispose();
        TestLogger.Info("Unbind stops dispatching: passed");
    }

    public void TestDispose_NoCrashOnSubsequentVmChanges()
    {
        var session = SwiftBindingsTestLib.UpdatableCounterViewSession.Create(
            count: 0, label: "test");

        var vm = new CounterViewModel { Count = 0, Label = "test" };
        session.BindTo(vm);

        // Dispose should call Unbind
        session.Dispose();

        // Changing VM after dispose should NOT crash (handler was unsubscribed)
        vm.Count = 100;
        vm.Label = "after dispose";
        Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.1));

        // If we get here, no crash occurred
        TestLogger.Info("Dispose + VM changes no crash: passed");
    }
}

/// <summary>
/// Simple INotifyPropertyChanged implementation for testing observable binding.
/// </summary>
public class CounterViewModel : INotifyPropertyChanged
{
    private int _count;
    private string _label = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }
}

#endif
