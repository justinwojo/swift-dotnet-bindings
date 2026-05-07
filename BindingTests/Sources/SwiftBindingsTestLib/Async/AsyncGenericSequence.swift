// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic-async Sequence shape (MusicKit MusicPlayer.Queue.insert)
//
// Mirrors Bug 2 in `bug-0.10.0-generic-async-wrapper-symbol-missing.md`:
//
//   public class MusicPlayer.Queue {
//       public func insert<S: Sequence>(_ entries: S, at position: ...) async throws
//           where S.Element == Entry
//   }
//
// Pre-fix: the wrapper-emit pipeline emitted `_async` cdecl trampolines for each
// concrete-element conformer registered in `specialization-hints.json`, but C# also
// emitted a separate `[LibraryImport(EntryPoint = "...STRzAE0G0V7ElementRtzlF_async")]`
// referencing an unspecialized-generic mangled symbol that does not exist in the
// wrapper dylib. First call → `EntryPointNotFoundException`.
//
// `AnimalRoster` already exists in `Generics/Constraints.swift` as a sync fixture —
// the post-fix engine emits a single `Insert(SwiftArray<Animal>, nint)` overload
// keyed off the `Animal` Sequence conformer. This fixture exercises the same shape
// with `async throws` so the CSM-async path is forced through, not the sync path.
//
// Class (not struct) — mutating async methods on structs aren't part of the bug's
// shape; the bug surface is "instance async-throws method generic over Sequence".

public final class AnimalAsyncRoster {
    public private(set) var animals: [Animal]

    public init(_ animals: [Animal]) {
        self.animals = animals
    }

    /// Async-throwing variant of `AnimalRoster.insert`. The CSM-async pipeline
    /// must emit a closed-instantiation `_async` cdecl trampoline for every
    /// matching `Swift.Sequence` conformer (Animal, Dog) — and *no* unspecialized
    /// generic `[LibraryImport]` may be emitted on the C# side.
    public func insertAsync<S: Sequence>(
        contentsOf source: S,
        beforeIndex i: Int,
        shouldThrow: Bool
    ) async throws where S.Element : Animal {
        try? await Task.sleep(nanoseconds: 1_000_000)
        if shouldThrow {
            throw AsyncError.requestedThrow
        }
        let upcast: [Animal] = source.map { $0 as Animal }
        animals.insert(contentsOf: upcast, at: i)
    }

    public var count: Int { animals.count }
    public subscript(position: Int) -> Animal { animals[position] }
}

/// Factory mirroring `makeAnimalRoster`. Avoids the generic-method dispatch path
/// during construction so the runtime tests focus narrowly on the async-insert
/// generic-Sequence path.
public func makeAnimalAsyncRoster(firstName: String, secondName: String) -> AnimalAsyncRoster {
    return AnimalAsyncRoster([
        Animal(name: firstName, sound: "Roar"),
        Animal(name: secondName, sound: "Howl"),
    ])
}
