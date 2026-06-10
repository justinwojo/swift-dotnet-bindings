// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests the AnimatedButton/AnimatedSwitch UIKit control hierarchy pattern:
/// - Class hierarchy with inherited play/stop behavior
/// - Bool state properties (isOn) with getter/setter
/// - State change methods with parameters
/// - Optional animation in constructor
///
/// Exercises L6 (AnimatedButton / AnimatedSwitch) from the library parity roadmap.
/// </summary>
public class ControlHierarchyTests : TestBase
{
    public ControlHierarchyTests(TestResults results) : base(results) { }

    #region AnimatedControlBase

    public void TestAnimatedControlBaseDefaultConstruction()
    {
        using var control = new AnimatedControlBase();
        AssertNull(control.AnimationName, "Default animation name is null");
        AssertApproxEqual(1.0, control.Speed, message: "Default speed is 1.0");
        AssertFalse(control.IsAnimating, "Not animating by default");
        TestLogger.Info("AnimatedControlBase default construction passed");
    }

    public void TestAnimatedControlBaseNamedConstruction()
    {
        using var control = new AnimatedControlBase(animationName: "bounce");
        AssertEqual("bounce", control.AnimationName, "Animation name set");
        TestLogger.Info($"AnimatedControlBase with name '{control.AnimationName}'");
    }

    public void TestAnimatedControlBasePlayStop()
    {
        using var control = new AnimatedControlBase();
        AssertFalse(control.IsAnimating, "Not animating initially");
        control.Play();
        AssertTrue(control.IsAnimating, "Animating after play");
        control.Stop();
        AssertFalse(control.IsAnimating, "Not animating after stop");
        TestLogger.Info("AnimatedControlBase play/stop cycle works");
    }

    public void TestAnimatedControlBaseSpeedProperty()
    {
        using var control = new AnimatedControlBase();
        control.Speed = 2.5;
        AssertApproxEqual(2.5, control.Speed, message: "Speed updated");
        TestLogger.Info($"AnimatedControlBase.Speed = {control.Speed}");
    }

    public void TestAnimatedControlBaseStateDescription()
    {
        using var control = new AnimatedControlBase(animationName: "fade");
        var desc = control.GetStateDescription();
        AssertTrue(desc.Contains("fade"), "Description contains animation name");
        AssertTrue(desc.Contains("false"), "Description shows not playing");
        TestLogger.Info($"GetStateDescription = '{desc}'");
    }

    #endregion

    #region ToggleSwitch (AnimatedSwitch pattern)

    public void TestToggleSwitchDefaultState()
    {
        using var toggle = new ToggleSwitch();
        AssertFalse(toggle.IsOn, "Toggle is off by default");
        AssertEqual(0, toggle.ChangeCount, "No changes yet");
        TestLogger.Info("ToggleSwitch default state: off, 0 changes");
    }

    public void TestToggleSwitchInitialState()
    {
        using var toggle = new ToggleSwitch(animationName: "switch", initialState: true);
        AssertTrue(toggle.IsOn, "Toggle is on (initial state)");
        AssertEqual("switch", toggle.AnimationName, "Animation name set");
        TestLogger.Info("ToggleSwitch with initial state=true");
    }

    public void TestToggleSwitchSetIsOn()
    {
        using var toggle = new ToggleSwitch();
        toggle.SetIsOn(true, false);
        AssertTrue(toggle.IsOn, "Toggle is on after SetIsOn(true)");
        AssertEqual(1, toggle.ChangeCount, "1 change recorded");
        AssertFalse(toggle.IsAnimating, "Not animating (animated=false)");
        TestLogger.Info("SetIsOn(true, animated:false) works");
    }

    public void TestToggleSwitchSetIsOnAnimated()
    {
        using var toggle = new ToggleSwitch();
        toggle.SetIsOn(true, true);
        AssertTrue(toggle.IsOn, "Toggle is on");
        AssertTrue(toggle.IsAnimating, "Animating (animated=true)");
        TestLogger.Info("SetIsOn(true, animated:true) triggers animation");
    }

    public void TestToggleSwitchSetIsOnNoChange()
    {
        using var toggle = new ToggleSwitch();
        toggle.SetIsOn(false, false); // same state
        AssertEqual(0, toggle.ChangeCount, "No change when state is same");
        TestLogger.Info("SetIsOn with same state doesn't count as change");
    }

    public void TestToggleSwitchMultipleToggles()
    {
        using var toggle = new ToggleSwitch();
        toggle.SetIsOn(true, false);
        toggle.SetIsOn(false, false);
        toggle.SetIsOn(true, false);
        AssertTrue(toggle.IsOn, "Final state is on");
        AssertEqual(3, toggle.ChangeCount, "3 state changes");
        TestLogger.Info("Multiple toggle cycles tracked correctly");
    }

    public void TestToggleSwitchProgressForState()
    {
        using var toggle = new ToggleSwitch();
        var onRange = toggle.SetProgressForState(0.0, 0.5, true);
        AssertTrue(onRange.Contains("on"), "On state range");
        var offRange = toggle.SetProgressForState(0.5, 1.0, false);
        AssertTrue(offRange.Contains("off"), "Off state range");
        TestLogger.Info($"Progress ranges: on='{onRange}', off='{offRange}'");
    }

    public void TestToggleSwitchIsOnPropertyDirect()
    {
        using var toggle = new ToggleSwitch();
        AssertFalse(toggle.IsOn, "Initially off");
        toggle.IsOn = true;
        AssertTrue(toggle.IsOn, "Set to on via property");
        AssertEqual(0, toggle.ChangeCount, "Direct property set doesn't increment changeCount");
        toggle.IsOn = false;
        AssertFalse(toggle.IsOn, "Set back to off");
        TestLogger.Info("ToggleSwitch.IsOn property direct set works");
    }

    #endregion

    #region TapButton (AnimatedButton pattern)

    public void TestTapButtonDefaultConstruction()
    {
        using var button = new TapButton();
        AssertEqual(0, button.TapCount, "No taps initially");
        AssertFalse(button.IsEnabled, "Disabled without animation");
        TestLogger.Info("TapButton default: 0 taps, disabled");
    }

    public void TestTapButtonWithAnimation()
    {
        using var button = new TapButton(animationName: "tap-effect");
        AssertTrue(button.IsEnabled, "Enabled with animation");
        AssertEqual("tap-effect", button.AnimationName, "Animation name");
        TestLogger.Info("TapButton with animation: enabled");
    }

    public void TestTapButtonPerformTap()
    {
        using var button = new TapButton(animationName: "tap");
        button.PerformTap();
        AssertEqual(1, button.TapCount, "1 tap");
        AssertTrue(button.IsAnimating, "Animating after tap");
        TestLogger.Info("TapButton.PerformTap triggers animation");
    }

    public void TestTapButtonMultipleTaps()
    {
        using var button = new TapButton(animationName: "tap");
        button.PerformTap();
        button.PerformTap();
        button.PerformTap();
        AssertEqual(3, button.TapCount, "3 taps recorded");
        TestLogger.Info("TapButton tracks multiple taps");
    }

    public void TestTapButtonInheritedPlayStop()
    {
        using var button = new TapButton(animationName: "tap");
        button.Play();
        AssertTrue(button.IsAnimating, "Animating after play");
        button.Stop();
        AssertFalse(button.IsAnimating, "Stopped after Stop()");
        TestLogger.Info("TapButton inherits Play/Stop from base");
    }

    #endregion
}
