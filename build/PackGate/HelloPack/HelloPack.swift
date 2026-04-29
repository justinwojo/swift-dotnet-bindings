// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Minimal Swift artifact for the PackGate end-to-end consumer step. One public
// top-level function with a deterministic return value — the consumer exe asserts
// the round-tripped string. The Swift surface is intentionally trivial; the gate
// proves the full shipping pipeline (pack -> SDK -> generator -> runtime ->
// consumer) wires up correctly, not any specific binding feature.
public func packGateGreet(_ name: String) -> String {
    return "Hello, \(name) from Swift!"
}
