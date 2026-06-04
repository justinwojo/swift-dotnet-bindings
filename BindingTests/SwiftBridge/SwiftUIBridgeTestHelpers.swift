// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Test-only helper functions for SwiftBindingsTestLib bridge validation.
// These are NOT auto-generated — they provide test hooks
// into the auto-generated bridge session classes.

import UIKit
import SwiftUI
import SwiftBindingsTestLib

// MARK: - Session extensions (coupled to generated field names)
// If the emitter renames internal fields, only this section needs updating.
//
// All views use the State/Wrapper pattern (always-wrapper):
//   hostingController.rootView → Wrapper type
//   Closures are stored as properties on the Wrapper
//   Updatable/optional params are stored on state via session.state.{prop}

extension SBW_SwiftBindingsTestLib_TypedClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_TypedClosureView_Wrapper { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_MultiArgClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_MultiArgClosureView_Wrapper { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_StringClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_StringClosureView_Wrapper { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_ClassClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_ClassClosureView_Wrapper { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_OptionalClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_OptionalClosureView_Wrapper { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_PlaceholderOnlyView_Session {
    var rootView: SBW_SwiftBindingsTestLib_PlaceholderOnlyView_Wrapper { hostingController.rootView }
}

// MARK: - SimpleModel helpers

/// Create a SimpleModel, return opaque pointer via Unmanaged.passRetained() (caller owns +1 retain).
@_cdecl("SBW_TEST_CreateSimpleModel")
public func SBW_TEST_CreateSimpleModel(_ value: Int32) -> UnsafeMutableRawPointer {
    let model = SimpleModel(value: value)
    return Unmanaged.passRetained(model).toOpaque()
}

/// Consumes the +1 retain from SBW_TEST_CreateSimpleModel.
@_cdecl("SBW_TEST_FreeSimpleModel")
public func SBW_TEST_FreeSimpleModel(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<SimpleModel>.fromOpaque(ptr).release()
}

/// Read SimpleModel.deinitCount for lifetime validation.
@_cdecl("SBW_TEST_GetSimpleModelDeinitCount")
public func SBW_TEST_GetSimpleModelDeinitCount() -> Int32 {
    return SimpleModel.deinitCount
}

/// Reset deinit counter to 0.
@_cdecl("SBW_TEST_ResetSimpleModelDeinitCount")
public func SBW_TEST_ResetSimpleModelDeinitCount() {
    SimpleModel.deinitCount = 0
}

// MARK: - EnumParamView helpers (State/Wrapper pattern)

/// Read stored enum raw value via the session's state.
@_cdecl("SBW_TEST_EnumParamView_GetStyle")
public func SBW_TEST_EnumParamView_GetStyle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_EnumParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_EnumParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.style.rawValue
    }
}

// MARK: - ClassParamView helpers (State/Wrapper pattern)

/// Read model.value via the session's state.
@_cdecl("SBW_TEST_ClassParamView_GetModelValue")
public func SBW_TEST_ClassParamView_GetModelValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ClassParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ClassParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.model.getValue()
    }
}

// MARK: - TypedClosureView helpers (always-wrapper, closure on Wrapper)

/// Invoke the View's onValue closure via rootView.
@_cdecl("SBW_TEST_TypedClosureView_InvokeClosure")
public func SBW_TEST_TypedClosureView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ value: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_TypedClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_TypedClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let result = session.rootView.onValue(value)
        return result ? 1 : 0
    }
}

// MARK: - MultiArgClosureView helpers (always-wrapper, closure on Wrapper)

/// Invoke the View's onEvent closure via rootView.
@_cdecl("SBW_TEST_MultiArgClosureView_InvokeClosure")
public func SBW_TEST_MultiArgClosureView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ val: Int32, _ flag: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MultiArgClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MultiArgClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.onEvent(val, flag != 0)
        return 1  // success indicator
    }
}

// MARK: - MixedParamView helpers (State/Wrapper pattern)

/// Read enum from mixed session via the state's style property.
@_cdecl("SBW_TEST_MixedParamView_GetStyle")
public func SBW_TEST_MixedParamView_GetStyle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.style.rawValue
    }
}

