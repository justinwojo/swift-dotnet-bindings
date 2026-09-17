// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Parameterized protocol existentials at an older deployment target
//
// `any RequestTask<Int, RequestTaskError>` is legal at iOS 15 as long as the library only uses it
// statically. A binding moves the value through raw memory, which needs the existential's runtime
// metadata — iOS 16 / macOS 13 / tvOS 16 or newer. The members below are available from the
// library's own floor; the binding marks them with the runtime floor instead.

public struct RequestTaskError: Error {
    public let code: Int

    public init(code: Int) { self.code = code }
}

public protocol RequestTask<Value, Failure> {
    associatedtype Value
    associatedtype Failure: Error

    func start() -> Result<Value, Failure>
}

public struct PendingRequestTask<Value, Failure: Error>: RequestTask {
    let value: Value

    public init(value: Value) { self.value = value }

    public func start() -> Result<Value, Failure> { .success(value) }

    public func erased() -> any RequestTask<Value, Failure> { self }
}

/// Implemented in C#; Swift calls back through it.
public protocol RequestTaskClient {
    func fetch(id: Int) -> any RequestTask<Int, RequestTaskError>
    func submit(_ request: any RequestTask<Int, RequestTaskError>) -> Int
    var name: String { get }
}

public struct RequestTaskApi {
    public init() {}

    public func fetch(id: Int) -> any RequestTask<Int, RequestTaskError> {
        PendingRequestTask<Int, RequestTaskError>(value: id)
    }

    public func run(_ request: any RequestTask<Int, RequestTaskError>) -> Int {
        if case .success(let value) = request.start() { return value }
        return -1
    }

    /// Fetches through the client, runs the result, and submits a fresh request back to it.
    public func runClient(_ client: any RequestTaskClient) -> Int {
        run(client.fetch(id: 7)) + client.submit(PendingRequestTask<Int, RequestTaskError>(value: 30))
    }

    public func clientName(_ client: any RequestTaskClient) -> String { client.name }
}
