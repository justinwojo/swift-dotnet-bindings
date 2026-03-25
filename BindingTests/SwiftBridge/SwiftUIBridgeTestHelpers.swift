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
// All views use the State/Wrapper pattern (Session 5: always-wrapper):
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

// MARK: - UpdatableCounterView helpers (State/Wrapper pattern, Session 4A)

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

// MARK: - UpdatableMixedView helpers (State/Wrapper pattern, Session 4A)

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