/// Invoke the mixed view's onAction closure via the wrapper's rootView.
@_cdecl("SBW_TEST_MixedParamView_FireAction")
public func SBW_TEST_MixedParamView_FireAction(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.hostingController.rootView.onAction()
        return 1
    }
}

/// Read count from mixed session via the state's count property.
@_cdecl("SBW_TEST_MixedParamView_GetCount")
public func SBW_TEST_MixedParamView_GetCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.count
    }
}

// MARK: - OptionalEnumView helpers (State/Wrapper pattern)

/// Return 1 if enum present, 0 if nil.
@_cdecl("SBW_TEST_OptionalEnumView_HasValue")
public func SBW_TEST_OptionalEnumView_HasValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalEnumView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalEnumView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.style != nil ? 1 : 0
    }
}

/// Read the optional enum raw value (returns -1 if nil).
@_cdecl("SBW_TEST_OptionalEnumView_GetStyle")
public func SBW_TEST_OptionalEnumView_GetStyle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalEnumView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalEnumView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.style?.rawValue ?? -1
    }
}

// MARK: - OptionalClassView helpers (State/Wrapper pattern)

/// Return 1 if model present, 0 if nil.
@_cdecl("SBW_TEST_OptionalClassView_HasValue")
public func SBW_TEST_OptionalClassView_HasValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalClassView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalClassView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.model != nil ? 1 : 0
    }
}

/// Read the optional model's value (returns -1 if nil).
@_cdecl("SBW_TEST_OptionalClassView_GetModelValue")
public func SBW_TEST_OptionalClassView_GetModelValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalClassView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalClassView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.model?.getValue() ?? -1
    }
}

// MARK: - StringClosureView helpers (always-wrapper, closure on Wrapper)

/// Invoke the View's onResult closure with a test string.
@_cdecl("SBW_TEST_StringClosureView_InvokeClosure")
public func SBW_TEST_StringClosureView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ value: UnsafePointer<UInt8>?, _ len: Int) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_StringClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_StringClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let str: String
        if let value = value, len > 0 {
            str = String(bytes: UnsafeBufferPointer(start: value, count: len), encoding: .utf8) ?? ""
        } else {
            str = ""
        }
        session.rootView.onResult(str)
        return 1
    }
}

// MARK: - ClassClosureView helpers (always-wrapper, closure on Wrapper)

/// Invoke the View's onModel closure with a SimpleModel pointer.
@_cdecl("SBW_TEST_ClassClosureView_InvokeClosure")
public func SBW_TEST_ClassClosureView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ modelPtr: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ClassClosureView_liveHandles.contains(handle),
              let modelPtr = modelPtr else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ClassClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let model = Unmanaged<SimpleModel>.fromOpaque(modelPtr).takeUnretainedValue()
        session.rootView.onModel(model)
        return 1
    }
}

// MARK: - OptionalStringView helpers (State/Wrapper pattern)

/// Return 1 if title is present, 0 if nil.
@_cdecl("SBW_TEST_OptionalStringView_HasValue")
public func SBW_TEST_OptionalStringView_HasValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalStringView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalStringView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.title != nil ? 1 : 0
    }
}

/// Read the title string length (returns -1 if nil).
@_cdecl("SBW_TEST_OptionalStringView_GetTitleLength")
public func SBW_TEST_OptionalStringView_GetTitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalStringView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalStringView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        guard let title = session.state.title else { return -1 }
        return Int32(title.count)
    }
}

// MARK: - OptionalClosureView helpers (always-wrapper, closure on Wrapper)

/// Invoke the View's callback closure via Wrapper (always-wrapper: closure is non-optional on Wrapper).
@_cdecl("SBW_TEST_OptionalClosureView_InvokeClosure")
public func SBW_TEST_OptionalClosureView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ value: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.callback(value)
        return 1
    }
}

// MARK: - MixedStringView helpers (State/Wrapper pattern)

/// Read the title string length from MixedStringView via state.
@_cdecl("SBW_TEST_MixedStringView_GetTitleLength")
public func SBW_TEST_MixedStringView_GetTitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedStringView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedStringView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.title.utf8.count)
    }
}

