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
    public void Emit_SimpleEnum_EmitsCSharpEnumValueType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Simple enums emit as C# enum value types
        Assert.Contains("public enum Direction : int", csOutput);
        Assert.Contains("North = 0,", csOutput);
        Assert.Contains("South = 1,", csOutput);
        // Should NOT contain class-based emission
        Assert.DoesNotContain("unsafe class Direction", csOutput);
        Assert.DoesNotContain("SwiftSafeHandle", csOutput);
    }

    [Fact]
    public void Emit_RawRepresentableIntEnum_EmitsCSharpEnumValueType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int";
        enumDecl.Cases.Add(CreateCase("ok"));
        enumDecl.Cases.Add(CreateCase("error"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Int-raw-value enums qualify as simple enums → C# enum value type
        // Swift "Int" maps to C# "int" for enum underlying types (not nint/long)
        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("Ok = 0,", csOutput);
        Assert.Contains("Error = 1,", csOutput);
        // Should NOT contain class-based emission
        Assert.DoesNotContain("FromRawValue", csOutput);
        Assert.DoesNotContain("SwiftSafeHandle", csOutput);
    }

    [Fact]
    public void Emit_StringRawRepresentableEnum_EmitsClassNotSimpleEnum()
    {
        // String enums should NOT qualify as simple enums
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should be class-based, not C# enum
        Assert.Contains("public class LogLevel", csOutput);
        Assert.Contains("FromRawValue", csOutput);
        Assert.DoesNotContain("public enum LogLevel", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnum_EmitsClassNotSimpleEnum()
    {
        // Non-frozen enums must NOT be simple enums (library evolution safety)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("class Status", csOutput);
        Assert.DoesNotContain("public enum Status", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithExtensionMethod_EmitsExtensionClassAndSwiftWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));

        // Add an instance method returning the same enum type
        var oppositeMethod = new MethodDecl
        {
            Name = "opposite",
            MangledName = "$s10TestModule9DirectionO8oppositeA2CyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Direction"),
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
        };
        enumDecl.Methods.Add(oppositeMethod);

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# should have enum + extensions class with Opposite method
        Assert.Contains("public enum Direction : int", csOutput);
        Assert.Contains("public static class DirectionExtensions", csOutput);
        Assert.Contains("public static Direction Opposite(this Direction self)", csOutput);
        Assert.Contains("(Direction)PInvoke_Opposite((int)self)", csOutput);
        Assert.Contains("[DllImport(", csOutput);

        // Swift wrapper should have tag-to-case conversion
        Assert.Contains("switch tag {", swiftOutput);
        Assert.Contains("case 0: value = .north", swiftOutput);
        Assert.Contains("case 1: value = .south", swiftOutput);
        Assert.Contains("value.opposite()", swiftOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithUnsupportedMethod_SkipsMethodNoEmptyExtensions()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Color", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("red"));
        enumDecl.Cases.Add(CreateCase("blue"));

        // Add a method with unsupported parameter type (Hasher)
        var hashMethod = new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule5ColorO4hashySHzF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Hasher"),
                    Name = "into",
                    PrivateName = "hasher",
                    IsInOut = true,
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
        enumDecl.Methods.Add(hashMethod);

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should emit C# enum but NOT the empty extensions class
        Assert.Contains("public enum Color : int", csOutput);
        Assert.DoesNotContain("ColorExtensions", csOutput);
        // No Swift wrapper for unsupported method
        Assert.DoesNotContain("_sbw_Color_hash", swiftOutput);
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

        Assert.Contains("public bool TryGetPair([MaybeNullWhen(false)] out System.Int64 value0, [MaybeNullWhen(false)] out System.Boolean value1)", csOutput);
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

        Assert.Contains("public static unsafe PlaybackMode Paused(", csOutput);
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

        Assert.Contains("public class ValueProviderStorage<T0> : ISwiftObject where T0 : ISwiftObject", csOutput);
        Assert.Contains("public static unsafe ValueProviderStorage<T0> Boxed(T0 value0)", csOutput);
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

    [Fact]
    public void Emit_NonFrozenStringRawValueEnum_UsesCopyMemoryInsteadOfStoreBytes()
    {
        // Regression test: non-frozen enums with String raw values must NOT use storeBytes
        // because Optional<Enum> with String raw values is not BitwiseCopyable in Swift 6+.
        // The wrapper must use withUnsafePointer + copyMemory instead.
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("StatusCode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        EnumHandler.ResetUtf8SliceTracking();
        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should use withUnsafePointer + copyMemory pattern
        Assert.Contains("withUnsafePointer(to: result)", swiftOutput);
        Assert.Contains("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);

        // Should NOT use storeBytes API call (BitwiseCopyable crash in Swift 6+)
        Assert.DoesNotContain(".storeBytes(of:", swiftOutput);
    }

    [Fact]
    public void Emit_SameNamedNestedEnumsWithStringRawValue_ProducesDistinctWrapperSymbols()
    {
        // Regression test: same-named nested enums in different containers should
        // produce distinct SBW_*_InitWithRawValue wrapper symbols to avoid collisions.
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create first nested enum: TestModule.Container1.ErrorType
        var container1 = CreateStructDecl("Container1", moduleDecl);
        var enum1 = CreateNestedEnumDecl("ErrorType", container1, moduleDecl, isFrozen: true);
        enum1.RawValueTypeName = "String";
        enum1.Cases.Add(CreateCase("unknown"));
        enum1.Methods.Add(CreateStringRawValueInitializer(enum1, moduleDecl));
        container1.Types.Add(enum1);

        // Create second nested enum: TestModule.Container2.ErrorType
        var container2 = CreateStructDecl("Container2", moduleDecl);
        var enum2 = CreateNestedEnumDecl("ErrorType", container2, moduleDecl, isFrozen: true);
        enum2.RawValueTypeName = "String";
        enum2.Cases.Add(CreateCase("failed"));
        enum2.Methods.Add(CreateStringRawValueInitializer(enum2, moduleDecl));
        container2.Types.Add(enum2);

        // Reset shared state and emit both enums
        EnumHandler.ResetUtf8SliceTracking();
        var (_, swiftOutput1) = EmitEnum(enum1, typeDatabase);
        var (_, swiftOutput2) = EmitEnum(enum2, typeDatabase);

        // Verify distinct wrapper symbols
        Assert.Contains("SBW_TestModule_Container1_ErrorType_InitWithRawValue", swiftOutput1);
        Assert.Contains("SBW_TestModule_Container2_ErrorType_InitWithRawValue", swiftOutput2);

        // Verify they are different
        Assert.DoesNotContain("SBW_TestModule_Container2_ErrorType_InitWithRawValue", swiftOutput1);
        Assert.DoesNotContain("SBW_TestModule_Container1_ErrorType_InitWithRawValue", swiftOutput2);
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10{moduleDecl.Name}{name.Length}{name}VMa"
        };
    }

    private static EnumDecl CreateNestedEnumDecl(string name, StructDecl container, ModuleDecl moduleDecl, bool isFrozen)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{container.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{container.Name.Length}{container.Name}{name.Length}{name}ON",
            Cases = new List<EnumCaseDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = container,
            ModuleDecl = moduleDecl,
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10{moduleDecl.Name}{container.Name.Length}{container.Name}{name.Length}{name}OMa"
        };
    }

    private static MethodDecl CreateStringRawValueInitializer(EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = $"$s{enumDecl.MangledName}8rawValueACSgSS_tcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec(enumDecl.SwiftTypeName.ModuleQualifiedName),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
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

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string protocolModule, string protocolName)
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

        // Create the test module with the protocol registration
        var testModuleDb = new ModuleTypeDatabase(protocolModule, $"/tmp/{protocolModule}.dylib");
        testModuleDb.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{protocolModule}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{protocolModule}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{protocolModule}.{protocolName}"),
                MetadataAccessor = $"$s{protocolModule}{protocolName}Ma",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModuleDb);

        return typeDatabase;
    }

    [Fact]
    public void Emit_ExistentialWithKnownProxy_FactoryUsesInterfaceType()
    {
        // Register ImageProcessing as a known protocol
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "ImageProcessing");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ImageError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("processingFailed");
        // Single-protocol existential associated value
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageProcessing") }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory signature should use interface type
        Assert.Contains("IImageProcessing", csOutput);
        Assert.Contains("public static unsafe ImageError ProcessingFailed(IImageProcessing", csOutput);
        // Body should extract container via ISwiftExistentialConvertible
        Assert.Contains("ISwiftExistentialConvertible", csOutput);
        Assert.Contains("GetExistentialContainer()", csOutput);
    }

    [Fact]
    public void Emit_ExistentialWithoutProxy_KeepsExistentialContainer()
    {
        // Swift.Error has no TypeRecord → should stay as ExistentialContainer1
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LoadError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should keep ExistentialContainer1 (no proxy)
        Assert.Contains("ExistentialContainer1", csOutput);
        // Should NOT contain interface type
        Assert.DoesNotContain("IError", csOutput);
        Assert.DoesNotContain("ISwiftExistentialConvertible", csOutput);
    }

    [Fact]
    public void Emit_TryGetWithExistentialProxy_WrapsInProxy()
    {
        // Register protocol so we get interface types
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "ImageDecoding");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("DecodeResult", moduleDecl, isFrozen: true);

        var decodedCase = CreateCase("decoded");
        decodedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageDecoding") }));
        enumDecl.Cases.Add(decodedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // TryGet out parameter should use interface type
        Assert.Contains("out IImageDecoding value", csOutput);
        // Body should marshal to temp container then wrap in proxy
        Assert.Contains("_value_raw", csOutput);
        Assert.Contains("new ImageDecodingProxy(", csOutput);
    }

    [Fact]
    public void Emit_TupleTryGetWithMixedExistentials_MixesInterfaceAndContainer()
    {
        // One known protocol, one unknown protocol in a tuple
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "ImageProcessing");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("PipelineError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        // Tuple: (known protocol, unknown protocol)
        failedCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageProcessing") }),
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") })
        }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Known protocol → interface type in out param
        Assert.Contains("out IImageProcessing value0", csOutput);
        // Unknown protocol → ExistentialContainer1 in out param
        Assert.Contains("out Swift.Runtime.ExistentialContainer1 value1", csOutput);
        // Proxy wrapping for known protocol
        Assert.Contains("new ImageProcessingProxy(", csOutput);
    }

    [Fact]
    public void Emit_MultiProtocolCompositionWithUnregistered_KeepsExistentialContainer()
    {
        // 2-protocol composition but only one protocol is registered
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "ImageProcessing");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("MixedError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        // Composition: ImageProcessing & UnknownProtocol (2 protocols)
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.ImageProcessing"),
            new NamedTypeSpec("TestModule.UnknownProtocol")
        }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should keep ExistentialContainer2 (one protocol unregistered → can't build proxy)
        Assert.Contains("ExistentialContainer2", csOutput);
        // Should NOT contain a composition interface name
        Assert.DoesNotContain("IImageProcessingAndUnknownProtocol", csOutput);
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
