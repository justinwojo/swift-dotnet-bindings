// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The typed reader of a protocol a module the run never loads declares in its SDK interface
/// (<see cref="ForeignProtocolRequirements.TypedParentOf"/>): each requirement without a default,
/// with its declaration, attributes, parameter and return types, all or nothing per protocol.
/// </summary>
public class ForeignProtocolTypedRequirementsTests
{
    [Fact]
    public void ReadsTheNonisolatedRequirementWithItsTypes()
    {
        using var sdk = FakeSdkInterfaces.Standard();

        var parent = ForeignProtocolRequirements.TypedParentOf("ExtensionFoundation.AppExtensionConfiguration");

        Assert.NotNull(parent);
        Assert.Equal("ExtensionFoundation.AppExtensionConfiguration", parent!.QualifiedName);
        var accept = Assert.Single(parent.Requirements);
        Assert.Equal("accept", accept.Name);
        Assert.Equal("accept(connection:)", accept.Key);
        Assert.True(accept.IsNonisolated);
        Assert.False(accept.Throws);
        Assert.Contains("func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", accept.Declaration);
        var parameter = Assert.Single(accept.Parameters);
        Assert.Equal("connection", parameter.Label);
        Assert.Equal("connection", parameter.PrivateName);
        Assert.Equal("Foundation.NSXPCConnection", Assert.IsType<NamedTypeSpec>(parameter.Type).Name);
        Assert.Equal("Swift.Bool", Assert.IsType<NamedTypeSpec>(accept.ReturnType).Name);
    }

    [Fact]
    public void ReadsModuleSelectorsAsDots()
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            @preconcurrency @_Concurrency::MainActor public protocol Configuring : Swift::Sendable {
              nonisolated func accept(_ connection: Foundation::NSXPCConnection, count: Swift::Int) -> Swift::Bool
            }
            """));

        var parent = ForeignProtocolRequirements.TypedParentOf("Ext.Configuring");

        var accept = Assert.Single(parent!.Requirements);
        Assert.Equal("accept(_:count:)", accept.Key);
        Assert.Equal(new[] { "_", "count" }, accept.Parameters.Select(p => p.Label));
        Assert.Equal(new[] { "connection", "count" }, accept.Parameters.Select(p => p.PrivateName));
        Assert.Equal("Foundation.NSXPCConnection", Assert.IsType<NamedTypeSpec>(accept.Parameters[0].Type).Name);
        Assert.Equal("Swift.Int", Assert.IsType<NamedTypeSpec>(accept.Parameters[1].Type).Name);
    }

    [Fact]
    public void KeepsTheAttributeLinesAboveARequirement()
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring {
              @available(iOS 17.0, *)
              @_Concurrency.MainActor func refresh() throws
              func reset()
            }
            """));

        var parent = ForeignProtocolRequirements.TypedParentOf("Ext.Configuring");

        Assert.Equal(new[] { "refresh", "reset" }, parent!.Requirements.Select(r => r.Name));
        var refresh = parent.Requirements[0];
        Assert.Equal(new[] { "@available(iOS 17.0, *)" }, refresh.Attributes);
        Assert.True(refresh.Throws);
        Assert.True(refresh.IsMainActorIsolated);
        Assert.False(refresh.IsNonisolated);
        Assert.Same(TupleTypeSpec.Empty, refresh.ReturnType);
        Assert.Empty(parent.Requirements[1].Attributes);
    }

    [Fact]
    public void SkipsARequirementAnUnconstrainedExtensionDefaults()
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring {
              func accept(value: Swift.Int) -> Swift.Bool
              func describe() -> Swift.String
              func label() -> Swift.String
            }
            extension Ext.Configuring {
              public func describe() -> Swift.String { "" }
            }
            extension Ext.Configuring where Self : AnyObject {
              public func label() -> Swift.String { "" }
            }
            """));

        var parent = ForeignProtocolRequirements.TypedParentOf("Ext.Configuring");

        // The constrained extension's default does not hold for every conformer, so `label` stays.
        Assert.Equal(new[] { "accept", "label" }, parent!.Requirements.Select(r => r.Name));
    }

    [Fact]
    public void ABraceInAStringOfAnExtensionBodyDoesNotHideTheNextDefault()
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring {
              func accept(value: Swift.Int) -> Swift.Bool
              func describe() -> Swift.String
            }
            extension Ext.Configuring {
              @inlinable public func opening() -> Swift.String { "{" }
              public func describe() -> Swift.String { "" }
            }
            """));

        var parent = ForeignProtocolRequirements.TypedParentOf("Ext.Configuring");

        Assert.Equal(new[] { "accept" }, parent!.Requirements.Select(r => r.Name));
    }

    [Theory]
    [InlineData("func run<T>(_ value: T)")]                       // generic
    [InlineData("func run() async")]                              // async
    [InlineData("static func make() -> Swift.Int")]               // static
    [InlineData("var name: Swift.String { get }")]                // property
    [InlineData("init(value: Swift.Int)")]                        // initializer
    [InlineData("func copy() -> Self")]                           // Self
    [InlineData("func update(_ value: inout Swift.Int)")]         // inout
    [InlineData("func take(_ values: Swift.Int...)")]             // variadic
    [InlineData("associatedtype Element")]                        // associated type
    public void TypesNothingWhenOneRequirementDoesNotType(string untypable)
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, $$"""
            public protocol Configuring {
              func accept(value: Swift.Int) -> Swift.Bool
              {{untypable}}
            }
            """));

        Assert.Null(ForeignProtocolRequirements.TypedParentOf("Ext.Configuring"));
    }

    [Fact]
    public void TypesNothingWhenTheProtocolInheritsANonMarkerProtocol()
    {
        // One unbound level only: a parent that inherits requirements from another protocol stays
        // untyped, while a marker parent (Sendable) carries none.
        using var sdk = FakeSdkInterfaces.Create(
            ("Swift", false, FakeSdkInterfaces.Swift),
            ("Ext", true, """
                public protocol Base {
                  func base()
                }
                public protocol Configuring : Ext.Base, Swift.Sendable {
                  func accept(value: Swift.Int) -> Swift.Bool
                }
                """));

        Assert.Null(ForeignProtocolRequirements.TypedParentOf("Ext.Configuring"));
        Assert.NotNull(ForeignProtocolRequirements.TypedParentOf("Ext.Base"));
    }

    [Fact]
    public void TypesNothingForAProtocolNoInterfaceDeclares()
    {
        using var sdk = FakeSdkInterfaces.Standard();

        Assert.Null(ForeignProtocolRequirements.TypedParentOf("ExtensionFoundation.Missing"));
        Assert.Null(ForeignProtocolRequirements.TypedParentOf("NoSuchModule.AppExtensionConfiguration"));
    }

    [Fact]
    public void TypesAProtocolWithNoRequirementsAsEmpty()
    {
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring : Swift.Sendable {
            }
            """));

        var parent = ForeignProtocolRequirements.TypedParentOf("Ext.Configuring");

        Assert.NotNull(parent);
        Assert.Empty(parent!.Requirements);
    }
}