/// Invoke the MixedStringView's onResult closure via the wrapper's rootView.
@_cdecl("SBW_TEST_MixedStringView_InvokeClosure")
public func SBW_TEST_MixedStringView_InvokeClosure(_ handle: UnsafeMutableRawPointer?, _ value: UnsafePointer<UInt8>?, _ len: Int) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedStringView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedStringView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let str: String
        if let value = value, len > 0 {
            str = String(bytes: UnsafeBufferPointer(start: value, count: len), encoding: .utf8) ?? ""
        } else {
            str = ""
        }
        session.hostingController.rootView.onResult(str)
        return 1
    }
}

// MARK: - StringReturnClosureView helpers (always-wrapper, closure on Wrapper)

extension SBW_SwiftBindingsTestLib_StringReturnClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_StringReturnClosureView_Wrapper { hostingController.rootView }
}

/// Invoke the View's transformer closure with a value, return the result string length.
@_cdecl("SBW_TEST_StringReturnClosureView_InvokeTransformer")
public func SBW_TEST_StringReturnClosureView_InvokeTransformer(_ handle: UnsafeMutableRawPointer?, _ value: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_StringReturnClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_StringReturnClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let result = session.rootView.transformer(value)
        return Int32(result.utf8.count)
    }
}

// MARK: - ClassReturnClosureView helpers (always-wrapper, closure on Wrapper)

extension SBW_SwiftBindingsTestLib_ClassReturnClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_ClassReturnClosureView_Wrapper { hostingController.rootView }
}

/// Invoke the View's factory closure with a value, return model.getValue().
@_cdecl("SBW_TEST_ClassReturnClosureView_InvokeFactory")
public func SBW_TEST_ClassReturnClosureView_InvokeFactory(_ handle: UnsafeMutableRawPointer?, _ value: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ClassReturnClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ClassReturnClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let model = session.rootView.factory(value)
        return model.getValue()
    }
}

// MARK: - ModifiableView helpers (State/Wrapper pattern, custom modifiers)

/// Read mod_highlighted state (1=true, 0=false).
@_cdecl("SBW_TEST_ModifiableView_GetHighlighted")
public func SBW_TEST_ModifiableView_GetHighlighted(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ModifiableView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ModifiableView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.mod_highlighted ? 1 : 0
    }
}

/// Read mod_enabled state (1=true, 0=false, -1=nil).
@_cdecl("SBW_TEST_ModifiableView_GetModEnabled")
public func SBW_TEST_ModifiableView_GetModEnabled(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ModifiableView_liveHandles.contains(handle) else { return -2 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ModifiableView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        guard let val = session.state.mod_enabled else { return -1 }
        return val ? 1 : 0
    }
}

// MARK: - LifecycleTestView helpers (State/Wrapper pattern, lifecycle callbacks)

/// Fire the stored onAppear callback (1=fired, 0=no callback registered, -1=invalid handle).
@_cdecl("SBW_TEST_LifecycleTestView_FireOnAppear")
public func SBW_TEST_LifecycleTestView_FireOnAppear(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_LifecycleTestView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_LifecycleTestView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        guard let onAppear = session.state.lifecycleOnAppear else { return 0 }
        onAppear()
        return 1
    }
}

/// Fire the stored onDisappear callback (1=fired, 0=no callback registered, -1=invalid handle).
@_cdecl("SBW_TEST_LifecycleTestView_FireOnDisappear")
public func SBW_TEST_LifecycleTestView_FireOnDisappear(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_LifecycleTestView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_LifecycleTestView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        guard let onDisappear = session.state.lifecycleOnDisappear else { return 0 }
        onDisappear()
        return 1
    }
}

// MARK: - GenericPlaceholderView helpers (State/Wrapper pattern)

/// Read the title string length from a GenericPlaceholderView session via state.
@_cdecl("SBW_TEST_GenericPlaceholderView_GetTitleLength")
public func SBW_TEST_GenericPlaceholderView_GetTitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_GenericPlaceholderView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_GenericPlaceholderView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.title.utf8.count)
    }
}

// MARK: - PlaceholderOnlyView helpers (always-wrapper, no user params)

/// Verify PlaceholderOnlyView session was created (returns 1 on success).
@_cdecl("SBW_TEST_PlaceholderOnlyView_IsAlive")
public func SBW_TEST_PlaceholderOnlyView_IsAlive(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_PlaceholderOnlyView_liveHandles.contains(handle) else { return 0 }
        return 1
    }
}

// MARK: - UpdatableCounterView helpers (State/Wrapper pattern)

