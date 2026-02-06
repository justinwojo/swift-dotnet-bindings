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

extension SBW_SwiftBindingsTestLib_EnumParamView_Session {
    var rootView: EnumParamView { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_ClassParamView_Session {
    var rootView: ClassParamView { hostingController.rootView }
    var storedModel: SimpleModel { model }
}

extension SBW_SwiftBindingsTestLib_TypedClosureView_Session {
    var rootView: TypedClosureView { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_MultiArgClosureView_Session {
    var rootView: MultiArgClosureView { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_MixedParamView_Session {
    var rootView: MixedParamView { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_OptionalEnumView_Session {
    var rootView: OptionalEnumView { hostingController.rootView }
}

extension SBW_SwiftBindingsTestLib_OptionalClassView_Session {
    var rootView: OptionalClassView { hostingController.rootView }
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

// MARK: - EnumParamView helpers

/// Read stored enum raw value via the View's style property.
@_cdecl("SBW_TEST_EnumParamView_GetStyle")
public func SBW_TEST_EnumParamView_GetStyle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_EnumParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_EnumParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.rootView.style.rawValue
    }
}

// MARK: - ClassParamView helpers

/// Read model.value via the session's stored model reference.
@_cdecl("SBW_TEST_ClassParamView_GetModelValue")
public func SBW_TEST_ClassParamView_GetModelValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_ClassParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_ClassParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.storedModel.getValue()
    }
}

// MARK: - TypedClosureView helpers

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

// MARK: - MultiArgClosureView helpers

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

// MARK: - MixedParamView helpers

/// Read enum from mixed session via the View's style property.
@_cdecl("SBW_TEST_MixedParamView_GetStyle")
public func SBW_TEST_MixedParamView_GetStyle(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.rootView.style.rawValue
    }
}

/// Invoke the mixed view's onAction closure via rootView.
@_cdecl("SBW_TEST_MixedParamView_FireAction")
public func SBW_TEST_MixedParamView_FireAction(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        session.rootView.onAction()
        return 1
    }
}

/// Read count from mixed session via the View's count property.
@_cdecl("SBW_TEST_MixedParamView_GetCount")
public func SBW_TEST_MixedParamView_GetCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_MixedParamView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_MixedParamView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.rootView.count
    }
}

// MARK: - OptionalEnumView helpers

/// Return 1 if enum present, 0 if nil.
@_cdecl("SBW_TEST_OptionalEnumView_HasValue")
public func SBW_TEST_OptionalEnumView_HasValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalEnumView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalEnumView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.rootView.style != nil ? 1 : 0
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
        return session.rootView.style?.rawValue ?? -1
    }
}

// MARK: - OptionalClassView helpers

/// Return 1 if model present, 0 if nil.
@_cdecl("SBW_TEST_OptionalClassView_HasValue")
public func SBW_TEST_OptionalClassView_HasValue(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    return SBW_onMainThread {
        guard let handle = handle,
              SBW_SwiftBindingsTestLib_OptionalClassView_liveHandles.contains(handle) else { return -1 }
        let session = Unmanaged<SBW_SwiftBindingsTestLib_OptionalClassView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        return session.rootView.model != nil ? 1 : 0
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
        return session.rootView.model?.getValue() ?? -1
    }
}
