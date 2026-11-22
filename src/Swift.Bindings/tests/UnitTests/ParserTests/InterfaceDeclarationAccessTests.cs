// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Whether the SwiftInterfaceParser host reads a declaration as public depends on its own
/// access level, never on the other modifiers an interface prints with it, and a keyword-named
/// member keys by its unescaped name. <c>PublicMemberNames</c> is negative space for the ABI
/// parser: a public member missing from it is withdrawn as module-internal, so every spelling
/// below once cost a consumer a working member (<c>public __consuming func makeIterator()</c>
/// on a Sequence conformer, <c>public func `repeat`(count:)</c>).
/// </summary>
public class InterfaceDeclarationAccessTests
{
    [SkippableTheory]
    // Ownership modifiers, old and new spellings.
    [InlineData("public __consuming func makeIterator() -> Kit.Box.Iterator", "Box.makeIterator()")]
    [InlineData("public consuming func take() -> Swift.Int", "Box.take()")]
    [InlineData("public borrowing func peek() -> Swift.Int", "Box.peek()")]
    [InlineData("public mutating func bump()", "Box.bump()")]
    [InlineData("public nonmutating func touch()", "Box.touch()")]
    // Isolation, dispatch and storage modifiers after the access keyword.
    [InlineData("public nonisolated func detached()", "Box.detached()")]
    [InlineData("public dynamic func observed()", "Box.observed()")]
    [InlineData("public final override func refresh()", "Box.refresh()")]
    [InlineData("public static func make() -> Kit.Box", "Box.make()")]
    [InlineData("public nonisolated init(value: Swift.Int)", "Box.init(value:)")]
    [InlineData("public dynamic var observedValue: Swift.Int { get }", "Box.observedValue")]
    [InlineData("public unowned(unsafe) var owner: Swift.AnyObject", "Box.owner")]
    [InlineData("public nonisolated var label: Swift.String { get }", "Box.label")]
    [InlineData("public private(set) var count: Swift.Int { get }", "Box.count")]
    [InlineData("public final subscript(key: Swift.String) -> Swift.Int { get }", "Box.subscript(_:)")]
    [InlineData("public nonmutating subscript(index index: Swift.Int) -> Swift.Int { get nonmutating set }", "Box.subscript(index:)")]
    // Keyword-named members print backticked.
    [InlineData("public func `repeat`(count: Swift.Int) -> Kit.Box", "Box.repeat(count:)")]
    [InlineData("public static func `default`() -> Kit.Box", "Box.default()")]
    [InlineData("public var `class`: Swift.Int { get }", "Box.class")]
    public void PublicMember_IsPublicWhateverModifiersFollowTheAccessKeyword(string declaration, string expectedKey)
    {
        var facts = Produce($$"""
            // swift-module-flags: -module-name Kit
            public struct Box {
              public struct Iterator { }
              {{declaration}}
            }
            """);
        Assert.Contains(expectedKey, facts.PublicMemberNames!);
    }

    [SkippableFact]
    public void OpenClassMembers_ArePublicWithOverrideAndClassModifiers()
    {
        var facts = Produce("""
            // swift-module-flags: -module-name Kit
            open class Node {
              open override func refresh()
              open class func make() -> Kit.Node
              open override subscript(index: Swift.Int) -> Swift.Int { get }
              public required init(seed: Swift.Int)
            }
            """);
        Assert.Contains("Node.refresh()", facts.PublicMemberNames!);
        Assert.Contains("Node.make()", facts.PublicMemberNames!);
        Assert.Contains("Node.subscript(_:)", facts.PublicMemberNames!);
        Assert.Contains("Node.init(seed:)", facts.PublicMemberNames!);
    }

    [SkippableFact]
    public void TypeWithExtraModifiers_ScopesItsMembers()
    {
        // `public indirect enum` used to fail the type-scope shape, so the type was missing from
        // the public type names and its members were keyed bare: both read as internal.
        var facts = Produce("""
            // swift-module-flags: -module-name Kit
            @available(iOS 17.0, macOS 14.0, *)
            public indirect enum Tree {
              case leaf
              case node(Kit.Tree, Kit.Tree)
              @available(iOS 18.0, macOS 15.0, *)
              public func depth(limit: Swift.Int = 8) -> Swift.Int
            }
            """);
        Assert.Contains("Tree", facts.PublicTypeNames!);
        Assert.Contains("Tree.depth(limit:)", facts.PublicMemberNames!);
        Assert.DoesNotContain("depth(limit:)", facts.PublicMemberNames!);
        Assert.Equal("limit", Assert.Single(facts.ParameterNames!["Tree.depth(limit:)"]));
        Assert.Equal("8", facts.DefaultParameterValues!["Tree.depth(limit:)"][0]);
        Assert.True(facts.AvailabilityAnnotations!.ContainsKey("Tree"));
        Assert.True(facts.AvailabilityAnnotations!.ContainsKey("Tree.depth(limit:)"));
    }

