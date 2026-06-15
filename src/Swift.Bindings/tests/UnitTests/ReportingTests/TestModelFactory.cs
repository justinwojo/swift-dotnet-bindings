// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// Lightweight builders for the Swift declaration model used by reporting
/// tests. The shape mirrors the inline helpers that previously lived in
/// <see cref="ReportCollectorTests"/>; <see cref="MemberDiagnosticIdentityTests"/>
/// shares them. Kept minimal — these aren't realistic decls; they're just
/// enough to exercise the diagnostic identity surface.
/// </summary>
internal static class TestModelFactory
{
    public static ModuleDecl CreateModuleDecl(string name = "TestModule")
    {
        var moduleDecl = new ModuleDecl
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
        moduleDecl.Methods.Add(CreateMethod("topLevel", parent: moduleDecl));

        var classDecl = new ClassDecl
        {
            Name = "Loader",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{name}.Loader"),
            MangledName = $"$s{name.Length}{name}6LoaderCN",
            Properties = new List<PropertyDecl> { CreateProperty("State", parent: moduleDecl) },
            Methods = new List<MethodDecl> { CreateMethod("Fetch", parent: moduleDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        var nestedStruct = new StructDecl
        {
            Name = "Payload",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{name}.Loader.Payload"),
            MangledName = $"$s{name.Length}{name}6LoaderV7PayloadV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethod("Read", parent: classDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = true,
            MetadataAccessor = $"$s{name.Length}{name}6LoaderV7PayloadVMa",
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
        };

        classDecl.Types.Add(nestedStruct);
        moduleDecl.Types.Add(classDecl);

        var protocolDecl = new ProtocolDecl
        {
            Name = "IThing",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{name}.IThing"),
            MangledName = $"$s{name.Length}{name}6IThingP",
            Properties = new List<PropertyDecl> { CreateProperty("Value", parent: moduleDecl) },
            Methods = new List<MethodDecl> { CreateMethod("DoWork", parent: moduleDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        // ProtocolDecl : TypeDecl, so the parser's OfType<TypeDecl>() puts protocols
        // in both moduleDecl.Types and moduleDecl.Protocols.
        moduleDecl.Types.Add(protocolDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        return moduleDecl;
    }

    public static MethodDecl CreateMethod(
        string name,
        BaseDecl? parent = null,
        IEnumerable<(string Label, string SwiftType)>? args = null,
        string? mangledName = null,
        MethodType methodType = MethodType.Instance,
        bool isConstructor = false)
    {
        var argumentDecls = new List<ArgumentDecl>();
        var resolvedArgs = args?.ToList();
        if (resolvedArgs == null || resolvedArgs.Count == 0)
        {
            argumentDecls.Add(new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parent,
                ModuleDecl = parent?.ModuleDecl,
            });
        }
        else
        {
            foreach (var (label, swiftType) in resolvedArgs)
            {
                argumentDecls.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec(swiftType),
                    Name = label,
                    PrivateName = label,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parent,
                    ModuleDecl = parent?.ModuleDecl,
                });
            }
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName ?? $"$s4Test{name.Length}{name}yyF",
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = argumentDecls,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = parent,
            ModuleDecl = parent?.ModuleDecl,
        };
    }

    public static PropertyDecl CreateProperty(string name, BaseDecl? parent) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
        HasStorage = false,
        IsStatic = false,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = parent,
        ModuleDecl = parent?.ModuleDecl,
    };

    public static SubscriptDecl CreateSubscript(
        BaseDecl? parent,
        IEnumerable<(string Label, string SwiftType)> indexParams,
        string returnType = "Swift.Int",
        string mangledName = "$s_subscript")
    {
        var indices = new List<ArgumentDecl>();
        foreach (var (label, swiftType) in indexParams)
        {
            indices.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(swiftType),
                Name = label,
                PrivateName = label,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parent,
                ModuleDecl = parent?.ModuleDecl,
            });
        }
        return new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec(returnType),
            IndexParameters = indices,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = mangledName,
            ParentDecl = parent,
            ModuleDecl = parent?.ModuleDecl,
        };
    }

    public static OperatorDecl CreateOperator(
        string symbol,
        BaseDecl? parent,
        IEnumerable<(string Label, string SwiftType)> args,
        string mangledName = "$s_operator")
    {
        var underlying = CreateMethod(
            $"op_{symbol}",
            parent,
            args: args,
            mangledName: mangledName,
            methodType: MethodType.Static);
        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = underlying,
            ParentDecl = parent,
            ModuleDecl = parent?.ModuleDecl,
        };
    }
}