/// Read count from UpdatableCounterView via state.
@_cdecl("SBW_TEST_UpdatableCounterView_GetCount")
public func SBW_TEST_UpdatableCounterView_GetCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UpdatableCounterView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UpdatableCounterView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.count
    }
}

/// Read label string length from UpdatableCounterView via state.
@_cdecl("SBW_TEST_UpdatableCounterView_GetLabelLength")
public func SBW_TEST_UpdatableCounterView_GetLabelLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UpdatableCounterView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UpdatableCounterView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.label.utf8.count)
    }
}

// MARK: - UpdatableMixedView helpers (State/Wrapper pattern)

/// Read title string length from UpdatableMixedView via state.
@_cdecl("SBW_TEST_UpdatableMixedView_GetTitleLength")
public func SBW_TEST_UpdatableMixedView_GetTitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UpdatableMixedView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UpdatableMixedView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.title.utf8.count)
    }
}

/// Read isEnabled from UpdatableMixedView via state (1=true, 0=false).
@_cdecl("SBW_TEST_UpdatableMixedView_GetIsEnabled")
public func SBW_TEST_UpdatableMixedView_GetIsEnabled(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UpdatableMixedView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UpdatableMixedView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.isEnabled ? 1 : 0
    }
}

/// Invoke the UpdatableMixedView's onTap closure via the wrapper's rootView.
@_cdecl("SBW_TEST_UpdatableMixedView_FireOnTap")
public func SBW_TEST_UpdatableMixedView_FireOnTap(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UpdatableMixedView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UpdatableMixedView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.hostingController.rootView.onTap()
        return 1
    }
}

// MARK: - Validation Pattern Views: Session extensions

extension SBW_SwiftBindingsTestLib_FormatMenuView_Session {
    var rootView: SBW_SwiftBindingsTestLib_FormatMenuView_Wrapper { hostingController.rootView }
}

// MARK: - BindingToggleView helpers (Binding<Bool> param gate)

/// Read the isOn Bool state from BindingToggleView (1=true, 0=false).
@_cdecl("SBW_TEST_BindingToggleView_GetIsOn")
public func SBW_TEST_BindingToggleView_GetIsOn(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_BindingToggleView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_BindingToggleView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.isOn ? 1 : 0
    }
}

// MARK: - NumberListView helpers (Array<Int> param gate)

/// Read the count of the numbers array from NumberListView.
@_cdecl("SBW_TEST_NumberListView_GetCount")
public func SBW_TEST_NumberListView_GetCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_NumberListView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_NumberListView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.hostingController.rootView.numbers.count)
    }
}

/// Read a specific element from the numbers array.
@_cdecl("SBW_TEST_NumberListView_GetElement")
public func SBW_TEST_NumberListView_GetElement(_ handle: UnsafeMutableRawPointer?, _ index: Int32) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_NumberListView_liveHandles.contains(handle) else { return -999 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_NumberListView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let numbers = session.hostingController.rootView.numbers
        guard index >= 0, index < numbers.count else { return -999 }
        return numbers[Int(index)]
    }
}

// MARK: - SymbolIconView helpers (SwiftUI.Image param gate)

/// Read the icon string length from SymbolIconView's state.
@_cdecl("SBW_TEST_SymbolIconView_GetIconLength")
public func SBW_TEST_SymbolIconView_GetIconLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_SymbolIconView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_SymbolIconView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.icon.utf8.count)
    }
}

// MARK: - TransformOutcome helpers (BoundStruct creation for C# tests)

/// Create a TransformOutcome.completed(result:) and return an opaque pointer.
/// C# passes this to FormatActionView_Create. Caller must free with SBW_TEST_FreeTransformOutcome.
@_cdecl("SBW_TEST_CreateTransformOutcome_Completed")
public func SBW_TEST_CreateTransformOutcome_Completed(_ result: Int32) -> UnsafeMutableRawPointer {
    let outcome = TransformOutcome.completed(result: result)
    let ptr = UnsafeMutableRawPointer.allocate(
        byteCount: MemoryLayout<TransformOutcome>.size,
        alignment: MemoryLayout<TransformOutcome>.alignment)
    ptr.initializeMemory(as: TransformOutcome.self, repeating: outcome, count: 1)
    return ptr
}

