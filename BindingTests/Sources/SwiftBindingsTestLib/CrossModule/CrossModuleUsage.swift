// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Type References

/// Uses DependencyPoint from the dependency module as parameter and return type.
/// Tests that the generator correctly resolves cross-module type references.
public func transformDependencyPoint(_ point: DependencyPoint, scale: Double) -> DependencyPoint {
    return DependencyPoint(x: point.x * scale, y: point.y * scale)
}

/// Uses DependencyConfig from the dependency module.
public func upgradeDependencyConfig(_ config: DependencyConfig) -> DependencyConfig {
    return DependencyConfig(name: config.name, version: config.version + 1)
}

/// Uses DependencyService class from the dependency module.
public func toggleDependencyService(_ service: DependencyService) -> String {
    service.isActive = !service.isActive
    return service.status()
}

// MARK: - Cross-Module Protocol Conformance

/// A local struct that conforms to DependencyProtocol from the dependency module.
/// Tests that cross-module protocol conformances are correctly emitted.
public struct LocalConformant: DependencyProtocol {
    public var identifier: String
    public var tag: Int32

    public init(identifier: String, tag: Int32 = 0) {
        self.identifier = identifier
        self.tag = tag
    }

    public func describe() -> String {
        return "Local[\(tag)]: \(identifier)"
    }
}

/// Factory for creating LocalConformant instances.
public func makeLocalConformant(identifier: String, tag: Int32) -> LocalConformant {
    return LocalConformant(identifier: identifier, tag: tag)
}

/// Accepts any DependencyProtocol and returns its description.
/// Tests that the generated binding can pass local conformants to dependency protocol functions.
public func describeLocalConformant(_ conformant: some DependencyProtocol) -> String {
    return conformant.describe()
}

// MARK: - Cross-Module Property Type (Part B-1)

/// Struct with a cross-module type as a stored property.
/// Tests that cross-module type references work in property positions.
public struct AnnotatedLocation {
    public var label: String
    public var point: DependencyPoint

    public init(label: String, point: DependencyPoint) {
        self.label = label
        self.point = point
    }

    public func summary() -> String {
        return "\(label): (\(point.x), \(point.y))"
    }
}

/// Factory for AnnotatedLocation.
public func makeAnnotatedLocation(label: String, x: Double, y: Double) -> AnnotatedLocation {
    return AnnotatedLocation(label: label, point: DependencyPoint(x: x, y: y))
}

/// Reads the point from an AnnotatedLocation (tests property getter round-trip).
public func getLocationPoint(_ location: AnnotatedLocation) -> DependencyPoint {
    return location.point
}

// MARK: - Cross-Module Collection (Part B-2)

/// Takes an array of DependencyPoints and returns the sum point.
/// Tests cross-module types in collection parameters and returns.
public func sumDependencyPoints(_ points: [DependencyPoint]) -> DependencyPoint {
    var totalX = 0.0
    var totalY = 0.0
    for p in points {
        totalX += p.x
        totalY += p.y
    }
    return DependencyPoint(x: totalX, y: totalY)
}

/// Returns an array of DependencyPoints (tests collection return with cross-module element).
public func makeDependencyPointGrid(rows: Int32, cols: Int32) -> [DependencyPoint] {
    var result: [DependencyPoint] = []
    for r in 0..<rows {
        for c in 0..<cols {
            result.append(DependencyPoint(x: Double(c), y: Double(r)))
        }
    }
    return result
}

// MARK: - Cross-Module Enum Usage (Part B-3)

/// Uses DependencyStatus enum from the dependency module as parameter and return.
public func promoteDependencyStatus(_ status: DependencyStatus) -> DependencyStatus {
    switch status {
    case .unknown: return .pending
    case .pending: return .active
    case .active: return .active  // already at max
    case .inactive: return .pending
    @unknown default: return .unknown
    }
}

/// Returns the label for a DependencyStatus (tests enum property access cross-module).
public func describeDependencyStatus(_ status: DependencyStatus) -> String {
    return "Status: \(status.label)"
}

