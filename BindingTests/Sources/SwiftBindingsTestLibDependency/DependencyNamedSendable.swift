// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - A protocol that shares the standard library marker's name
//
// `Sendable` here is this module's own protocol, not `Swift.Sendable`: it has a requirement, so
// its existential is a box, its metadata and one witness table (40 bytes), where the standard
// library's marker has no witness table (32 bytes). A file that imports this module and spells
// `Sendable` bare names this protocol. The main test library vends arrays and values of it
// (SwiftBindingsTestLib/Generics/MarkerIdentityExistentials.swift); a binding that classifies the
// protocol as a marker by its name reads them at the marker's stride.

public protocol Sendable {
    var dependencySendableCode: Int32 { get }
}