/// Create a TransformOutcome.failed(errorCode:) and return an opaque pointer.
@_cdecl("SBW_TEST_CreateTransformOutcome_Failed")
public func SBW_TEST_CreateTransformOutcome_Failed(_ errorCode: Int32) -> UnsafeMutableRawPointer {
    let outcome = TransformOutcome.failed(errorCode: errorCode)
    let ptr = UnsafeMutableRawPointer.allocate(
        byteCount: MemoryLayout<TransformOutcome>.size,
        alignment: MemoryLayout<TransformOutcome>.alignment)
    ptr.initializeMemory(as: TransformOutcome.self, repeating: outcome, count: 1)
    return ptr
}

/// Free a TransformOutcome pointer allocated by the above helpers.
@_cdecl("SBW_TEST_FreeTransformOutcome")
public func SBW_TEST_FreeTransformOutcome(_ ptr: UnsafeMutableRawPointer?) {
    guard let ptr = ptr else { return }
    ptr.assumingMemoryBound(to: TransformOutcome.self).deinitialize(count: 1)
    ptr.deallocate()
}

// MARK: - FormatActionView helpers (non-raw-value enum / BoundStruct param)

/// Read the stored TransformOutcome value from FormatActionView's state.
/// Returns the associated Int32 value (result for .completed, errorCode for .failed).
@_cdecl("SBW_TEST_FormatActionView_GetOutcomeValue")
public func SBW_TEST_FormatActionView_GetOutcomeValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_FormatActionView_liveHandles.contains(handle) else { return -999 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_FormatActionView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return outcomeValue(session.state.action)
    }
}

/// Check if FormatActionView's stored outcome is .completed (1) or .failed (0).
@_cdecl("SBW_TEST_FormatActionView_IsCompleted")
public func SBW_TEST_FormatActionView_IsCompleted(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_FormatActionView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_FormatActionView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return outcomeIsCompleted(session.state.action) ? 1 : 0
    }
}

// MARK: - FormatMenuView helpers (closure with BoundStruct arg)

/// Invoke FormatMenuView's onFormat closure with a constructed TransformOutcome.
/// isCompleted=1 → .completed(result: value), isCompleted=0 → .failed(errorCode: value)
@_cdecl("SBW_TEST_FormatMenuView_InvokeOnFormat")
public func SBW_TEST_FormatMenuView_InvokeOnFormat(
    _ handle: UnsafeMutableRawPointer?,
    _ isCompleted: Int32,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_FormatMenuView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_FormatMenuView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let outcome: TransformOutcome = isCompleted != 0
            ? .completed(result: value)
            : .failed(errorCode: value)
        session.rootView.onFormat(outcome)
        return 1
    }
}

// MARK: - PlayerStyleView helpers (class + string params)

/// Read the player model's value from PlayerStyleView's state.
@_cdecl("SBW_TEST_PlayerStyleView_GetPlayerValue")
public func SBW_TEST_PlayerStyleView_GetPlayerValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_PlayerStyleView_liveHandles.contains(handle) else { return -999 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_PlayerStyleView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.player.getValue()
    }
}

// MARK: - ResultCompletionView helpers (Result<T,E> closure param)

extension SBW_SwiftBindingsTestLib_ResultCompletionView_Session {
    var rootView: SBW_SwiftBindingsTestLib_ResultCompletionView_Wrapper { hostingController.rootView }
}

/// Invoke the completion closure with .success(SimpleModel(value:)).
/// Returns 1 on success, -1 if handle invalid.
@_cdecl("SBW_TEST_ResultCompletionView_InvokeSuccess")
public func SBW_TEST_ResultCompletionView_InvokeSuccess(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ResultCompletionView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ResultCompletionView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let model = SimpleModel(value: value)
        session.rootView.completion(.success(model))
        return 1
    }
}

/// Invoke the completion closure with .failure(ScanError(code:)).
/// Returns 1 on success, -1 if handle invalid.
@_cdecl("SBW_TEST_ResultCompletionView_InvokeError")
public func SBW_TEST_ResultCompletionView_InvokeError(
    _ handle: UnsafeMutableRawPointer?,
    _ errorCode: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ResultCompletionView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ResultCompletionView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let error = ScanError(code: errorCode)
        session.rootView.completion(.failure(error))
        return 1
    }
}