// MARK: - Cross-Module Closure (Part B-4)

/// Takes a closure that receives a DependencyPoint.
/// Tests cross-module types as closure parameters.
public func applyToDependencyPoint(x: Double, y: Double, action: (DependencyPoint) -> Void) {
    let point = DependencyPoint(x: x, y: y)
    action(point)
}

/// Takes a closure that transforms a DependencyPoint and returns the result.
public func mapDependencyPoint(_ point: DependencyPoint, transform: (DependencyPoint) -> DependencyPoint) -> DependencyPoint {
    return transform(point)
}

// MARK: - Cross-Module Extension (Part B-5)

/// Extension on DependencyPoint defined in the main library.
/// Tests cross-module protocol extension pattern.
extension DependencyPoint {
    /// Scales both coordinates by the given factor.
    public func scaled(by factor: Double) -> DependencyPoint {
        return DependencyPoint(x: self.x * factor, y: self.y * factor)
    }

    /// Returns the Manhattan distance from origin.
    public var manhattanDistance: Double {
        return abs(x) + abs(y)
    }

    /// Cross-module struct-extension method taking and returning a SimpleEnum
    /// (`DependencyStatus`). Locks in the @_cdecl SimpleEnum lowering on the
    /// struct-receiver trampoline path: the C# side casts (int)status across
    /// the boundary, the Swift trampoline reconstructs the enum via
    /// `DependencyStatus(rawValue:)!` for the call and surfaces `.rawValue` on
    /// the return.
    public func classify(against status: DependencyStatus) -> DependencyStatus {
        switch status {
        case .unknown: return x > 0 ? .pending : .unknown
        case .pending: return .active
        case .active: return .active
        case .inactive: return .pending
        @unknown default: return .unknown
        }
    }
}

/// Free function that uses the extension method.
public func scaleDependencyPoint(_ point: DependencyPoint, factor: Double) -> DependencyPoint {
    return point.scaled(by: factor)
}

/// Lightweight pure-Swift class used as the success-path payload of the
/// Stripe-shape `produceToken` completion below. Reproduces the
/// `(STPToken?, (any Error)?) -> Void` Optional<class> closure-arg shape.
public class DependencyToken {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

// MARK: - Cross-Module Class Extension (Stripe STPAPIClient shape)

/// Extension on `DependencyService` (a class declared in the dependency
/// module). Reproduces the Stripe pattern where module B layers extra API
/// onto a class owned by module A — `extension StripeCore.STPAPIClient`
/// declared in StripePayments. The Phase 1 fix routes class receivers
/// through `CrossModuleExtensionEmitter` so the extension members surface
/// as `static partial class DependencyServiceSwiftBindingsTestLibExtensions`.
extension DependencyService {
    /// Returns the receiver's tag-shifted activation flag. Pure-Swift class
    /// receiver, primitive parameter and primitive return — exercises the
    /// Phase 1 happy path: a CallConvSwift call site that routes the receiver
    /// through SwiftSelf and a primitive arg through the regular register.
    public func taggedActivation(tag: Int32) -> Int32 {
        return self.isActive ? tag : -tag
    }

    /// Activates the receiver and returns whether the receiver is now active.
    /// Tests instance-mutating extension dispatch on class receivers without
    /// crossing the Swift-String register-vs-indirect-result boundary.
    public func activateAndReport() -> Bool {
        self.isActive = true
        return self.isActive
    }

    /// Closure-bearing cross-module class extension — reproduces the Stripe
    /// `STPAPIClient.createToken(withCard:completion:)` shape on a pure Swift
    /// class receiver. The completion block fires synchronously inside the
    /// trampoline so a per-call GCHandle lifetime is sufficient.
    public func computeWithCompletion(value: Int32, completion: @escaping (Int32) -> Void) {
        completion(self.isActive ? value * 2 : -value)
    }

