// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;

namespace BindingsGeneration.Tests;

/// <summary>
/// A throwaway SDK holding hand-written module interfaces, installed as the run's SDK for the
/// lifetime of the returned scope. Toolchain modules land under <c>usr/lib/swift</c>, frameworks
/// under <c>System/Library/Frameworks</c>, where the generator's readers look for them.
/// </summary>
internal sealed class FakeSdkInterfaces : IDisposable
{
    /// <summary>The standard-library protocols, printed as an Xcode 27.2 interface prints them (module selectors).</summary>
    internal const string Swift = """
        @_marker public protocol SendableMetatype : ~Copyable, ~Escapable {
        }
        @_marker public protocol Sendable : Swift::SendableMetatype {
        }
        public protocol Error : Swift::Sendable {
        }
        public protocol Equatable : ~Copyable, ~Escapable {
          static func == (lhs: borrowing Self, rhs: borrowing Self) -> Swift::Bool
        }
        public protocol Hashable : Swift::Equatable, ~Copyable, ~Escapable {
          var hashValue: Swift::Int { get }
          func hash(into hasher: inout Swift::Hasher)
          func _rawHashValue(seed: Swift::Int) -> Swift::Int
        }
        public protocol CustomStringConvertible {
          var description: Swift::String { get }
        }
        public protocol Identifiable<ID> {
          associatedtype ID : Swift::Hashable
          var id: Self.ID { get }
        }
        public protocol Encodable {
          func encode(to encoder: any Swift::Encoder) throws
        }
        public protocol Decodable {
          init(from decoder: any Swift::Decoder) throws
        }
        public typealias Codable = Swift::Decodable & Swift::Encodable
        public protocol RawRepresentable<RawValue> {
          associatedtype RawValue
          init?(rawValue: Self.RawValue)
          var rawValue: Self.RawValue { get }
        }
        """;

    internal const string Foundation = """
        @available(macOS 13, iOS 16, tvOS 16, watchOS 9, *)
        public protocol CustomLocalizedStringResourceConvertible {
          var localizedStringResource: Foundation.LocalizedStringResource { get }
        }
        """;

    internal const string ExtensionFoundation = """
        @preconcurrency @_Concurrency.MainActor public protocol AppExtensionConfiguration : Swift.Sendable {
          nonisolated func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool
        }
        """;

    private readonly string _root;
    private readonly IDisposable _scope;

    private FakeSdkInterfaces(string root)
    {
        _root = root;
        _scope = SdkPublicModuleNames.Install(new SdkPublicModuleNames(root));
    }

    /// <summary>An SDK with the standard library, Foundation and ExtensionFoundation interfaces above.</summary>
    internal static FakeSdkInterfaces Standard()
        => Create(("Swift", false, Swift), ("Foundation", true, Foundation), ("ExtensionFoundation", true, ExtensionFoundation));

    internal static FakeSdkInterfaces Create(params (string Module, bool IsFramework, string Text)[] modules)
    {
        var root = Path.Combine(Path.GetTempPath(), "fake-sdk-" + Guid.NewGuid().ToString("N"));
        foreach (var (module, isFramework, text) in modules)
        {
            var dir = isFramework
                ? Path.Combine(root, "System", "Library", "Frameworks", $"{module}.framework", "Modules", $"{module}.swiftmodule")
                : Path.Combine(root, "usr", "lib", "swift", $"{module}.swiftmodule");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "arm64-apple-ios-simulator.swiftinterface"),
                $"// swift-interface-format-version: 1.0\n// swift-module-flags: -module-name {module}\n{text}\n");
        }
        return new FakeSdkInterfaces(root);
    }

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
