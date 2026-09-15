// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ModuleProcessorProtocolOwnershipTests
{
    [Theory]
    [InlineData("AdditiveArithmetic", "IAdditiveArithmetic")]
    [InlineData("Numeric", "INumeric")]
    public void ForeignProtocolReExport_RecordsGeneratedInterfaceInCurrentModuleNamespace(
        string protocolName,
        string interfaceName)
    {
        // Swift Numerics' RealModule emits local interfaces for Swift.AdditiveArithmetic and
        // Swift.Numeric. ComplexModule later consumes those records when it writes conformance
        // metadata. The record must therefore describe the emitted RealModule interface while
        // retaining the original Swift identity used for database lookup.
        const string currentModule = "RealModule";
        var moduleDecl = CreateModuleDecl(currentModule);
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Swift.{protocolName}");
        var typeSpec = new NamedTypeSpec(swiftTypeName.ModuleQualifiedName);
        var protocolDecl = new ProtocolDecl
        {
            Name = protocolName,
            SwiftTypeName = swiftTypeName,
            MangledName = $"$ss{protocolName.Length}{protocolName}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var processor = new ModuleProcessor(
            currentModule,
            "/tmp/RealModule.dylib",
            currentModule,
            new Dictionary<NamedTypeSpec, TypeDecl> { { typeSpec, protocolDecl } },
            new TypeDatabase(),
            NullLogger.Instance,
            new NamespacePatternResolver("Bindings.{Module}"));

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal($"Swift.{protocolName}", record!.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal($"Bindings.RealModule.{interfaceName}", record.CSharpTypeName.FullyQualifiedName);
    }

    private static ModuleDecl CreateModuleDecl(string name) => new()
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };
}