    /// Stripe-shape completion block exercising Optional<class> + Optional<any Error>
    /// closure args end-to-end. When `activate` is true the receiver hands back
    /// a `DependencyToken` and a nil error; otherwise it hands back a nil token
    /// and an `NSError` so both nil legs of the optional bridge are covered.
    public func produceToken(activate: Bool, completion: @escaping (DependencyToken?, (any Error)?) -> Void) {
        if activate {
            completion(DependencyToken(value: 42), nil)
        } else {
            completion(nil, NSError(domain: "DependencyService", code: 2, userInfo: [NSLocalizedDescriptionKey: "inactive"]))
        }
    }

    /// Async-throws cross-module class extension — reproduces the
    /// `STPAPIClient.createToken(withCard:) async throws -> STPToken` shape.
    /// Throws when the receiver is inactive so the failure path is exercised
    /// alongside the happy path.
    public func computeAsync(value: Int32) async throws -> Int32 {
        if !self.isActive {
            throw NSError(domain: "DependencyService", code: 1, userInfo: [NSLocalizedDescriptionKey: "inactive"])
        }
        return value * 3
    }

    /// Static class func on a class receiver — reproduces the
    /// `StripeAPI.paymentRequest(withMerchantIdentifier:)` shape but with
    /// primitive arg + primitive return.
    public class func makeWithSeed(_ seed: Int32) -> DependencyService {
        return DependencyService(name: "seed-\(seed)", isActive: seed > 0)
    }

    /// Static class func taking a Swift.String — locks the full Stripe
    /// `StripeAPI.paymentRequest(withMerchantIdentifier:)` shape (static,
    /// String param, class return). Routes through the wrapper-library
    /// trampoline because the simple direct-CallConvSwift path cannot
    /// synthesize the Swift.String two-word value layout.
    public class func makeWithLabel(_ label: String) -> DependencyService {
        return DependencyService(name: label, isActive: true)
    }

    /// Closure-bearing instance method with a Swift.String parameter —
    /// reproduces the Stripe `confirmPaymentIntent(clientSecret:completion:)`
    /// shape where the receiver, a String arg, and an escaping completion
    /// closure all co-occur. Routes through the trampoline because of the
    /// String + closure pair.
    public func notifyLabel(_ label: String, completion: @escaping (Int32) -> Void) {
        completion(Int32(label.count) + (self.isActive ? 1 : 0))
    }
}

// NOTE: Module name = type name collision (Reachability pattern) is tested
// through validation libraries. Swift issue #56573 prevents including this
// pattern in a library-evolution-enabled module used as a build dependency.

// MARK: - Cross-Module Nested-Type Rename Propagation

/// Returns the alert-type case from a dependency-owned `DependencyContainer`.
/// Locks the producer-side nested-type rename (`AlertType` -> `AlertTypeType`):
/// the consumer must reference the renamed C# name when emitting this
/// function's return type and `Container_AlertType_Get` thunk signature.
public func getDependencyContainerAlertType(_ container: DependencyContainer) -> DependencyContainer.AlertType {
    return container.alertType
}

/// Builds a `DependencyContainer` with the supplied alert type. Locks the
/// renamed C# name on the parameter side as well.
public func makeDependencyContainer(name: String, alert: DependencyContainer.AlertType) -> DependencyContainer {
    return DependencyContainer(name: name, alertType: alert)
}

/// Holds a renamed cross-module nested type as a property. The consumer-emitted
/// property type must use `DependencyContainer.AlertTypeType`, otherwise the
/// generated C# fails to compile with CS0426.
public struct DependencyContainerHolder {
    public let label: String
    public let alert: DependencyContainer.AlertType

    public init(label: String, alert: DependencyContainer.AlertType) {
        self.label = label
        self.alert = alert
    }

    public func summarize() -> String {
        return "\(label):\(alert.rawValue)"
    }
}

/// Factory for `DependencyContainerHolder`.
public func makeDependencyContainerHolder(label: String, alert: DependencyContainer.AlertType) -> DependencyContainerHolder {
    return DependencyContainerHolder(label: label, alert: alert)
}
