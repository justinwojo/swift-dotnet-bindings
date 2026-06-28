// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration.Tests;

/// <summary>
/// Shared test helpers for ProtocolExtension* test classes.
/// Provides common setup methods for creating ModuleDecl, ClassDecl, TypeDatabase,
/// and ProtocolExtensionMethodDecl instances used across all protocol extension tests.
/// </summary>
internal static class ProtocolExtensionTestHelpers
{
    internal static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateSetup(string moduleName, string className, string protocolName)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", className),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
                MetadataAccessor = $"$s10{moduleName}{className.Length}{className}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var conformingType = CreateClassDecl(className, moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, conformingType, typeDatabase);
    }

    internal static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    internal static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    internal static ProtocolExtensionMethodDecl CreateExtMethod(string methodName, string rawSignature)
    {
        var printedName = $"{methodName}()";
        var parenStart = rawSignature.IndexOf('(');
        if (parenStart >= 0)
        {
            var parenEnd = rawSignature.IndexOf(')', parenStart);
            if (parenEnd > parenStart + 1)
            {
                var paramStr = rawSignature.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var labels = paramStr.Split(',').Select(p =>
                {
                    var trimmed = p.Trim();
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) return "_";
                    var label = trimmed.Substring(0, colonIdx).Trim();
                    if (label.StartsWith("_ ")) return "_";
                    return label;
                });
                printedName = $"{methodName}({string.Join("", labels.Select(l => l + ":"))})";
            }
        }

        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    /// <summary>
    /// Creates a read-only (or read-write) protocol-extension PROPERTY default decl
    /// (IsProperty=true), mirroring what the swiftinterface facts walker produces for
    /// `extension P { public var name: T { get } }`.
    /// </summary>
    internal static ProtocolExtensionMethodDecl CreateExtProperty(
        string propertyName, string rawSignature, bool hasSetter = false)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = propertyName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = true,
            HasSetter = hasSetter,
            PrintedName = propertyName,
            WhereConstraints = new List<string>()
        };
    }

    internal static Dictionary<string, List<ProtocolExtensionMethodDecl>> CreateExtensionMethodDict(
        string protocolQualifiedName, params ProtocolExtensionMethodDecl[] methods)
    {
        foreach (var m in methods)
            m.ProtocolQualifiedName = protocolQualifiedName;

        return new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [protocolQualifiedName] = methods.ToList()
        };
    }
}
