// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested-class + Result<Self, any Error> / ((any Error)?) closure shape
//
// Mirrors Stripe's `PaymentSheet.FlowController` shape (Bug 1 in
// `bug-0.10.0-missingwrappersymbol-after-wrapper-emit.md`):
//
//   public class PaymentSheet {
//       public class FlowController {
//           public static func create(... completion: @escaping
//                                     (Result<FlowController, Error>) -> Void)
//           public func update(... completion: @escaping (Error?) -> Void)
//       }
//   }
//
// The wrapper-emit pipeline silently dropped @_cdecl wrappers for these methods
// because the closure-arg cdecl-compat predicate did not recognise
// `Result<Self, any Error>` / `Optional<any Error>` as bridgeable. C# still
// emitted `[LibraryImport]` entries pointing at symbols that do not exist in
// the wrapper dylib, surfacing as `MissingWrapperSymbol` in the binding report
// and `EntryPointNotFoundException` at first call.

/// Outer non-nested public class — mirrors `PaymentSheet`.
public class OnboardingFlow {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Configuration value passed to the nested controller's factory + update.
    public struct Configuration {
        public var theme: Int32
        public init(theme: Int32) { self.theme = theme }
    }

    /// Nested public class — mirrors `PaymentSheet.FlowController`.
    /// Has a static factory taking `(Result<Self, any Error>) -> Void` and an
    /// instance `update` taking `((any Error)?) -> Void`.
    public class SessionController {
        public var token: String
        public var configuration: Configuration

        public init(token: String, configuration: Configuration) {
            self.token = token
            self.configuration = configuration
        }

        /// Static factory whose completion handler delivers a freshly-created
        /// `SessionController` (the nested class itself) wrapped in a Result.
        ///
        /// Stripe shape:
        ///   `static func create(intentConfiguration:configuration:completion:)`
        ///   where `completion: @escaping (Result<FlowController, Error>) -> Void`.
        public static func create(
            token: String,
            configuration: Configuration,
            shouldFail: Bool,
            completion: @escaping (Result<SessionController, any Error>) -> Void
        ) {
            if shouldFail {
                completion(.failure(MathError.divisionByZero))
            } else {
                completion(.success(SessionController(token: token, configuration: configuration)))
            }
        }

        /// Static factory variant whose completion uses `((any Error)?) -> Void`
        /// instead of Result. Mirrors Stripe overloads of `create` that report
        /// failure-only via an Optional Error completion.
        public static func createWithOptionalError(
            token: String,
            configuration: Configuration,
            shouldFail: Bool,
            completion: @escaping ((any Error)?) -> Void
        ) {
            if shouldFail {
                completion(MathError.divisionByZero)
            } else {
                completion(nil)
            }
        }

        /// Instance method whose completion is `((any Error)?) -> Void`. Mirrors
        /// `PaymentSheet.FlowController.update(intentConfiguration:completion:)`.
        public func update(
            configuration: Configuration,
            shouldFail: Bool,
            completion: @escaping ((any Error)?) -> Void
        ) {
            self.configuration = configuration
            if shouldFail {
                completion(MathError.divisionByZero)
            } else {
                completion(nil)
            }
        }
    }
}