// MARK: - ResultWithStructView helpers (Result<BoundType, BoundStruct> closure)

extension SBW_SwiftBindingsTestLib_ResultWithStructView_Session {
    var rootView: SBW_SwiftBindingsTestLib_ResultWithStructView_Wrapper { hostingController.rootView }
}

/// Invoke completion with .success(SimpleModel(value:)).
@_cdecl("SBW_TEST_ResultWithStructView_InvokeSuccess")
public func SBW_TEST_ResultWithStructView_InvokeSuccess(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ResultWithStructView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ResultWithStructView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let model = SimpleModel(value: value)
        session.rootView.completion(.success(model))
        return 1
    }
}

/// Invoke completion with .failure(DetailedError.validation(code:)).
@_cdecl("SBW_TEST_ResultWithStructView_InvokeError")
public func SBW_TEST_ResultWithStructView_InvokeError(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ResultWithStructView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ResultWithStructView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let error = DetailedError.validation(code: value)
        session.rootView.completion(.failure(error))
        return 1
    }
}

// MARK: - RichToolbarView helpers (dual string params)

/// Read the title length from RichToolbarView's state.
@_cdecl("SBW_TEST_RichToolbarView_GetTitleLength")
public func SBW_TEST_RichToolbarView_GetTitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_RichToolbarView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_RichToolbarView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.title.utf8.count)
    }
}

/// Read the subtitle length from RichToolbarView's state.
@_cdecl("SBW_TEST_RichToolbarView_GetSubtitleLength")
public func SBW_TEST_RichToolbarView_GetSubtitleLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_RichToolbarView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_RichToolbarView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.subtitle.utf8.count)
    }
}

// MARK: - Audit Session 5 helpers
//
// Test hooks for the SwiftUI-bridge defect fixtures in AuditSession5Views.swift.
// The closure-firing helpers call the closure stored on the Wrapper, which IS the
// generated decomposition closure built in the Session init — so they exercise the
// real P1-19 (withExtendedLifetime) / P1-20 (heap-buffer initializeMemory) callback
// marshalling rather than bypassing it.

// MARK: UrlResultView (P1-19) — Result<URL, ScanError> closure

extension SBW_SwiftBindingsTestLib_UrlResultView_Session {
    var rootView: SBW_SwiftBindingsTestLib_UrlResultView_Wrapper { hostingController.rootView }
}

/// Fire onResult(.success(URL)) with a deterministic URL keyed by `value`.
/// The success payload is an ObjC-bridgeable struct (URL→NSURL); the generated
/// decomposition closure binds `value as AnyObject` and `withExtendedLifetime`s it
/// across the synchronous C callback. Without that guard the bridged NSURL could be
/// released before the C# callback reads absoluteString (the P1-19 use-after-free).
/// Returns 1 on success, -1 if the handle is invalid.
@_cdecl("SBW_TEST_UrlResultView_InvokeSuccess")
public func SBW_TEST_UrlResultView_InvokeSuccess(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UrlResultView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UrlResultView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let url = URL(string: "https://audit.example/url-result/\(value)")!
        session.rootView.onResult(.success(url))
        return 1
    }
}

/// Fire onResult(.failure(ScanError(code:))).
/// Returns 1 on success, -1 if the handle is invalid.
@_cdecl("SBW_TEST_UrlResultView_InvokeError")
public func SBW_TEST_UrlResultView_InvokeError(
    _ handle: UnsafeMutableRawPointer?,
    _ code: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UrlResultView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UrlResultView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.onResult(.failure(ScanError(code: code)))
        return 1
    }
}

// MARK: UrlClosureView (review) — typed (URL)->Void closure, ObjC-bridgeable struct arg

extension SBW_SwiftBindingsTestLib_UrlClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_UrlClosureView_Wrapper { hostingController.rootView }
}

/// Fire onPick(URL(string:)) with a deterministic URL keyed by `value`. The generated
/// decomposition closure bridges the URL to NSURL and delivers it as an object pointer
/// (Unmanaged.passUnretained, held by withExtendedLifetime); the C# trampoline reads it via
/// GetNSObject. A wrong path (heap-allocate raw struct bytes + MarshalFromSwift) would
/// reinterpret the object pointer as struct memory → garbage AbsoluteString or SIGSEGV.
/// Returns 1 on success, -1 if the handle is invalid.
@_cdecl("SBW_TEST_UrlClosureView_InvokeOnPick")
public func SBW_TEST_UrlClosureView_InvokeOnPick(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UrlClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UrlClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.onPick(URL(string: "https://audit.example/url-closure/\(value)")!)
        return 1
    }
}

