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
    public void Emit_StringRawRepresentableEnum_EmitsCSharpEnum()
    {
        // Frozen String-raw-value enums with no methods/properties qualify as simple enums
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should be a C# enum with ToRawValue/FromRawValue extensions
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("FromRawValue", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.DoesNotContain("public partial class LogLevel", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenSimpleEnum_EmitsCSharpEnum()
    {
        // Step 3b: non-frozen no-payload enums are now emitted as C# enum value types
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("Active = 0,", csOutput);
        Assert.Contains("Inactive = 1,", csOutput);
        Assert.DoesNotContain("class Status", csOutput);
        Assert.DoesNotContain("SwiftSafeHandle", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnumWithAssociatedValues_EmitsClass()
    {
        // Non-frozen enums WITH associated values stay class-based
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Result", moduleDecl, isFrozen: false);
        var successCase = CreateCase("success");
        successCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(successCase);
        enumDecl.Cases.Add(CreateCase("failure"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("class Result", csOutput);
        Assert.DoesNotContain("public enum Result", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnumWithInstanceProperties_EmitsSimpleEnumWithExtension()
    {
        // BX2: Non-frozen no-payload enum with instance property → simple enum + extension getter
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("priority", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("StatusExtensions", csOutput);
        Assert.Contains("GetPriority(this Status self)", csOutput);
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnumWithInstanceProperty_EmitsDangerousAddRefRelease()
    {
        // B18 gate lift: non-simple enum instance property accessors must emit
        // DangerousAddRef/DangerousRelease to pin the _payload SafeHandle during P/Invoke.
        // The Tag property also emits DangerousAddRef, so we verify the property accessor
        // path specifically by checking for 2+ occurrences (Tag + Area) and for SwiftSelf
        // which only appears in the WrapperEmitter.Marshalling property accessor path.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Shape", moduleDecl, isFrozen: false);
        var circleCase = CreateCase("circle");
        circleCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Double"));
        enumDecl.Cases.Add(circleCase);
        enumDecl.Cases.Add(CreateCase("empty"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("area", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("class Shape", csOutput);
        // Tag property emits one DangerousAddRef; the instance property accessor emits another.
        // Count occurrences to ensure BOTH paths emit (not just Tag).
        var addRefCount = csOutput.Split("DangerousAddRef").Length - 1;
        Assert.True(addRefCount >= 2, $"Expected at least 2 DangerousAddRef calls (Tag + Area), got {addRefCount}");
        var releaseCount = csOutput.Split("DangerousRelease").Length - 1;
        Assert.True(releaseCount >= 2, $"Expected at least 2 DangerousRelease calls (Tag + Area), got {releaseCount}");
        // SwiftSelf is unique to the property accessor path (WrapperEmitter.Marshalling),
        // not emitted by the Tag property (EnumHandler.CaseInspection).
        Assert.Contains("SwiftSelf", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnumWithStaticMethods_EmitsSimpleEnumWithStaticMethod()
    {
        // BX2: Non-frozen no-payload enum with static method → simple enum + static method in extensions
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStaticMethod("defaultStatus", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("StatusExtensions", csOutput);
        Assert.Contains("DefaultStatus()", csOutput);
        Assert.Contains("(Status)PInvoke_DefaultStatus()", csOutput);
        Assert.DoesNotContain(": ISwiftObject", csOutput);
        // Swift wrapper should call the static method on the qualified type
        Assert.Contains(".defaultStatus()", swiftOutput);
    }

    [Fact]
    public void Emit_NonFrozenEnumWithStaticProperty_EmitsSimpleEnumWithStaticProperty()
    {
        // BX2: Non-frozen no-payload enum with static property → simple enum + static property in extensions
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Properties.Add(CreateStaticIntProperty("count", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("StatusExtensions", csOutput);
        Assert.Contains("Count =>", csOutput);
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_NoMembers_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithInstanceProperty_ReturnsTrue()
    {
        // BX2: Properties are emitted as extension getters or skipped — they don't block the gate
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("degrees", enumDecl, moduleDecl));

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithStaticProperty_ReturnsTrue()
    {
        // BX2: Static properties are emitted in extensions class or skipped — they don't block the gate
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Properties.Add(CreateStaticIntProperty("count", enumDecl, moduleDecl));

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithStaticMethod_ReturnsTrue()
    {
        // BX2: Static methods are emitted in extensions class or skipped — they don't block the gate
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Methods.Add(CreateStaticMethod("defaultDirection", enumDecl, moduleDecl));

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithNonEqualityOperator_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Operators.Add(new OperatorDecl
        {
            Name = "<",
            OperatorSymbol = "<",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = new MethodDecl
            {
                Name = "<",
                MangledName = "$s10TestModule9DirectionO1loiySbAC_ACtFZ",
                MethodType = MethodType.Static,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = enumDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Public
            },
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        Assert.False(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
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
        Assert.Contains("public static partial class DirectionExtensions", csOutput);
        Assert.Contains("public static Direction Opposite(this Direction self)", csOutput);
        Assert.Contains("(Direction)PInvoke_Opposite((int)self)", csOutput);
        // Simple enum P/Invoke uses its own emission path (LibraryImport, not PInvokeEmitter)
        Assert.Contains("[LibraryImport(", csOutput);

        // Swift wrapper should have tag-to-case conversion
        Assert.Contains("switch tag {", swiftOutput);
        Assert.Contains("case 0: value = .north", swiftOutput);
        Assert.Contains("case 1: value = .south", swiftOutput);
        Assert.Contains("value.opposite()", swiftOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithSynthesizedHashMethod_EmitsAsSimpleEnum()
    {
        // Synthesized hash(into:) is filtered out by CanSafelyEmitAsSimpleEnum —
        // C# enums inherit GetHashCode() natively. The enum should NOT fall to class path.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Color", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("red"));
        enumDecl.Cases.Add(CreateCase("blue"));
        // Conformance-aware: hash(into:) is only synthesized if enum conforms to Hashable
        enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            ""));

        // Add the synthesized hash(into:) method — same shape as Swift compiler generates
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

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Synthesized hash(into:) is excluded — emits as simple C# enum
        Assert.Contains("public enum Color", csOutput);
        Assert.DoesNotContain("class Color", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithSynthesizedProperties_EmitsAsSimpleEnum()
    {
        // Synthesized Hashable/RawRepresentable properties (hashValue, rawValue) should
        // NOT block simple enum emission — C# enums handle these natively.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Priority", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("low"));
        enumDecl.Cases.Add(CreateCase("high"));
        // Conformance-aware: properties only excluded if enum conforms to their source protocol
        enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            ""));
        enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.RawRepresentable"),
            ""));

        // Add synthesized properties: hashValue (Hashable) and rawValue (RawRepresentable)
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "hashValue",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = false,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "rawValue",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = false,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Synthesized properties excluded — emits as C# enum, not class
        Assert.Contains("public enum Priority", csOutput);
        Assert.DoesNotContain("class Priority", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithNonSynthesizedProperty_EmitsSimpleEnumSkipsProperty()
    {
        // BX2: A real (non-synthesized) property like "description" no longer forces class-based emission.
        // The property is skipped (no getter accessor) and the enum stays simple.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        // Add a real property with no accessor (will be skipped)
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "description",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: enum still emits as simple (property skipped, not blocking)
        Assert.Contains("public enum Status", csOutput);
        Assert.DoesNotContain("class Status", csOutput);
    }

    [Fact]
    public void Emit_RawValuePropertyWithoutConformance_EmitsSimpleEnumSkipsProperty()
    {
        // BX2: If an enum has a "rawValue" property but does NOT conform to RawRepresentable,
        // the property is user-defined, not synthesized — but it no longer blocks simple path.
        // The property is skipped (no getter accessor) and the enum stays simple.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Flavor", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("sweet"));
        enumDecl.Cases.Add(CreateCase("sour"));
        // NO RawRepresentable conformance added — rawValue is user-defined
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "rawValue",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = false,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: enum still emits as simple (property skipped)
        Assert.Contains("public enum Flavor", csOutput);
        Assert.DoesNotContain("class Flavor", csOutput);
    }

    [Fact]
    public void Emit_ModuleInternalMethodDoesNotBlockSimpleEnum()
    {
        // Module-internal methods (@usableFromInline internal) appear in ABI JSON but
        // cannot be called from external Swift wrappers. They should not block simple
        // enum emission or be emitted as extension methods.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Bit", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("zero"));
        enumDecl.Cases.Add(CreateCase("one"));

        // Add an internal method with unsupported param type — would block simple path
        // if not filtered by IsModuleInternal
        var internalMethod = new MethodDecl
        {
            Name = "inverted",
            MangledName = "$s10TestModule3BitO8invertedACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsModuleInternal = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Bit"),
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
        enumDecl.Methods.Add(internalMethod);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Internal method excluded — emits as simple C# enum
        Assert.Contains("public enum Bit", csOutput);
        Assert.DoesNotContain("class Bit", csOutput);
        // Internal method should NOT appear as extension method
        Assert.DoesNotContain("Inverted", csOutput);
    }

    [Fact]
    public void Emit_ModuleInternalStaticMethodDoesNotBlockSimpleEnum()
    {
        // Module-internal static methods should not block simple enum emission,
        // just like module-internal instance methods.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Flag", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("on"));
        enumDecl.Cases.Add(CreateCase("off"));

        var internalStaticMethod = new MethodDecl
        {
            Name = "makeDefault",
            MangledName = "$s10TestModule4FlagO11makeDefaultACyFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            IsModuleInternal = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Flag"),
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
        enumDecl.Methods.Add(internalStaticMethod);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Internal static method excluded — emits as simple C# enum
        Assert.Contains("public enum Flag", csOutput);
        Assert.DoesNotContain("class Flag", csOutput);
        Assert.DoesNotContain("MakeDefault", csOutput);
    }

    [Fact]
    public void Emit_HashIntoWithoutHashableConformance_EmitsSimpleEnumSkipsMethod()
    {
        // BX2: If an enum has a hash(into:) method but does NOT conform to Hashable,
        // the method is user-defined, not synthesized — but it no longer blocks simple path.
        // The incompatible method (Hasher param) is skipped; enum stays simple.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Token", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("a"));
        enumDecl.Cases.Add(CreateCase("b"));
        // NO Hashable conformance added

        var hashMethod = new MethodDecl
        {
            Name = "hash",
            MangledName = "$s10TestModule5TokenO4hashySHzF",
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

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: Incompatible method is skipped, enum still simple
        Assert.Contains("public enum Token", csOutput);
        Assert.DoesNotContain("class Token", csOutput);
        // hash(into:) should NOT be emitted (Hasher param unsupported)
        Assert.DoesNotContain("Hash", csOutput);
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

        Assert.Contains("public bool TryGetPair([MaybeNullWhen(false)] out long value0, [MaybeNullWhen(false)] out bool value1)", csOutput);
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
        Assert.Contains("public static long Active", csOutput);
        Assert.DoesNotContain("public static long Paused", csOutput);
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

        Assert.Contains("public partial class ValueProviderStorage<T> : ISwiftObject, IDisposable where T : ISwiftObject", csOutput);
        Assert.Contains("public static unsafe ValueProviderStorage<T> Boxed(T value0)", csOutput);
        Assert.Contains("var value0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();", csOutput);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value0, ref value0SwiftSpan);", csOutput);
        Assert.Contains("ValueProviderStorage_PInvoke.PInvoke_Boxed(indirectResult, (IntPtr)value0SwiftBuffer", csOutput);
        Assert.Contains("internal static partial class ValueProviderStorage_PInvoke", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_EmitsStaticClass()
    {
        // E12: Zero-case enums used as namespaces should emit as static classes
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ImageProcessors", moduleDecl, isFrozen: true);
        // No cases — this is a namespace-like enum

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static partial class ImageProcessors", csOutput);
        // Should NOT contain ISwiftObject, IDisposable, SafeHandle, Payload
        Assert.DoesNotContain("ISwiftObject", csOutput);
        Assert.DoesNotContain("IDisposable", csOutput);
        Assert.DoesNotContain("SwiftSafeHandle", csOutput);
        Assert.DoesNotContain("_payload", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_WithStaticProperties_EmitsPropertiesInStaticClass()
    {
        // Codex P1: Caseless enums with static members must emit those members, not just nested types.
        // Real example: PhoneNumberKit.CountryCodePicker has commonCountryCodes, forceModalPresentation, etc.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Constants", moduleDecl, isFrozen: true);
        // No cases — caseless enum
        enumDecl.Properties.Add(CreateStaticIntProperty("defaultTimeout", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static partial class Constants", csOutput);
        // The property should be emitted (not dropped)
        Assert.Contains("DefaultTimeout", csOutput);
        // Should NOT contain ISwiftObject/IDisposable boilerplate
        Assert.DoesNotContain("ISwiftObject", csOutput);
        Assert.DoesNotContain("IDisposable", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_WithStaticMethods_PlumbsMethodsThroughConductor()
    {
        // Caseless enums with static methods route them through HandleBaseDecl.
        // Methods may silently fail resolution in test contexts (no full TypeDatabase),
        // but the path must not crash and the static class must still be correct.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Utils", moduleDecl, isFrozen: true);
        // No cases — caseless enum with a static method
        enumDecl.Methods.Add(CreateVoidStaticMethod("reset", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static partial class Utils", csOutput);
        Assert.DoesNotContain("ISwiftObject", csOutput);
        Assert.DoesNotContain("IDisposable", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_SkipsInstanceMembers()
    {
        // Instance members are invalid in a C# static class. While rare in practice
        // (swiftc allows them but no real library uses them), they must be filtered out.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Config", moduleDecl, isFrozen: true);
        // No cases — caseless enum
        enumDecl.Properties.Add(CreateStaticIntProperty("maxRetries", enumDecl, moduleDecl));
        enumDecl.Properties.Add(CreateInstanceIntProperty("instanceProp", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static partial class Config", csOutput);
        Assert.Contains("MaxRetries", csOutput);
        // Instance property must NOT appear in the static class
        Assert.DoesNotContain("InstanceProp", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_PreservesGenericParameters()
    {
        // Generic caseless enums must preserve type parameters and where clauses.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Wrapper", moduleDecl, isFrozen: true);
        enumDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new(), new())
        };

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public static partial class Wrapper<T>", csOutput);
        // ISwiftObject appears in the where clause (constraint on T), not as an interface on the class
        Assert.DoesNotContain("IDisposable", csOutput);
        Assert.DoesNotContain("SwiftSafeHandle", csOutput);
    }

    [Fact]
    public void Emit_NamespaceEnum_DoesNotEmitForNonZeroCaseEnum()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("north"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Single-case enum should NOT be emitted as static class
        Assert.DoesNotContain("static partial class Direction", csOutput);
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
        => CreateTypedRawValueInitializer(enumDecl, moduleDecl, "Swift.Int");

    private static MethodDecl CreateTypedRawValueInitializer(EnumDecl enumDecl, ModuleDecl moduleDecl, string swiftRawType)
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
                    SwiftTypeSpec = new NamedTypeSpec(swiftRawType),
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

    private static PropertyDecl CreateInstanceIntProperty(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name}Sivg",
                        MethodType = MethodType.Instance,
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

    private static MethodDecl CreateStaticMethod(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name}yACyFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
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

    private static MethodDecl CreateVoidStaticMethod(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name.Length}{name}yyFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Void"),
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

        // Context-based tracking: tests use default context (no parallelism)
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
        // Use non-frozen to keep class-based emission (frozen String enums are now C# enums).
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create first nested enum: TestModule.Container1.ErrorType (non-frozen → class emission)
        var container1 = CreateStructDecl("Container1", moduleDecl);
        var enum1 = CreateNestedEnumDecl("ErrorType", container1, moduleDecl, isFrozen: false);
        enum1.RawValueTypeName = "String";
        enum1.Cases.Add(CreateCase("unknown"));
        enum1.Methods.Add(CreateStringRawValueInitializer(enum1, moduleDecl));
        container1.Types.Add(enum1);

        // Create second nested enum: TestModule.Container2.ErrorType (non-frozen → class emission)
        var container2 = CreateStructDecl("Container2", moduleDecl);
        var enum2 = CreateNestedEnumDecl("ErrorType", container2, moduleDecl, isFrozen: false);
        enum2.RawValueTypeName = "String";
        enum2.Cases.Add(CreateCase("failed"));
        enum2.Methods.Add(CreateStringRawValueInitializer(enum2, moduleDecl));
        container2.Types.Add(enum2);

        // Reset shared state and emit both enums
        // Context-based tracking: tests use default context (no parallelism)
        var (_, swiftOutput1) = EmitEnum(enum1, typeDatabase);
        var (_, swiftOutput2) = EmitEnum(enum2, typeDatabase);

        // Verify distinct wrapper symbols
        Assert.Contains("SBW_TestModule_Container1_ErrorType_InitWithRawValue", swiftOutput1);
        Assert.Contains("SBW_TestModule_Container2_ErrorType_InitWithRawValue", swiftOutput2);

        // Verify they are different
        Assert.DoesNotContain("SBW_TestModule_Container2_ErrorType_InitWithRawValue", swiftOutput1);
        Assert.DoesNotContain("SBW_TestModule_Container1_ErrorType_InitWithRawValue", swiftOutput2);
    }

    [Fact]
    public void Emit_FrozenStringEnum_EmitsCSharpEnumNotClass()
    {
        // Frozen String-raw-value enums with only constructors are now emitted as C# enums
        var typeDatabase = CreateTypeDatabaseWithString();
        typeDatabase.AsyncLibraryName = "BlinkIDSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ErrorCode", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should be a C# enum with no wrapper P/Invokes
        Assert.Contains("public enum ErrorCode", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.DoesNotContain("[LibraryImport(", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStringEnum_DllImportUsesAsyncLibraryName()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        typeDatabase.AsyncLibraryName = "BlinkIDSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ErrorCode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // The wrapper P/Invoke (InitWithRawValue) should use AsyncLibraryName
        Assert.Contains("[LibraryImport(\"BlinkIDSwiftBindings\", EntryPoint = \"SBW_TestModule_ErrorCode_InitWithRawValue\"", csOutput);
        Assert.DoesNotContain("[LibraryImport(\"SwiftBindings\"", csOutput);
    }

    [Fact]
    public void Emit_FrozenStringEnum_WithRawValueConversions()
    {
        // Frozen String-raw-value enums emit as C# enums with raw value conversion extensions
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ErrorCode", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Cases.Add(CreateCase("timeout"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should emit C# enum with conversion extensions
        Assert.Contains("public enum ErrorCode", csOutput);
        Assert.Contains("Unknown = 0,", csOutput);
        Assert.Contains("Timeout = 1,", csOutput);
        Assert.Contains("ErrorCodeExtensions", csOutput);
        Assert.Contains("\"unknown\" => ErrorCode.Unknown,", csOutput);
        Assert.Contains("\"timeout\" => ErrorCode.Timeout,", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenStringEnum_DllImportFallsBackToModuleLibrary()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        // No AsyncLibraryName set — should fall back to module library path
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ErrorCode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Without AsyncLibraryName, wrapper P/Invoke falls back to module library path
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\", EntryPoint = \"SBW_TestModule_ErrorCode_InitWithRawValue\"", csOutput);
        Assert.DoesNotContain("[LibraryImport(\"SwiftBindings\"", csOutput);
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

    [Fact]
    public void Emit_ComplexEnum_EmitsFinalizer()
    {
        // Complex enum with associated values that need memory management → finalizer
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Result", moduleDecl, isFrozen: false);
        var successCase = CreateCase("success");
        successCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(successCase);
        enumDecl.Cases.Add(CreateCase("failure"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Non-frozen complex enum → class emission with finalizer
        Assert.Contains("~Result()", csOutput);
    }

    [Fact]
    public void Emit_ComplexEnum_EmitsGCSuppressFinalize()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Result", moduleDecl, isFrozen: false);
        var successCase = CreateCase("success");
        successCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(successCase);
        enumDecl.Cases.Add(CreateCase("failure"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Dispose should suppress finalizer
        Assert.Contains("GC.SuppressFinalize", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnum_EmitsToRawValueMethod()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Level", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("low"));
        enumDecl.Cases.Add(CreateCase("high"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Must emit ToRawValue extension method
        Assert.Contains("public static string ToRawValue(this Level value)", csOutput);
        Assert.Contains("\"low\"", csOutput);
        Assert.Contains("\"high\"", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnum_EmitsFromRawValueMethod()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Level", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("low"));
        enumDecl.Cases.Add(CreateCase("high"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Must emit FromRawValue extension method
        Assert.Contains("public static Level? FromRawValue(", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnumWithInstanceMethods_EmitsCSharpEnumWithExtensions()
    {
        // Step 3a: frozen String-raw-value enums with instance methods should be C# enums
        // Instance methods are emitted as extension methods on the C# enum.
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Add an instance method (should NOT prevent simple enum emission)
        var describeMethod = new MethodDecl
        {
            Name = "describe",
            MangledName = "$s10TestModule8LogLevelO8describeSSyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
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
        enumDecl.Methods.Add(describeMethod);

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should emit C# enum (not class)
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.DoesNotContain("public partial class LogLevel", csOutput);
        // Should emit ToRawValue/FromRawValue extensions
        Assert.Contains("ToRawValue", csOutput);
        Assert.Contains("FromRawValue", csOutput);
        // Should emit instance method as extension method
        Assert.Contains("public static partial class LogLevelExtensions", csOutput);
        // Swift wrapper should be emitted for the instance method
        Assert.Contains("_sbw_LogLevel_describe", swiftOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnumWithStaticMethods_EmitsSimpleEnum()
    {
        // BX2: frozen String-raw-value enums with static methods → simple enum + extensions
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Add a static method (BX2: no longer blocks simple enum emission)
        var factoryMethod = new MethodDecl
        {
            Name = "defaultLevel",
            MangledName = "$s10TestModule8LogLevelO07defaultD0ACyFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.LogLevel"),
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
        enumDecl.Methods.Add(factoryMethod);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: emits as C# enum with static method in extensions
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("DefaultLevel()", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnumWithProperties_EmitsSimpleEnumWithExtension()
    {
        // BX2: frozen String-raw-value enums with properties → simple enum + extension getters
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Add a property (BX2: no longer blocks simple enum emission)
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "priority",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "priority_Get",
                        MangledName = "$s10TestModule8LogLevelO8prioritySivg",
                        MethodType = MethodType.Instance,
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
        });

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: Should be simple enum with extension getter for property
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("LogLevelExtensions", csOutput);
        Assert.Contains("GetPriority", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnumWithComplexInstanceMethod_EmitsSimpleEnumSkipsMethod()
    {
        // BX2: frozen String-raw-value enums with instance methods that have unsupported
        // parameter types → emit as simple enum, skip the incompatible method
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Add a genuinely complex instance method (non-synthesized, unsupported param type)
        var describeMethod = new MethodDecl
        {
            Name = "describe",
            MangledName = "$s10TestModule8LogLevelO8describeyAA7OptionsVF",
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
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Options"),
                    Name = "options",
                    PrivateName = "options",
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
        enumDecl.Methods.Add(describeMethod);

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: Simple enum emitted, incompatible method skipped
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.DoesNotContain("Describe", csOutput);  // complex method skipped
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnumWithComplexReturnType_EmitsSimpleEnumSkipsMethod()
    {
        // BX2: instance method with unsupported return type → simple enum, method skipped
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Add an instance method returning a complex type (Array — not primitive/string/bool/void/self)
        var toArrayMethod = new MethodDecl
        {
            Name = "components",
            MangledName = "$s10TestModule8LogLevelO10componentsSaySSGyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array"),
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
        enumDecl.Methods.Add(toArrayMethod);

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: Simple enum emitted, incompatible method skipped
        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.DoesNotContain("Components", csOutput);  // complex return skipped
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    private static TypeDatabase CreateTypeDatabaseWithNonFrozenStruct()
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

        var nukeModule = new ModuleTypeDatabase("Nuke", "/tmp/Nuke.dylib");
        nukeModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Nuke.ImageResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Nuke", "ImageResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageResponse"),
                MetadataAccessor = "$s4Nuke13ImageResponseVMa",
                Flags = TypeRecordFlags.None, // NOT frozen — ClassWithOpaquePayload
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(nukeModule);
        return typeDatabase;
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
    public void Emit_ExistentialWithoutProxy_UsesAnyError()
    {
        // Swift.Error is a well-known protocol → maps to Swift.AnyError (not ExistentialContainer1)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LoadError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should use AnyError (well-known runtime type, no proxy)
        Assert.Contains("Swift.AnyError", csOutput);
        // Should NOT contain raw ExistentialContainer or interface type
        Assert.DoesNotContain("IError", csOutput);
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
    public void Emit_TupleTryGetWithMixedExistentials_MixesInterfaceAndAnyError()
    {
        // One known protocol, one well-known stdlib protocol in a tuple
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "ImageProcessing");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("PipelineError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        // Tuple: (known protocol, Swift.Error)
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
        // Swift.Error → AnyError in out param (well-known runtime type)
        Assert.Contains("out Swift.AnyError value1", csOutput);
        // Proxy wrapping for known protocol
        Assert.Contains("new ImageProcessingProxy(", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithNonFrozenStructParam_UsesIntPtrAndPayloadExtract()
    {
        // Non-frozen struct as enum case associated value → P/Invoke uses IntPtr + .Payload.DangerousGetHandle()
        var typeDatabase = CreateTypeDatabaseWithNonFrozenStruct();
        var moduleDecl = CreateModuleDecl("Nuke");
        var enumDecl = CreateEnumDecl("ImageError", moduleDecl, isFrozen: false);
        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new NamedTypeSpec("Nuke.ImageResponse"));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // P/Invoke parameter should use IntPtr (not the C# class name)
        Assert.Contains("IntPtr imageResponse)", csOutput);
        // Call site should extract the SafeHandle payload
        Assert.Contains(".Payload.DangerousGetHandle()", csOutput);
        // P/Invoke declaration should use IntPtr, not ImageResponse
        Assert.Contains("PInvoke_Failed(SwiftIndirectResult", csOutput);
        Assert.DoesNotContain("PInvoke_Failed(SwiftIndirectResult result, Nuke.ImageResponse", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithTupleContainingAnyType_UsesIntPtrAndPayloadExtractForUnknownElement()
    {
        // Tuple associated value where one element is unknown → resolves to AnyType (Kind=Protocol).
        // GetPInvokeArgument recurses into each tuple element; the unknown element hits the AnyType
        // branch and emits .Payload.DangerousGetHandle(). This is the exact Lottie (nint, AnyType) scenario.
        var typeDatabase = CreateTypeDatabase();
        var lottieModule = new ModuleTypeDatabase("Lottie", "/tmp/Lottie.dylib");
        typeDatabase.AddModuleDatabase(lottieModule);

        var moduleDecl = CreateModuleDecl("Lottie");
        var enumDecl = CreateEnumDecl("AnimationResult", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        // Tuple: (Int, UnknownType) — Int is registered, UnknownType resolves to AnyType
        dataCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Lottie.UnknownType")
        }));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // P/Invoke signature: unknown element becomes IntPtr in the ValueTuple
        Assert.Contains("ValueTuple<long, IntPtr> value0)", csOutput);
        // Call site: known element passes directly, unknown element extracts SafeHandle payload
        Assert.Contains("value0.Item2.Payload.DangerousGetHandle()", csOutput);
        // The AnyType class name should NOT appear in the P/Invoke ValueTuple
        Assert.DoesNotContain("ValueTuple<long, Swift.AnyType>", csOutput);
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

    [Fact]
    public void Emit_TupleWithSwiftStringAndExistential_ProjectsBothElements()
    {
        // Tuple: (SwiftString, known protocol) — both elements should be projected
        // to idiomatic C# types in the public API (SwiftString → string, existential → interface).
        var typeDatabase = CreateTypeDatabaseWithStringAndProtocol("TestModule", "ImageProcessing");
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("FilterError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("invalidFilter");
        // Tuple: (String, known protocol existential)
        failedCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageProcessing") })
        }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory signature: tuple elements projected — SwiftString → string, existential → interface
        Assert.Contains("(string, IImageProcessing) value0)", csOutput);
    }

    [Fact]
    public void Emit_StandaloneSwiftStringEnumCase_UsesStringInPublicSignature()
    {
        // Standalone SwiftString (not in a tuple) → public API should use "string"
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Message", moduleDecl, isFrozen: true);

        var textCase = CreateCase("text");
        textCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        enumDecl.Cases.Add(textCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory: public signature uses "string"
        Assert.Contains("public static unsafe Message Text(string value0)", csOutput);
        // Body: converts string → SwiftString for P/Invoke
        Assert.Contains("using var __value0 = new SwiftString(value0);", csOutput);
        // TryGet: out parameter uses "string"
        Assert.Contains("out string value", csOutput);
        // TryGet body: converts SwiftString → string via .ToString()
        Assert.Contains(".ToString()", csOutput);
    }

    [Fact]
    public void Emit_ImmutableRawRepresentableEnum_EmitsLazyCachedCaseProperties()
    {
        // Step 3c: immutable class-based enums should cache case properties via Lazy<T>
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("StatusCode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should emit Lazy<T> backing field for each case
        Assert.Contains("private static readonly Lazy<StatusCode> _lazy_active", csOutput);
        Assert.Contains("private static readonly Lazy<StatusCode> _lazy_inactive", csOutput);
        // Should set _isCachedSingleton
        Assert.Contains("_isCachedSingleton = true", csOutput);
        // Should emit arrow property
        Assert.Contains("public static StatusCode Active => _lazy_active.Value;", csOutput);
        Assert.Contains("public static StatusCode Inactive => _lazy_inactive.Value;", csOutput);
    }

    [Fact]
    public void Emit_ImmutableTagBasedEnum_EmitsLazyCachedCaseProperties()
    {
        // Step 3c: immutable non-RawRepresentable class-based enums should also cache.
        // Uses a payload case to keep it class-based (step 3b makes bare no-payload enums C# enums).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        dataCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should emit Lazy<T> backing field for each no-payload case
        Assert.Contains("private static readonly Lazy<Status> _lazy_active", csOutput);
        Assert.Contains("private static readonly Lazy<Status> _lazy_inactive", csOutput);
        // Should set _isCachedSingleton
        Assert.Contains("_isCachedSingleton = true", csOutput);
        // Should emit arrow property
        Assert.Contains("public static Status Active => _lazy_active.Value;", csOutput);
    }

    [Fact]
    public void Emit_EnumWithMutatingMethod_DoesNotCacheCases()
    {
        // Step 3c: enums with mutating methods must NOT cache (mutation poisoning).
        // Uses a payload case to keep it class-based (step 3b makes bare no-payload enums C# enums).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        dataCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        // Add a mutating method
        var mutateMethod = new MethodDecl
        {
            Name = "toggle",
            MangledName = "$s10TestModule6StatusO6toggleyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsMutating = true,
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
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        enumDecl.Methods.Add(mutateMethod);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should NOT emit Lazy<T> (per-access construction preserved)
        Assert.DoesNotContain("Lazy<Status>", csOutput);
        Assert.DoesNotContain("_isCachedSingleton = true", csOutput);
        // Should still emit case properties (just not cached)
        Assert.Contains("public static Status Active", csOutput);
    }

    [Fact]
    public void Emit_EnumWithWritableProperty_DoesNotCacheCases()
    {
        // Step 3c: enums with writable instance properties must NOT cache (mutation poisoning)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        // Add a writable instance property
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "label_Get",
                        MangledName = "$s10TestModule6StatusO5labelSivg",
                        MethodType = MethodType.Instance,
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
                },
                new SetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "label_Set",
                        MangledName = "$s10TestModule6StatusO5labelSivs",
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
                                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                                Name = "newValue",
                                PrivateName = "newValue",
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
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // BX2: no-payload enum with writable property → simple enum (not class-based)
        Assert.Contains("public enum Status : int", csOutput);
        // Getter emitted as extension, setter skipped
        Assert.Contains("GetLabel(this Status self)", csOutput);
        Assert.DoesNotContain("SetLabel", csOutput);
    }

    [Fact]
    public void Emit_ClassBasedEnum_EmitsDisposalGuard()
    {
        // Step 3c: all class-based enums should emit _isCachedSingleton field and disposal guard.
        // Uses a payload case to keep it class-based (step 3b makes bare no-payload enums C# enums).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        dataCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("active"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should emit the _isCachedSingleton field (with pragma to suppress CS0649 when not cached)
        Assert.Contains("internal bool _isCachedSingleton;", csOutput);
        Assert.Contains("#pragma warning disable CS0649", csOutput);
        // Dispose should check _isCachedSingleton
        Assert.Contains("if (_isCachedSingleton) return;", csOutput);
        // Finalizer should check _isCachedSingleton
        Assert.Contains("if (!_isCachedSingleton)", csOutput);
    }

    private static TypeDatabase CreateTypeDatabaseWithStringAndProtocol(string protocolModule, string protocolName)
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

    // --- Simple enum in tuple metadata: uses GetTypeMetadataOrThrow, NOT SwiftObjectHelper ---

    [Fact]
    public void Emit_TupleWithSimpleEnumElement_EmitsGetTypeMetadataOrThrow_NotSwiftObjectHelper()
    {
        // Reproduces the Alamofire CS0315 bug: NSUrlSessionWebSocketCloseCode (simple enum)
        // used in a tuple associated value. The emitter must use GetTypeMetadataOrThrow<nint>()
        // (matching the Swift ABI backing type), not SwiftObjectHelper<T> (requires ISwiftObject).
        var typeDatabase = CreateTypeDatabase();
        var foundationModule = new ModuleTypeDatabase("Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URLSessionWebSocketTask.CloseCode"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrlSessionWebSocketCloseCode"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URLSessionWebSocketTask.CloseCode"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int"
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("WebSocketEvent", moduleDecl, isFrozen: false);
        var disconnectedCase = CreateCase("disconnected");
        // Tuple: (CloseCode, Int) — CloseCode is a simple enum
        disconnectedCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Foundation.URLSessionWebSocketTask.CloseCode"),
            new NamedTypeSpec("Swift.Int")
        }));
        enumDecl.Cases.Add(disconnectedCase);
        enumDecl.Cases.Add(CreateCase("connected"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Simple enum element uses GetTypeMetadataOrThrow with the Swift ABI backing type
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow<nint>()", csOutput);
        // Must NOT use SwiftObjectHelper (requires ISwiftObject — invalid for .NET enums)
        Assert.DoesNotContain("SwiftObjectHelper<Foundation.NSUrlSessionWebSocketCloseCode>", csOutput);
    }

    [Fact]
    public void Emit_TupleWithUInt8SimpleEnum_EmitsGetTypeMetadataOrThrow_Byte()
    {
        // Verifies that a simple enum with UInt8 backing type uses byte metadata, not nint.
        var typeDatabase = CreateTypeDatabase();
        var testModule2 = new ModuleTypeDatabase("OtherModule", "/tmp/OtherModule.dylib");
        testModule2.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OtherModule.SmallEnum"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OtherModule", "SmallEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.SmallEnum"),
                MetadataAccessor = "$s11OtherModule9SmallEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "UInt8"
            });
        typeDatabase.AddModuleDatabase(testModule2);

        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Container", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        dataCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("OtherModule.SmallEnum"),
            new NamedTypeSpec("Swift.Int")
        }));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("empty"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // UInt8-backed simple enum should use byte metadata, not nint
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow<byte>()", csOutput);
        Assert.DoesNotContain("SwiftObjectHelper<OtherModule.SmallEnum>", csOutput);
    }

    [Theory]
    [InlineData("CGFloat", "double")]
    [InlineData("Double", "double")]
    [InlineData("Float", "float")]
    public void Emit_FloatingPointRawValueEnum_EmitsCorrectCSharpType(string swiftRawType, string expectedCSharpType)
    {
        // CGFloat/Double/Float raw value enums must map to the correct C# type
        // in FromRawValue signatures (not fall through to the Swift name).
        // Reproduces: Lottie CAKeyframeAnimation.RotationMode has CGFloat raw value.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("RotationMode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = swiftRawType;
        enumDecl.Cases.Add(CreateCase("auto"));
        enumDecl.Cases.Add(CreateCase("manual"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, $"Swift.{swiftRawType}"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // FromRawValue must use the C# type, not the Swift name
        Assert.Contains($"FromRawValue({expectedCSharpType} rawValue)", csOutput);
        Assert.DoesNotContain($"FromRawValue({swiftRawType} rawValue)", csOutput);
    }

    [Fact]
    public void Emit_CGFloatRawValueEnum_DoesNotEmitAsSimpleEnum()
    {
        // CGFloat is not integral — must NOT emit as C# enum value type
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("RotationMode", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "CGFloat";
        enumDecl.Cases.Add(CreateCase("auto"));
        enumDecl.Cases.Add(CreateCase("manual"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, "Swift.CGFloat"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Must NOT be a C# enum (CGFloat is not integral)
        Assert.DoesNotContain("public enum RotationMode", csOutput);
        // Must be class-based
        Assert.Contains("class RotationMode", csOutput);
    }

    // === BX2: New tests for simple enum expansion ===

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithIncompatibleInstanceMethod_ReturnsTrue()
    {
        // BX2: Incompatible instance methods are skipped, not blocking
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        // Add method with unsupported param type (Hasher)
        enumDecl.Methods.Add(new MethodDecl
        {
            Name = "doSomething",
            MangledName = "$s10TestModule9DirectionO11doSomethingyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("SomeModule.ComplexType"), Name = "arg", PrivateName = "arg", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        });

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void Emit_SimpleEnumWithIntProperty_EmitsExtensionGetMethod()
    {
        // BX2: Int-returning instance property → GetPriority extension + Swift wrapper + P/Invoke
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("priority", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("GetPriority(this Status self)", csOutput);
        Assert.Contains("PInvoke_GetPriority((int)self)", csOutput);
        Assert.Contains("[LibraryImport(", csOutput);
        Assert.Contains("EntryPoint =", csOutput);
        // Swift wrapper
        Assert.Contains("_sbw_Status_get_priority", swiftOutput);
        Assert.Contains("value.priority", swiftOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithStringProperty_EmitsUtf8SliceMarshalling()
    {
        // BX2: String-returning property → Utf8Slice pattern + free function
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Properties.Add(CreateInstanceStringProperty("label", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("GetLabel(this Status self)", csOutput);
        Assert.Contains("Utf8Slice", csOutput);
        Assert.Contains("Encoding.UTF8.GetString", csOutput);
        Assert.Contains("PInvoke_SBW_Free", csOutput);
        // Swift wrapper uses Utf8Slice
        Assert.Contains("SBW_Utf8Slice", swiftOutput);
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithDescription_EmitsGetDescription()
    {
        // BX2: CustomStringConvertible.description → GetDescription extension
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.CustomStringConvertible"),
            ""));
        enumDecl.Properties.Add(CreateInstanceStringProperty("description", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("GetDescription(this Status self)", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithStaticMethodReturningEnum_EmitsWithCast()
    {
        // BX2: Factory method returning same enum type → cast from underlying type
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStaticMethod("defaultStatus", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("public static Status DefaultStatus()", csOutput);
        Assert.Contains("(Status)PInvoke_DefaultStatus()", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithStaticMethodTakingEnum_EmitsWithCast()
    {
        // BX2: Static method taking enum param → cast to underlying type at call site
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStaticMethodWithEnumParam("isValid", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("IsValid(Status", csOutput);
        Assert.Contains("(int)value", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithStaticIntProperty_EmitsStaticProperty()
    {
        // BX2: Static int property → static property on extensions class
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Properties.Add(CreateStaticIntProperty("count", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("StatusExtensions", csOutput);
        Assert.Contains("Count =>", csOutput);
        Assert.Contains("PInvoke_GetCount()", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithCaseIterable_EmitsAllCasesProperty()
    {
        // BX2: CaseIterable → pure C# AllCases property
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Conformances.Add(new TypeConformance(
            enumDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.CaseIterable"),
            ""));
        // Add synthesized allCases property (filtered by IsSynthesizedProperty)
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "allCases",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array"),
            IsStatic = true,
            HasStorage = false,
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("StatusExtensions", csOutput);
        Assert.Contains("AllCases", csOutput);
        Assert.Contains("Enum.GetValues<Status>()", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithMixedCompatibility_EmitsSomeSkipsSome()
    {
        // BX2: Compatible members emitted, incompatible skipped, enum still simple
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));

        // Compatible: instance method returning same enum
        enumDecl.Methods.Add(new MethodDecl
        {
            Name = "next",
            MangledName = "$s10TestModule6StatusO4nextA2CyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.Status"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        });

        // Incompatible: instance method with unsupported param
        enumDecl.Methods.Add(new MethodDecl
        {
            Name = "doComplex",
            MangledName = "$s10TestModule6StatusO9doComplexyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("SomeModule.ComplexType"), Name = "arg", PrivateName = "arg", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("Next(this Status self)", csOutput);   // compatible emitted
        Assert.DoesNotContain("DoComplex", csOutput);          // incompatible skipped
    }

    [Fact]
    public void Emit_StringRawValueEnumWithProperties_EmitsAsSimple()
    {
        // BX2: String enum with extra properties → C# enum (properties no longer block)
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("debug"));
        enumDecl.Cases.Add(CreateCase("info"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));
        enumDecl.Properties.Add(CreateInstanceIntProperty("severity", enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum LogLevel", csOutput);
        Assert.Contains("ToRawValue", csOutput);
        Assert.Contains("GetSeverity", csOutput);
        Assert.DoesNotContain(": ISwiftObject", csOutput);
    }

    [Fact]
    public void Emit_SimpleEnumWithSettableProperty_EmitsGetterOnly()
    {
        // BX2: Setter skipped, getter emitted
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        // Property with both getter and setter
        enumDecl.Properties.Add(new PropertyDecl
        {
            Name = "priority",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "priority_Get",
                        MangledName = "$s10TestModule6StatusO8priorityS2ivg",
                        MethodType = MethodType.Instance, IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
                    }
                },
                new SetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "priority_Set",
                        MangledName = "$s10TestModule6StatusO8priorityS2ivs",
                        MethodType = MethodType.Instance, IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        });

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum Status : int", csOutput);
        Assert.Contains("GetPriority(this Status self)", csOutput);
        // Setter should NOT be emitted as an extension method
        Assert.DoesNotContain("SetPriority", csOutput);
    }

    // ==================== ABI Correctness Tests ====================

    [Fact]
    public void Emit_InstanceMethodWithEnumParam_SwiftWrapperUsesScalarType()
    {
        // Issue #1: Instance method enum params must use scalar in Swift wrapper (matching C# P/Invoke),
        // not the enum type. The wrapper converts scalar → enum before calling the Swift method.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));

        // Instance method: compare(other: Direction) -> Bool
        var compareMethod = new MethodDecl
        {
            Name = "compare",
            MangledName = "$s10TestModule9DirectionO7compareyS2bACF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.Direction"), Name = "other", PrivateName = "other", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        };
        enumDecl.Methods.Add(compareMethod);

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# P/Invoke should use int for both tag and enum param
        Assert.Contains("int tag", csOutput);
        Assert.Contains("int other", csOutput);
        Assert.Contains("[return: MarshalAs(UnmanagedType.U1)]", csOutput);

        // Swift wrapper param should be scalar (Int32), not Direction
        Assert.Contains("_ tag: Int32", swiftOutput);
        Assert.Contains("other: Int32", swiftOutput);
        // Swift wrapper should NOT declare param as Direction type
        Assert.DoesNotContain("other: Direction", swiftOutput);
    }

    [Fact]
    public void Emit_StaticMethodWithEnumParam_NonRawRepresentable_UsesTagSwitch()
    {
        // Issue #2: Non-RawRepresentable enum params must use tag switch, not rawValue init.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        // No RawValueTypeName — not RawRepresentable
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateStaticMethodWithEnumParam("isValid", enumDecl, moduleDecl));

        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should NOT use rawValue initializer (doesn't exist)
        Assert.DoesNotContain("rawValue:", swiftOutput);
        // Should use switch-based conversion
        Assert.Contains("switch value", swiftOutput);
        Assert.Contains("case 0: return .active", swiftOutput);
        Assert.Contains("case 1: return .inactive", swiftOutput);
    }

    [Fact]
    public void Emit_StaticMethodWithEnumParam_RawRepresentable_UsesRawValueInit()
    {
        // RawRepresentable enum params should use rawValue initializer
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));
        enumDecl.Methods.Add(CreateStaticMethodWithEnumParam("isValid", enumDecl, moduleDecl));

        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should use rawValue initializer for RawRepresentable
        Assert.Contains("rawValue:", swiftOutput);
    }

    [Fact]
    public void Emit_StringReturningMethodWithBoolParam_HasMarshalAsAttribute()
    {
        // Issue #3: String-return P/Invoke paths must include [MarshalAs(UnmanagedType.U1)] for bool params.
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));

        // Static method: describe(verbose: Bool) -> String
        var describeMethod = new MethodDecl
        {
            Name = "describe",
            MangledName = "$s10TestModule6StatusO8describeyS2SbFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.String"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), Name = "verbose", PrivateName = "verbose", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        };
        enumDecl.Methods.Add(describeMethod);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Bool param in P/Invoke must have MarshalAs attribute
        Assert.Contains("[MarshalAs(UnmanagedType.U1)]", csOutput);
    }

    [Fact]
    public void Emit_StringReturningInstanceMethodWithEnumParam_CastsToUnderlyingType()
    {
        // Issue #3: String-return instance path must handle enum params correctly
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Cases.Add(CreateCase("south"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));

        // Instance method: describeRelativeTo(other: Direction) -> String
        var descMethod = new MethodDecl
        {
            Name = "describeRelativeTo",
            MangledName = "$s10TestModule9DirectionO17describeRelativeToySSSACF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.String"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.Direction"), Name = "other", PrivateName = "other", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
        };
        enumDecl.Methods.Add(descMethod);

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# P/Invoke should use int for enum param, not enum type
        Assert.Contains("int tag", csOutput);
        Assert.Contains("int other", csOutput);
        // C# call should cast enum to int
        Assert.Contains("(int)other", csOutput);

        // Swift wrapper should accept scalar, not enum type
        Assert.Contains("other: Int32", swiftOutput);
        Assert.DoesNotContain("other: Direction", swiftOutput);
    }

    private static PropertyDecl CreateInstanceStringProperty(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name}SSvg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new() { SwiftTypeSpec = new NamedTypeSpec("Swift.String"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = enumDecl, ModuleDecl = moduleDecl, Throws = false, IsAsync = false, Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateStaticMethodWithEnumParam(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name}ySbACFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec($"TestModule.{enumDecl.Name}"), Name = "value", PrivateName = "value", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
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
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
