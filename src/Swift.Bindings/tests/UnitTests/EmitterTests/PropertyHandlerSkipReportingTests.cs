// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Locks in the <see cref="SkipReason"/> classification for closure properties
/// that PropertyHandler skips. The fixture comment on
/// <c>BindingTests/Sources/SwiftBindingsTestLib/Types/AsyncClosurePropertySetter.swift</c>
/// promises that confirmHandler / primitiveHandler / factory are reported as
/// <c>UnsupportedClosure</c> skips in <c>binding-report.json</c>. The runtime
/// BindingTest can only assert that the surrounding type still binds — these
/// tests pin the report contract that "the skip was recorded under the right
/// reason" so downstream skip-metrics tooling and coverage dashboards stay
/// honest.
/// </summary>
[Collection("ReportCollector")]
public class PropertyHandlerSkipReportingTests
{
    [Fact]
    public void Emit_StripeShape_OptionalAsyncThrowingClosureProperty_RecordsUnsupportedClosureSkip()
    {
        // Stripe StripePaymentSheet.ConfirmHandler — `((Int32, Bool) async throws -> String)?`.
        var asyncThrowingClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("Swift.Int32"),
                new NamedTypeSpec("Swift.Bool"),
            }),
            new NamedTypeSpec("Swift.String"))
        {
            IsAsync = true,
            Throws = true,
        };
        var propertyType = new NamedTypeSpec("Swift.Optional", asyncThrowingClosure);
        AssertPropertyRecordedAsUnsupportedClosureSkip("confirmHandler", propertyType, hasGetter: false, hasSetter: true);
    }

    [Fact]
    public void Emit_OptionalAsyncNonThrowingClosureProperty_RecordsUnsupportedClosureSkip()
    {
        // Baseline-shape async non-throwing closure as a stored property — still
        // unsupported (the bridge can invoke an async closure but cannot synthesize
        // one from a C# (funcPtr, context) pair through a sync setter).
        var asyncClosure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int32"))
        {
            IsAsync = true,
            Throws = false,
        };
        var propertyType = new NamedTypeSpec("Swift.Optional", asyncClosure);
        AssertPropertyRecordedAsUnsupportedClosureSkip("factory", propertyType, hasGetter: true, hasSetter: true);
    }

    [Fact]
    public void Emit_BaselineAsyncThrowingClosureProperty_RecordsUnsupportedClosureSkip()
    {
        // Baseline-shape async-throwing closure `((Int32) async throws -> Int32)?`
        // — even though the *invocation* path is supported via the async-throwing
        // bridge, *storage* through a sync setter is not. The property must be
        // classified under UnsupportedClosure, not silently dropped.
        var asyncThrowingClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Int32"))
        {
            IsAsync = true,
            Throws = true,
        };
        var propertyType = new NamedTypeSpec("Swift.Optional", asyncThrowingClosure);
        AssertPropertyRecordedAsUnsupportedClosureSkip("primitiveHandler", propertyType, hasGetter: false, hasSetter: true);
    }

    private static void AssertPropertyRecordedAsUnsupportedClosureSkip(
        string propertyName,
        TypeSpec propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        try
        {
            var typeDatabase = CreateTypeDatabaseWithIntAndBool();
            var moduleDecl = CreateModuleDecl("TestModule");
            var classDecl = CreateClassDecl("PaymentSheet", moduleDecl);
            CreateProperty(classDecl, moduleDecl, propertyName, propertyType, hasGetter, hasSetter);

            ReportCollector.Start(moduleDecl);
            EmitProperty(classDecl.Properties[0], typeDatabase);
            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            Assert.Contains(report!.SkippedItems, item =>
                item.Kind == BindingItemKind.Property &&
                item.Name == propertyName &&
                item.ContainingType == "TestModule.PaymentSheet" &&
                item.Reason == SkipReason.UnsupportedClosure);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    private static void EmitProperty(PropertyDecl property, TypeDatabase typeDatabase)
    {
        var handler = new PropertyHandler(new NullLogger<PropertyHandler>());
        var env = handler.Marshal(property, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(new CSharpWriter(new StringWriter()), new SwiftWriter(new StringWriter()),
            env, conductor, TypeHandlerContext.Empty);
    }

    private static TypeDatabase CreateTypeDatabaseWithIntAndBool()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int", "System", "Int64", "$sSiMa");
        RegisterPrimitive(swiftModule, "Swift.Int32", "System", "Int32", "$ss5Int32VMa");
        RegisterPrimitive(swiftModule, "Swift.Bool", "System", "Boolean", "$sSbMa");
        RegisterPrimitive(swiftModule, "Swift.String", "Swift", "SwiftString", "$sSSMa",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        typeDb.AddModuleDatabase(swiftModule);

        return typeDb;
    }

    private static void RegisterPrimitive(ModuleTypeDatabase module, string swiftName, string csNamespace, string csName, string accessor,
        TypeRecordFlags flags = TypeRecordFlags.Frozen)
    {
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = accessor,
                Flags = flags,
                Kind = TypeRecordKind.Struct,
            });
    }

    private static ModuleDecl CreateModuleDecl(string moduleName)
    {
        return new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static ClassDecl CreateClassDecl(string className, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = className,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{className}"),
            MangledName = $"$s10TestModule{className.Length}{className}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static void CreateProperty(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string propertyName,
        TypeSpec propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        var accessors = new List<AccessorDecl>();
        var property = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = propertyType,
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
        };

        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{propertyName}_Get",
                    MangledName = $"$s{propertyName}g",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyType,
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl,
                        },
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = classDecl,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public,
                },
            });
        }

        if (hasSetter)
        {
            accessors.Add(new SetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{propertyName}_Set",
                    MangledName = $"$s{propertyName}s",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = TupleTypeSpec.Empty,
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl,
                        },
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyType,
                            Name = "value",
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl,
                        },
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = classDecl,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public,
                },
            });
        }

        classDecl.Properties.Add(property);
    }
}
