// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Pure-Swift readiness control for the command-line StoreKit Sandbox lane. The harness installs
// this control on the exact simulator and bundle id the managed app will use, then requires one
// fresh four-operation receipt before replacing it with the managed app. No .storekit path is
// accepted: only Xcode can activate that backend.

import UIKit
import StoreKit

private struct Configuration {
    let platform: String
    let bundleID: String
    let productID: String
    let runToken: String

    static func load() throws -> Configuration {
        let args = ProcessInfo.processInfo.arguments
        func value(after flag: String) -> String? {
            guard let index = args.firstIndex(of: flag), index + 1 < args.count else { return nil }
            return args[index + 1]
        }
        guard let platform = value(after: "--storekit-platform"), platform == "ios" || platform == "tvos" else {
            throw ControlError.configuration("missing/invalid --storekit-platform")
        }
        guard let bundleID = value(after: "--storekit-bundle-id"), !bundleID.isEmpty else {
            throw ControlError.configuration("missing --storekit-bundle-id")
        }
        guard let productID = value(after: "--storekit-sandbox-product-id"),
              productID.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{2,254}$", options: .regularExpression) != nil,
              !productID.localizedCaseInsensitiveContains("nonexistent"),
              !productID.localizedCaseInsensitiveContains("placeholder") else {
            throw ControlError.configuration("missing --storekit-sandbox-product-id")
        }
        guard let runToken = value(after: "--storekit-sandbox-run-token"), runToken.count == 32 else {
            throw ControlError.configuration("missing/invalid --storekit-sandbox-run-token")
        }
        return Configuration(platform: platform, bundleID: bundleID, productID: productID, runToken: runToken)
    }
}

private enum ControlError: Error, CustomStringConvertible {
    case configuration(String)
    case assertion(String)

    var description: String {
        switch self {
        case .configuration(let detail): return "configuration: \(detail)"
        case .assertion(let detail): return "assertion: \(detail)"
        }
    }
}

private struct ProbeResult {
    let success: Bool
    let count: Int
    let detail: String
}

private final class Board: @unchecked Sendable {
    private let lock = NSLock()
    private var results: [String: ProbeResult] = [:]
    let names = ["appTransaction", "products", "currentEntitlements", "all"]

    func set(_ name: String, _ result: ProbeResult) {
        lock.lock()
        defer { lock.unlock() }
        guard results[name] == nil else { return }
        results[name] = result
    }

    func snapshot() -> [String: ProbeResult] {
        lock.lock()
        defer { lock.unlock() }
        return results
    }
}

private let started = Date()
private let deadlineSeconds: Double = 60
private let board = Board()

private func log(_ line: String) {
    let elapsed = String(format: "%6.1fs", Date().timeIntervalSince(started))
    print("[SK-CONTROL] \(elapsed) \(line)")
    fflush(stdout)
}

