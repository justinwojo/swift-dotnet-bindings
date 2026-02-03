// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class EnumHandlerOutputTests
{
    [Fact]
    public void Emit_SimpleEnum_EmitsDirectCaseConstructorsAndCaseTag()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static Direction North", csOutput);
        Assert.Contains("private static extern void PInvoke_North(SwiftIndirectResult result);", csOutput);
        Assert.Contains("public enum CaseTag : uint", csOutput);
        Assert.Contains("North = 0,", csOutput);
        Assert.Contains("South = 1,", csOutput);
    }

    [Fact]
    public void Emit_RawRepresentableEnum_EmitsFromRawValueSupport()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int";
        enumDecl.Cases.Add(CreateCase("ok"));
        enumDecl.Cases.Add(CreateCase("error"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static Status? FromRawValue(long rawValue)", csOutput);
        Assert.Contains("private static extern IntPtr PInvoke_InitWithRawValue(long rawValue);", csOutput);
        Assert.Contains("var result = FromRawValue(0);", csOutput);
        Assert.Contains("var result = FromRawValue(1);", csOutput);
    }

    [Fact]
    public void Emit_EnumWithTupleAssociatedValue_EmitsTupleTryGet()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Payload", moduleDecl, isFrozen: true);
        var tupleCase = CreateCase("pair");
        tupleCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        }));
        enumDecl.Cases.Add(tupleCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public unsafe bool TryGetPair([MaybeNullWhen(false)] out System.Int64 value0, [MaybeNullWhen(false)] out System.Boolean value1)", csOutput);
        Assert.Contains("Pair = 0,", csOutput);
        Assert.Contains("None = 1,", csOutput);
    }

    [Fact]
    public void Emit_EnumWithAssociatedValueCaseAndMatchingStaticProperty_SkipsDuplicateStaticProperty()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("PlaybackMode", moduleDecl, isFrozen: true);

        var pausedCase = CreateCase("paused");
        pausedCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(pausedCase);

        enumDecl.Properties.Add(CreateStaticIntProperty("paused", enumDecl, moduleDecl));
        enumDecl.Properties.Add(CreateStaticIntProperty("active", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static PlaybackMode Paused(", csOutput);
        Assert.Contains("public static System.Int64 Active", csOutput);
        Assert.DoesNotContain("public static System.Int64 Paused", csOutput);
    }

    [Fact]
    public void Emit_GenericEnum_EmitsGenericTypeAndPInvokeHelper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ValueProviderStorage", moduleDecl, isFrozen: true);
        enumDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        var boxedCase = CreateCase("boxed");
        boxedCase.AssociatedValues.Add(new NamedTypeSpec("τ_0_0"));
        enumDecl.Cases.Add(boxedCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public unsafe class ValueProviderStorage<T0> : ISwiftObject where T0 : ISwiftObject", csOutput);
        Assert.Contains("public static ValueProviderStorage<T0> Boxed(T0 value0)", csOutput);
        Assert.Contains("var value0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();", csOutput);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value0, ref value0SwiftSpan);", csOutput);
        Assert.Contains("ValueProviderStorage_PInvoke.PInvoke_Boxed(indirectResult, (IntPtr)value0SwiftBuffer", csOutput);
        Assert.Contains("internal static class ValueProviderStorage_PInvoke", csOutput);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
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

    private static EnumDecl CreateEnumDecl(string name, ModuleDecl moduleDecl, bool isFrozen)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            Cases = new List<EnumCaseDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa"
        };
    }

    private static EnumCaseDecl CreateCase(string name)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s10TestModule4CaseO{name}yA2CmFWC",
            AssociatedValues = new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateRawValueInitializer(EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6StatusO8rawValueACSgSi_tcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{enumDecl.Name}"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "rawValue",
                    PrivateName = "rawValue",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static PropertyDecl CreateStaticIntProperty(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = true,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s10TestModule12PlaybackModeO{name}Sivg",
                        MethodType = MethodType.Static,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new()
                            {
                                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                                Name = string.Empty,
                                PrivateName = string.Empty,
                                IsInOut = false,
                                IsGeneric = false,
                                ParentDecl = null,
                                ModuleDecl = moduleDecl
                            }
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = enumDecl,
                        ModuleDecl = moduleDecl,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitEnum(EnumDecl enumDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new EnumHandler(new NullLogger<EnumHandler>());
        var env = handler.Marshal(enumDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