// MARK: FrozenRefClosureView (P1-20) — @frozen struct w/ ref field as closure arg

extension SBW_SwiftBindingsTestLib_FrozenRefClosureView_Session {
    var rootView: SBW_SwiftBindingsTestLib_FrozenRefClosureView_Wrapper { hostingController.rootView }
}

/// Fire onEvent(FrozenRefArg(s:)) with a deterministic String keyed by `value`.
/// FrozenRefArg is a @frozen struct holding a String (ref-holding field); the generated
/// decomposition closure copies it into a heap buffer via initializeMemory (ARC-correct)
/// before the C callback and deinitializes/deallocates after (P1-20). The C# trampoline
/// reads back the .S field — a corrupt or leaked String there would prove the buffer
/// marshalling is wrong. Returns 1 on success, -1 if the handle is invalid.
@_cdecl("SBW_TEST_FrozenRefClosureView_InvokeOnEvent")
public func SBW_TEST_FrozenRefClosureView_InvokeOnEvent(
    _ handle: UnsafeMutableRawPointer?,
    _ value: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_FrozenRefClosureView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_FrozenRefClosureView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.onEvent(FrozenRefArg(s: "frozen-ref-\(value)"))
        return 1
    }
}

// MARK: UrlParamView (P0-04) — ObjC-bridgeable struct (URL) param

/// Return the UTF-8 byte length of the bridged URL's absoluteString, or -1 if the handle
/// is invalid. Lets the C# test confirm the URL crossed the Create ABI as an
/// ObjC-bridgeable struct param without truncation.
@_cdecl("SBW_TEST_UrlParamView_GetTargetLength")
public func SBW_TEST_UrlParamView_GetTargetLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_UrlParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_UrlParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.state.target.absoluteString.utf8.count)
    }
}

// MARK: OptionalUrlParamView (P0-04) — Optional<ObjC-bridgeable struct> param

/// Return the UTF-8 byte length of the bridged URL?'s absoluteString, -2 if the target is
/// nil, or -1 if the handle is invalid.
@_cdecl("SBW_TEST_OptionalUrlParamView_GetTargetLength")
public func SBW_TEST_OptionalUrlParamView_GetTargetLength(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalUrlParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalUrlParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        guard let target = session.state.target else { return -2 }
        return Int32(target.absoluteString.utf8.count)
    }
}

// MARK: ArrayEnumView (P0-03) — [BoundEnum] param

/// Return the number of decoded AlertStyle elements, or -1 if the handle is invalid.
@_cdecl("SBW_TEST_ArrayEnumView_GetCount")
public func SBW_TEST_ArrayEnumView_GetCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ArrayEnumView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ArrayEnumView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return Int32(session.styles.count)
    }
}

/// Return the rawValue of the AlertStyle at `index`, or -1 if the handle/index is invalid.
@_cdecl("SBW_TEST_ArrayEnumView_GetElement")
public func SBW_TEST_ArrayEnumView_GetElement(
    _ handle: UnsafeMutableRawPointer?,
    _ index: Int32
) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ArrayEnumView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ArrayEnumView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        let i = Int(index)
        guard i >= 0, i < session.styles.count else { return -1 }
        return session.styles[i].rawValue
    }
}

// MARK: HandleParamView (P1-22) — init params colliding with generated locals

/// Return the stored `handle` field, or Int32.min if the handle is invalid.
@_cdecl("SBW_TEST_HandleParamView_GetHandle")
public func SBW_TEST_HandleParamView_GetHandle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_HandleParamView_liveHandles.contains(handle) else { return Int32.min }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_HandleParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.handle
    }
}

/// Return the stored `session` field, or Int32.min if the handle is invalid.
@_cdecl("SBW_TEST_HandleParamView_GetSession")
public func SBW_TEST_HandleParamView_GetSession(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_HandleParamView_liveHandles.contains(handle) else { return Int32.min }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_HandleParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.state.session
    }
}