private func sandboxTransaction(_ result: VerificationResult<Transaction>) throws -> Transaction {
    let transaction: Transaction
    switch result {
    case .verified(let value): transaction = value
    case .unverified(_, let error):
        throw ControlError.assertion("unverified transaction: \(type(of: error))")
    }
    guard transaction.environment == .sandbox else {
        throw ControlError.assertion("transaction environment was \(transaction.environment.rawValue), expected Sandbox")
    }
    return transaction
}

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?
    var heartbeat: DispatchSourceTimer?
    private var configuration: Configuration?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
    ) -> Bool {
        window = UIWindow(frame: UIScreen.main.bounds)
        window?.rootViewController = UIViewController()
        window?.makeKeyAndVisible()

        do {
            let config = try Configuration.load()
            guard Bundle.main.bundleIdentifier == config.bundleID else {
                throw ControlError.assertion(
                    "running bundle \(Bundle.main.bundleIdentifier ?? "nil") != expected \(config.bundleID)")
            }
            configuration = config
            log("start platform=\(config.platform) bundle=\(config.bundleID) product=\(config.productID) token=\(config.runToken)")
            startProbes(config)
        } catch {
            log("FAIL \(error)")
            log("TEST FAILED")
            DispatchQueue.main.async { exit(2) }
            return true
        }

        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in self?.inspectProgress() }
        timer.resume()
        heartbeat = timer

        DispatchQueue.main.asyncAfter(deadline: .now() + deadlineSeconds) { [weak self] in
            guard let self else { return }
            let snapshot = board.snapshot()
            let pending = board.names.filter { snapshot[$0] == nil }
            if pending.isEmpty {
                // A final probe may have published after the last one-second heartbeat but before
                // this hard deadline callback. Let the completed snapshot report its real verdict.
                self.inspectProgress()
                return
            }
            let completed = board.names.compactMap { name -> String? in
                guard let result = snapshot[name] else { return nil }
                return "\(name)=\(result.success ? "ok" : "failed"):\(result.detail)"
            }
            log("FAIL deadline exceeded; pending=\(pending); completed=\(completed)")
            log("TEST FAILED")
            self.heartbeat?.cancel()
            exit(2)
        }
        return true
    }

    private func inspectProgress() {
        guard let config = configuration else { return }
        let snapshot = board.snapshot()
        guard snapshot.count == board.names.count else { return }
        heartbeat?.cancel()

        let failures = board.names.compactMap { name -> String? in
            guard let result = snapshot[name], !result.success else { return nil }
            return "\(name):\(result.detail)"
        }
        guard failures.isEmpty else {
            log("FAIL \(failures.joined(separator: ";"))")
            log("TEST FAILED")
            exit(2)
        }

        let products = snapshot["products"]!.count
        let current = snapshot["currentEntitlements"]!.count
        let all = snapshot["all"]!.count
        print("[SK-CONTROL] READY schema=1 platform=\(config.platform) bundle=\(config.bundleID) " +
              "product=\(config.productID) token=\(config.runToken) appTransaction=sandbox " +
              "products=\(products) currentEntitlements=\(current) all=\(all)")
        print("[SK-CONTROL] TEST SUCCESS")
        fflush(stdout)
        exit(0)
    }

    private func startProbes(_ config: Configuration) {
        Task.detached {
            do {
                let result = try await AppTransaction.shared
                let transaction: AppTransaction
                switch result {
                case .verified(let value): transaction = value
                case .unverified(_, let error):
                    throw ControlError.assertion("AppTransaction was unverified: \(type(of: error))")
                }
                guard transaction.bundleID == config.bundleID else {
                    throw ControlError.assertion("bundleID \(transaction.bundleID) != \(config.bundleID)")
                }
                guard transaction.environment == .sandbox else {
                    throw ControlError.assertion("environment \(transaction.environment.rawValue) != Sandbox")
                }
                board.set("appTransaction", ProbeResult(success: true, count: 1, detail: "sandbox"))
            } catch {
                board.set("appTransaction", ProbeResult(success: false, count: 0, detail: "\(error)"))
            }
        }

        Task.detached {
            do {
                let products = try await Product.products(for: [config.productID])
                let exact = products.filter { $0.id == config.productID }
                guard products.count == 1 && exact.count == 1 else {
                    throw ControlError.assertion(
                        "expected one exact configured product, got total=\(products.count) exact=\(exact.count)")
                }
                board.set("products", ProbeResult(success: true, count: exact.count, detail: "exact"))
            } catch {
                board.set("products", ProbeResult(success: false, count: 0, detail: "\(error)"))
            }
        }

        Task.detached {
            do {
                var count = 0
                for await result in Transaction.currentEntitlements {
                    _ = try sandboxTransaction(result)
                    count += 1
                    if count > 64 { throw ControlError.assertion("more than 64 results") }
                }
                board.set("currentEntitlements", ProbeResult(success: true, count: count, detail: "complete"))
            } catch {
                board.set("currentEntitlements", ProbeResult(success: false, count: 0, detail: "\(error)"))
            }
        }

        Task.detached {
            do {
                var count = 0
                for await result in Transaction.all {
                    _ = try sandboxTransaction(result)
                    count += 1
                    if count > 64 { throw ControlError.assertion("more than 64 results") }
                }
                board.set("all", ProbeResult(success: true, count: count, detail: "complete"))
            } catch {
                board.set("all", ProbeResult(success: false, count: 0, detail: "\(error)"))
            }
        }
    }
}
