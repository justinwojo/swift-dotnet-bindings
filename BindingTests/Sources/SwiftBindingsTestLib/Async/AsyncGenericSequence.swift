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

// MARK: - CSM-async trim-overload fixtures (Phase 3a / option (b))
//
// Exercises `ConcreteProtocolSpecializationEmitter.TryEmitConcreteOverloadAsync`'s new
// trailing tail call into `DefaultParameterOverloadEmitter.TryEmitOverloads`. Each
// method below has a method-level Sequence generic + two trailing defaults — one
// non-mappable Set + one mappable Int. The CSM-async path emits the primary
// specialized overload (per Animal-Sequence conformer) carrying both defaults
// (`options` with no C# inline default, `tag = N` inline), and the trim emitter
// layers two shorter variants where Swift fills one or both.
//
// The shape mirrors the StoreKit2 `purchase(confirmIn:options: Set<…> = [])` gap-doc
// reference. The non-mappable Set default is what bypasses the trim emitter's
// `AllTrailingDefaultsAreCSharpMappable` early-return; the mappable Int default
// then exercises the intermediate-trim path (one default exposed, one filled).
//
// Pre-fix, the trim emitter's `methodDecl.IsGeneric` bail short-circuited the
// wiring entirely on the unspecialized generic decl — no DBW_ symbol emitted,
// no per-conformer trim P/Invoke. Post-fix, the synthesized non-generic methodDecl
// (substituted CSSignature, cleared GenericParameters) carries through to the trim
// emitter, which produces per-conformer @_silgen_name shims keyed off the per-
// conformer cdecl symbol so symbols stay unique.

public final class DefaultedAsyncRoster {
    public private(set) var animals: [Animal]

    public init(_ animals: [Animal]) {
        self.animals = animals
    }

    /// Async no-throws variant — Sequence + non-mappable Set default + mappable Int default.
    /// CSM-async primary: `AppendAsync(IEnumerable<Animal> source, IReadOnlySet<int> options, nint tag = 13, ct = default)`.
    /// Trim variant trim=2 (drops both defaults): `AppendAsync(source, ct = default)`.
    /// Trim depths whose dropped suffix is entirely C#-mappable (e.g. trim=1 dropping only
    /// `tag`) are intentionally suppressed by `BuildMappableSuffixShadowKeys` in the
    /// CSM-async wiring — the primary already exposes those shapes via its inline `tag = 13`
    /// default, and emitting both overloads would produce CS0121 ambiguous-overload errors.
    public func appendAsync<S: Sequence>(
        contentsOf source: S,
        options: Set<Int> = [],
        tag: Int = 13
    ) async where S.Element : Animal {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let upcast: [Animal] = source.map { $0 as Animal }
        animals.append(contentsOf: upcast)
        animals[animals.count - 1] = Animal(
            name: animals[animals.count - 1].name,
            sound: "TAG=\(tag);OPT=\(options.count)")
    }

    /// Async-throws variant — Sequence + non-mappable Set default + mappable Int default.
    /// CSM-async primary: `AppendOrThrowAsync(source, shouldThrow, IReadOnlySet<int> options, nint tag = 17, ct = default)`.
    /// Trim variant trim=2 (drops both defaults): `AppendOrThrowAsync(source, shouldThrow, ct = default)`.
    /// Same mappable-suffix suppression as `appendAsync`: trim=1 (drops only `tag`) is
    /// covered by the primary's inline `tag = 17` default and is not emitted to avoid
    /// CS0121 ambiguity.
    public func appendOrThrowAsync<S: Sequence>(
        contentsOf source: S,
        shouldThrow: Bool,
        options: Set<Int> = [],
        tag: Int = 17
    ) async throws where S.Element : Animal {
        try? await Task.sleep(nanoseconds: 1_000_000)
        if shouldThrow {
            throw AsyncError.requestedThrow
        }
        let upcast: [Animal] = source.map { $0 as Animal }
        animals.append(contentsOf: upcast)
        animals[animals.count - 1] = Animal(
            name: animals[animals.count - 1].name,
            sound: "TAG=\(tag);OPT=\(options.count)")
    }

    public var count: Int { animals.count }
    public subscript(position: Int) -> Animal { animals[position] }
}

/// Factory mirroring `makeAnimalAsyncRoster` for the trim-overload fixtures so runtime
/// tests construct without exercising any other generic-method path.
public func makeDefaultedAsyncRoster(firstName: String, secondName: String) -> DefaultedAsyncRoster {
    return DefaultedAsyncRoster([
        Animal(name: firstName, sound: "Roar"),
        Animal(name: secondName, sound: "Howl"),
    ])
}