    [SkippableFact]
    public void NonPublicMembers_StayOutOfThePublicSet()
    {
        var facts = Produce("""
            // swift-module-flags: -module-name Kit
            public struct Box {
              @usableFromInline
              internal func hidden()
              @usableFromInline
              internal var storage: Swift.Int
              public internal(set) var visible: Swift.Int { get }
              @usableFromInline
              package func packaged()
            }
            """);
        Assert.DoesNotContain("Box.hidden()", facts.PublicMemberNames!);
        Assert.DoesNotContain("Box.storage", facts.PublicMemberNames!);
        Assert.DoesNotContain("Box.packaged()", facts.PublicMemberNames!);
        Assert.Contains("Box.hidden()", facts.InternalMemberKeys!);
        Assert.Contains("Box.visible", facts.PublicMemberNames!);
    }

    [SkippableFact]
    public void SignatureFacts_FollowTheAccessLevelAndUnescapedName()
    {
        // Defaults were read only for an allow-listed modifier shape, so an override's,
        // a required init's or a keyword-named member's defaults were lost, and with them
        // the default-trimmed overloads.
        var facts = Produce("""
            // swift-module-flags: -module-name Kit
            open class Clip {
              open override func play(rate: Swift.Double = 1.0)
              public required init(seed: Swift.Int = 0)
              public func `repeat`(count: Swift.Int = 2) -> Kit.Clip
              public nonisolated func detached(after delay: Swift.Double = 0.5)
              public __consuming func consumed(flag: Swift.Bool = false)
            }
            """);
        Assert.Equal("1.0", facts.DefaultParameterValues!["Clip.play(rate:)"][0]);
        Assert.Equal("0", facts.DefaultParameterValues!["Clip.init(seed:)"][0]);
        Assert.Equal("2", facts.DefaultParameterValues!["Clip.repeat(count:)"][0]);
        Assert.Equal("0.5", facts.DefaultParameterValues!["Clip.detached(after:)"][0]);
        Assert.Equal("false", facts.DefaultParameterValues!["Clip.consumed(flag:)"][0]);
        Assert.Equal("count", Assert.Single(facts.ParameterNames!["Clip.repeat(count:)"]));
        Assert.Equal("delay", Assert.Single(facts.ParameterNames!["Clip.detached(after:)"]));
    }

    [SkippableFact]
    public void AvailabilityFacts_FollowTheAccessLevelAndUnescapedName()
    {
        // A member floor the walker skipped leaves the member on its type's lower floor,
        // so nothing guards a call that reaches a weak import absent on an older OS.
        var facts = Produce("""
            // swift-module-flags: -module-name Kit
            public struct Box {
              @available(iOS 18.0, macOS 15.0, *)
              public __consuming func makeIterator() -> Swift.Int
              @available(iOS 18.0, macOS 15.0, *)
              public func `repeat`(count: Swift.Int) -> Kit.Box
              @available(iOS 18.0, macOS 15.0, *)
              public nonisolated var label: Swift.String { get }
              @available(iOS 18.0, macOS 15.0, *)
              public var `class`: Swift.Int { get }
            }
            open class Node {
              @available(iOS 18.0, macOS 15.0, *)
              open override func refresh()
              @available(iOS 18.0, macOS 15.0, *)
              public required init(seed: Swift.Int)
              @available(iOS 18.0, macOS 15.0, *)
              open override subscript(index: Swift.Int) -> Swift.Int { get }
            }
            """);
        foreach (var key in new[]
                 {
                     "Box.makeIterator()", "Box.repeat(count:)", "Box.label", "Box.class",
                     "Node.refresh()", "Node.init(seed:)", "Node.subscript(_:)",
                 })
        {
            Assert.True(facts.AvailabilityAnnotations!.ContainsKey(key), $"no availability for {key}");
        }
    }

    private static PartialSwiftInterfaceFacts Produce(string source)
    {
        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        Xunit.Skip.IfNot(binaryPath is not null && File.Exists(binaryPath),
            "SwiftInterfaceParser binary not found. Run `nuke compile` (or set SWIFT_INTERFACE_PARSER_PATH).");
        var path = Path.Combine(Path.GetTempPath(), $"DeclAccess-{Guid.NewGuid()}.swiftinterface");
        File.WriteAllText(path, source);
        try
        {
            return new SwiftSyntaxInterfaceFactsProducer(binaryPath!).Produce(path, NullLogger.Instance).Facts;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
