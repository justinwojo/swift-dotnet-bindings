// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The pre-pass that gives a protocol the requirements it inherits from a protocol of an SDK module
/// the run never loads (<see cref="UnboundParentRequirements"/>), the shape of LiveCommunicationKit's
/// <c>LiveAssistanceExtensionConfiguration : ExtensionFoundation.AppExtensionConfiguration</c>.
/// </summary>
public class UnboundParentRequirementsTests
{
    private const string Parent = "ExtensionFoundation.AppExtensionConfiguration";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddsTheParentsRequirementToTheChild(bool viaGenericSig)
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig);
        var module = Module(child);

        var added = UnboundParentRequirements.Inject(module, Database(), NullLogger.Instance);

        var accept = Assert.Single(added);
        Assert.Same(accept, Assert.Single(child.Methods));
        Assert.Equal("accept", accept.Name);
        Assert.Same(child, accept.ParentDecl);
        Assert.True(accept.IsProtocolRequirement);
        Assert.True(accept.IsNonisolated);
        Assert.False(accept.IsMainActorIsolated);
        Assert.False(accept.IsAsync);
        Assert.False(accept.Throws);
        Assert.Equal(Parent, accept.InheritedFromUnboundProtocol);
        Assert.StartsWith(UnboundParentRequirements.SyntheticSymbolPrefix, accept.MangledName);
        Assert.Matches("^[A-Za-z0-9_]+$", accept.MangledName);
        Assert.Equal("Swift.Bool", Assert.IsType<NamedTypeSpec>(accept.CSSignature[0].SwiftTypeSpec).Name);
        var connection = accept.CSSignature[1];
        Assert.Equal("connection", connection.Name);
        Assert.Equal("connection", connection.PrivateName);
        Assert.Equal("Foundation.NSXPCConnection", Assert.IsType<NamedTypeSpec>(connection.SwiftTypeSpec).Name);
        Assert.Equal("accept(connection:)", ProtocolExtensionEmitter.RequirementKeyOf(accept));
    }

    [Fact]
    public void AnUnreadableParentAddsNothing()
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Protocol("UnreadableChild", "ExtensionFoundation.Missing");

        Assert.Empty(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));
        Assert.Empty(child.Methods);
    }

    [Fact]
    public void AParentWithARequirementTypeWithoutAProjectionAddsNothing()
    {
        // The surface covers Foundation but declares no NSXpcConnection: the reference has no
        // projection, so the child's conformance stays with the wrapper compile.
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSUnrelated"));
        var child = Child(viaGenericSig: false);

        Assert.Empty(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));
        Assert.Empty(child.Methods);
    }

    [Fact]
    public void ABoundDependencyParentIsLeftToTheDependencyWalks()
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig: false);
        var module = Module(child);
        module.DependencyProtocols["ExtensionFoundation"] = new List<ProtocolDecl>();

        Assert.Empty(UnboundParentRequirements.Inject(module, Database(), NullLogger.Instance));
    }

    [Fact]
    public void ARequirementTheChildDeclaresIsNotAddedAgain()
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig: false);
        var own = Method(child, "accept", ("connection", new NamedTypeSpec("Foundation.NSXPCConnection")));
        child.Methods.Add(own);

        Assert.Empty(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));
        Assert.Same(own, Assert.Single(child.Methods));
    }

    [Theory]
    [InlineData("<τ_0_0 where τ_0_0 : TestModule.ForeignConfigAccepting>", false)]
    [InlineData("<τ_0_0 where τ_0_0 : TestModule.ForeignConfigAccepting, τ_0_0 : AnyObject>", true)]
    public void ARequirementTheChildsOwnExtensionDefaultsIsNotAdded(string extensionSignature, bool added)
    {
        // An unconstrained default in the child's module witnesses the requirement for every
        // conformer; a constrained one does not, so the requirement is still the child's.
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig: false);
        var extension = Method(child, "accept", ("connection", new NamedTypeSpec("Foundation.NSXPCConnection")));
        extension.IsProtocolRequirement = false;
        extension.IsExtensionMethod = true;
        extension.RawGenericSig = extensionSignature;
        child.ExtensionMembers.Add(extension);

        var result = UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance);

        Assert.Equal(added ? 1 : 0, result.Count);
        Assert.Equal(added ? 1 : 0, child.Methods.Count);
    }

    [Theory]
    // An unconstrained default the bound module declares on the parent witnesses the requirement for
    // every conformer, Swift's own carrier included: the C# side must not take the call over.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "", "public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", false)]
    // A default constrained to the child alone witnesses it for every conformer of the child.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "Self : TestModule.ForeignConfigAccepting", "public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", false)]
    // Constrained beyond the child, it does not.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "Self : AnyObject", "public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", true)]
    // A same-label member of another type is an overload, not a witness.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "", "public func accept(connection: Foundation.NSObject) -> Swift.Bool", true)]
    // An extension of another module's protocol of the same name extends another protocol.
    [InlineData("OtherKit.AppExtensionConfiguration", "", "public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", true)]
    // A throwing member cannot witness a requirement that does not throw.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "", "public func accept(connection: Foundation.NSXPCConnection) throws -> Swift.Bool", true)]
    // A mutating member cannot witness a requirement that is not mutating.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "", "nonisolated public mutating func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", true)]
    // A consuming one can: Swift copies the receiver.
    [InlineData("ExtensionFoundation.AppExtensionConfiguration", "", "nonisolated public consuming func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", false)]
    public void ADefaultTheModuleDeclaresOnTheParentIsHonoured(string extendedType, string whereConstraint, string signature, bool added)
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"), Class("Foundation", "NSObject"));
        var child = Child(viaGenericSig: false);
        var candidate = new ExtensionMemberCandidate
        {
            ExtendedTypeName = extendedType,
            MethodName = "accept",
            RawSignature = signature,
            PrintedName = "accept(connection:)",
            WhereConstraints = whereConstraint.Length == 0 ? new List<string>() : new List<string> { whereConstraint },
        };

        var result = UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance, new[] { candidate });

        Assert.Equal(added ? 1 : 0, result.Count);
        Assert.Equal(added ? 1 : 0, child.Methods.Count);
    }

    [Fact]
    public void ARequirementTheModuleDefaultsDoesNotBlockTheOthers()
    {
        // Only what the child still needs must project: a requirement over a type with no
        // projection, defaulted by the module on the parent, leaves the rest to be added.
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring {
              func accept(value: Swift.Int) -> Swift.Bool
              func peek(on: Ext.Unprojected)
            }
            """));
        var child = Protocol("ExtChild", "Ext.Configuring");
        var candidate = new ExtensionMemberCandidate
        {
            ExtendedTypeName = "Ext.Configuring",
            MethodName = "peek",
            RawSignature = "public func peek(on: Ext.Unprojected)",
            PrintedName = "peek(on:)",
            WhereConstraints = new List<string>(),
        };

        Assert.Empty(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));

        var added = Assert.Single(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance, new[] { candidate }));
        Assert.Equal("accept", added.Name);
    }

    [Fact]
    public void ASameLabelOverloadOfAnotherTypeDoesNotSuppressTheRequirement()
    {
        // `accept(connection: NSObject)` does not witness `accept(connection: NSXPCConnection)`.
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"), Class("Foundation", "NSObject"));
        var child = Child(viaGenericSig: false);
        child.Methods.Add(Method(child, "accept", ("connection", new NamedTypeSpec("Foundation.NSObject"))));

        var added = Assert.Single(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));

        Assert.Equal("Foundation.NSXPCConnection", added.CSSignature[1].SwiftTypeSpec!.ToString());
        Assert.Equal(2, child.Methods.Count);
    }

    [Fact]
    public void AParentTwoProtocolsOfTheModuleRefineAddsNothing()
    {
        // The carrier's conformance to the parent is one, so a witness written for one child would
        // answer for the other too.
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var first = Child(viaGenericSig: false);
        var second = Protocol("OtherConfigAccepting", Parent);

        Assert.Empty(UnboundParentRequirements.Inject(Module(first, second), Database(), NullLogger.Instance));
        Assert.Empty(first.Methods);
        Assert.Empty(second.Methods);
    }

    [Fact]
    public void ARequirementWithItsOwnAvailabilityAddsNothing()
    {
        // A requirement introduced after its parent needs availability the added declaration
        // does not carry.
        using var sdk = FakeSdkInterfaces.Create(("Ext", true, """
            public protocol Configuring {
              func accept(value: Swift.Int) -> Swift.Bool
              @available(iOS 27.2, macOS 27.2, *)
              func refresh()
            }
            """));
        var child = Protocol("ExtChild", "Ext.Configuring");

        Assert.Empty(UnboundParentRequirements.Inject(Module(child), Database(), NullLogger.Instance));
        Assert.Empty(child.Methods);
    }

    [Fact]
    public void TheAddedRequirementTakesASlotInTheChildsLayout()
    {
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig: false);
        var database = Database();
        UnboundParentRequirements.Inject(Module(child), database, NullLogger.Instance);

        var layout = new VtableLayoutBuilder(database).Build(child);

        var slot = Assert.Single(layout.IncludedSlots);
        Assert.Equal(0, slot.SlotIndex);
        Assert.Single(layout.MethodSlotIndexByKey);
    }

    [Fact]
    public void TheCarrierWitnessesTheAddedRequirementThroughTheVtable()
    {
        // No stub: the carrier's witness in the child's extension calls the managed implementation
        // through the child's vtable slot, and its answer is that implementation's.
        using var sdk = FakeSdkInterfaces.Standard();
        using var surface = Surface(Class("Foundation", "NSXpcConnection"));
        var child = Child(viaGenericSig: false);
        var database = Database();
        UnboundParentRequirements.Inject(Module(child), database, NullLogger.Instance);
        var emitter = new EveryProtocolEmitter(database, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(new[] { child });

        var writer = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(writer), child);
        var output = writer.ToString();

        Assert.Contains("extension EveryProtocol: TestModule.ForeignConfigAccepting", output);
        Assert.Contains("func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool", output);
        Assert.Contains("_vtable.func_accept_0", output);
        Assert.DoesNotContain("return true", output);
        Assert.DoesNotContain("return false", output);
        Assert.DoesNotContain(UnboundParentRequirements.SyntheticSymbolPrefix, output);
    }

    [Fact]
    public void APInvokeNamingTheSyntheticSymbolFailsTheGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "unbound-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Bindings.cs"),
                "[LibraryImport(\"Lib\", EntryPoint = \"SBUNBOUND_TestModule_Child_ExtensionFoundation_P_accept_connection__\")]\n");

            Assert.True(WrapperSymbolIntegrityGate.HasViolations(directory, NullLogger.Instance));

            File.WriteAllText(Path.Combine(directory, "Bindings.cs"), "// no P/Invoke\n");
            Assert.False(WrapperSymbolIntegrityGate.HasViolations(directory, NullLogger.Instance));

            // A real Swift symbol that merely contains the prefix (a type named SBUNBOUND_Widget).
            File.WriteAllText(Path.Combine(directory, "Bindings.cs"),
                "[LibraryImport(\"Lib\", EntryPoint = \"$s10TestModule16SBUNBOUND_WidgetCMa\")]\n");
            Assert.False(WrapperSymbolIntegrityGate.HasViolations(directory, NullLogger.Instance));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProtocolDecl Child(bool viaGenericSig)
    {
        if (!viaGenericSig)
            return Protocol("ForeignConfigAccepting", Parent);
        var child = Protocol("ForeignConfigAccepting", "AppExtensionConfiguration");
        child.GenericSignature = $"<τ_0_0 : {Parent}>";
        return child;
    }

    private static ProtocolDecl Protocol(string name, params string[] inherited) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}P",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        InheritedProtocols = inherited.Select(n => new NamedTypeSpec(n)).ToList(),
        IsMainActorIsolated = true,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static MethodDecl Method(ProtocolDecl parent, string name, params (string Label, TypeSpec Type)[] parameters)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = null,
            IsProtocolRequirement = true,
        };
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "", PrivateName = "", SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
            IsInOut = false, IsGeneric = false, ParentDecl = method, ModuleDecl = null,
        });
        foreach (var (label, type) in parameters)
            method.CSSignature.Add(new ArgumentDecl
            {
                Name = label, PrivateName = label, SwiftTypeSpec = type,
                IsInOut = false, IsGeneric = false, ParentDecl = method, ModuleDecl = null,
            });
        return method;
    }

    private static ModuleDecl Module(params ProtocolDecl[] protocols)
    {
        var module = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(protocols),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(protocols),
        };
        foreach (var protocol in protocols)
            protocol.ModuleDecl = module;
        return module;
    }

    private static TypeDatabase Database()
    {
        // The standard library's records (Swift.Bool); Foundation's classes come from the surface.
        var database = new TypeDatabase();
        database.LoadModuleDatabaseFromFile(Path.Combine(AppContext.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();
        database.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
        return database;
    }

    private static IDisposable Surface(params AppleTypeSurfaceEntry[] entries)
    {
        var byFull = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        var byBare = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byFull[$"{entry.Namespace}.{entry.Name}"] = entry;
            byBare.TryAdd(entry.Name, entry);
        }
        return AppleTypeSurfaceIndex.OverrideDefaultForTest(new AppleTypeSurfaceIndex(byFull, byBare));
    }

    private static AppleTypeSurfaceEntry Class(string ns, string name) => new(name, ns, AppleTypeSurfaceKind.Class, null, false);
}
