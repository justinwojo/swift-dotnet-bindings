// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI Views replicating patterns from third-party validation libraries.
// These exercise bridge parameter gates added in Sessions 2-3 at runtime:
//
//   NoParamBlurView       → AlertToast.BlurView (zero-param init)
//   PlayerStyleView       → YouTubePlayerKit.YouTubePlayerView (class + string)
//   FormatActionView      → RichTextKit.ActionButton (non-raw-value enum)
//   FormatMenuView        → RichTextKit.Menu (closure with BoundStruct arg)
//   RichToolbarView       → RichTextKit toolbar views (dual string params)

import SwiftUI

// MARK: - Zero-Parameter Init (AlertToast BlurView pattern)

/// Replicates AlertToast.BlurView — simplest possible bridged view.
/// Zero parameters, zero state. Tests the bridge's no-param path.
public struct NoParamBlurView: View {
    public init() {}
    public var body: some View {
        Text("BlurView")
    }
}

// MARK: - Class + String Params (YouTubePlayerKit pattern)

/// Replicates YouTubePlayerView(player:) — class param with state control.
/// Tests BoundType parameter bridging with a stateful object + string title.
public struct PlayerStyleView: View {
    let player: SimpleModel
    let title: String

    public init(player: SimpleModel, title: String) {
        self.player = player
        self.title = title
    }

    public var body: some View {
        Text("\(title): \(player.getValue())")
    }
}

// MARK: - Non-Raw-Value Enum Param (RichTextKit ActionButton pattern)

/// Replicates RichTextKit.ActionButton — view taking a non-raw-value enum
/// with associated values. Tests Session 3's BoundStruct bridge for enum types.
/// Uses TransformOutcome from Closures/StructClosureBridge.swift (has associated values).
public struct FormatActionView: View {
    let action: TransformOutcome

    public init(action: TransformOutcome) {
        self.action = action
    }

    public var body: some View {
        Text("Action: \(outcomeValue(action))")
    }
}

// MARK: - Closure with BoundStruct Arg (RichTextKit Menu pattern)

/// Replicates RichTextKit.Menu — closure taking non-raw-value enum arg.
/// Tests Session 3's BoundStruct closure arg bridge (heap-allocate + initializeMemory).
public struct FormatMenuView: View {
    let onFormat: (TransformOutcome) -> Void

    public init(onFormat: @escaping (TransformOutcome) -> Void) {
        self.onFormat = onFormat
    }

    public var body: some View {
        Text("FormatMenu")
    }
}

// MARK: - Dual String Params (RichTextKit toolbar pattern)

/// Replicates simple RichTextKit toolbar views — dual string parameters.
/// Tests multiple String param bridging (both updatable).
public struct RichToolbarView: View {
    let title: String
    let subtitle: String

    public init(title: String, subtitle: String) {
        self.title = title
        self.subtitle = subtitle
    }

    public var body: some View {
        VStack {
            Text(title)
            Text(subtitle)
        }
    }
}

// MARK: - Binding<Bool> Param (Session 2 gate: Binding<T>)

/// Tests the Binding<T> bridge parameter gate at runtime.
/// The bridge stores the Bool in @Published state and passes $state.isOn
/// (Binding projection) to the view's init.
public struct BindingToggleView: View {
    @Binding var isOn: Bool

    public init(isOn: Binding<Bool>) {
        self._isOn = isOn
    }

    public var body: some View {
        Toggle("Toggle", isOn: $isOn)
    }
}

// MARK: - Array<Int> Param (Session 2 gate: Array<T>)

/// Tests the BridgeArray parameter gate at runtime.
/// The bridge receives a pointer + count and reconstructs via UnsafeBufferPointer.map.
public struct NumberListView: View {
    let numbers: [Int32]

    public init(numbers: [Int32]) {
        self.numbers = numbers
    }

    public var body: some View {
        Text("Count: \(numbers.count)")
    }
}

// MARK: - SwiftUI.Image Param (Session 2 gate: Image)

/// Tests the SwiftUI.Image bridge parameter gate at runtime.
/// The bridge stores the SF Symbol name as a String and reconstructs
/// Image(systemName:) in the Wrapper.
public struct SymbolIconView: View {
    let icon: Image

    public init(icon: Image) {
        self.icon = icon
    }

    public var body: some View {
        icon
    }
}
