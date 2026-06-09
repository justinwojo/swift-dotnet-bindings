// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI Views replicating patterns from third-party validation libraries.
// These exercise bridge parameter gates at runtime:
//
//   NoParamBlurView       → AlertToast.BlurView (zero-param init)
//   PlayerStyleView       → YouTubePlayerKit.YouTubePlayerView (class + string)
//   FormatActionView      → RichTextKit.ActionButton (non-raw-value enum)
//   FormatMenuView        → RichTextKit.Menu (closure with BoundStruct arg)
//   RichToolbarView       → RichTextKit toolbar views (dual string params)

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
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
/// with associated values. Tests the BoundStruct bridge for enum types.
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
/// Tests the BoundStruct closure arg bridge (heap-allocate + initializeMemory).
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

// MARK: - Binding<Bool> Param (Binding<T> gate)

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

// MARK: - Array<Int> Param (Array<T> gate)

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

// MARK: - SwiftUI.Image Param (Image gate)

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

// MARK: - Result<T,E> Closure Param (CodeScanner pattern)

/// Custom error type for Result closure testing (conforms to Error).
public class ScanError: Error {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

/// Replicates CodeScanner's `(Result<ScanResult, ScanError>) -> Void` callback pattern.
/// The bridge decomposes the Result into two C callbacks: onSuccess + onError.
/// Tests that the Swift wrapper switch dispatches correctly.
public struct ResultCompletionView: View {
    let completion: (Result<SimpleModel, ScanError>) -> Void

    public init(completion: @escaping (Result<SimpleModel, ScanError>) -> Void) {
        self.completion = completion
    }

    public var body: some View {
        Text("ResultCompletion")
    }
}

/// Non-raw-value enum conforming to Error for BoundStruct Result testing.
/// Associated values make it non-RawRepresentable → BoundStruct in the bridge.
public enum DetailedError: Error {
    case validation(code: Int32)
    case network(code: Int32)
}

/// Tests Result<BoundType, BoundStruct> — the error branch uses heap-allocate + initializeMemory
/// (non-raw-value enum as BoundStruct). Exercises the P2 nil-guard fix and BoundStruct ABI path.
public struct ResultWithStructView: View {
    let completion: (Result<SimpleModel, DetailedError>) -> Void

    public init(completion: @escaping (Result<SimpleModel, DetailedError>) -> Void) {
        self.completion = completion
    }

    public var body: some View {
        Text("ResultWithStruct")
    }
}

// MARK: - Binding<Codable Struct> Param (FamilyActivityPicker pattern)

/// Codable struct that the bridge ferries across the boundary as JSON UTF-8.
/// Non-frozen + Codable + module-public satisfies the CodableJsonEmitter gate.
public struct CodableProfile: Codable, Equatable {
    public var name: String
    public var count: Int32

    public init(name: String, count: Int32) {
        self.name = name
        self.count = count
    }
}

/// Exercises Binding<CodableStruct> bridge support — mirrors the FamilyActivityPicker
/// shape where a targeted shim stores the struct in
/// @Published state and passes $state.profile to the view's init.
public struct CodableProfileEditorView: View {
    @Binding var profile: CodableProfile

    public init(profile: Binding<CodableProfile>) {
        self._profile = profile
    }

    public var body: some View {
        VStack {
            Text(profile.name)
            Text("\(profile.count)")
        }
    }
}
#endif
