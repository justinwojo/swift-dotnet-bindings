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
        // Swift "Int" maps to C# "long" for enum underlying types (matching 64-bit ABI)
        Assert.Contains("public enum Status : long", csOutput);
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
    public void CanSafelyEmitAsSimpleEnum_WithComparisonOperator_ReturnsTrue()
    {
        // C# integral enums natively support <, >, <=, >= — Comparable conformance
        // should not force the enum to the class-based path.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Operators.Add(CreateOperator("<", enumDecl, moduleDecl));

        Assert.True(EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
    }

    [Fact]
    public void CanSafelyEmitAsSimpleEnum_WithCustomOperator_ReturnsFalse()
    {
        // Custom operators (e.g., +) force the class-based path.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Direction", moduleDecl, isFrozen: false);
        enumDecl.Cases.Add(CreateCase("north"));
        enumDecl.Operators.Add(CreateOperator("+", enumDecl, moduleDecl));

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

        // T has no protocol conformances, so the ISwiftObject seed is dropped — blittable
        // instantiations like ValueProviderStorage<float> compile at the call site.
        Assert.Contains("public partial class ValueProviderStorage<T> : ISwiftObject, ISwiftStruct, IDisposable", csOutput);
        Assert.DoesNotContain("ValueProviderStorage<T> : ISwiftObject, ISwiftStruct, IDisposable where", csOutput);
        Assert.Contains("public static unsafe ValueProviderStorage<T> Boxed(T value)", csOutput);
        Assert.Contains("var valueMetadata = TypeMetadata.GetTypeMetadataOrThrow<T>();", csOutput);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value, ref valueSwiftSpan);", csOutput);
        Assert.Contains("ValueProviderStorage_PInvoke.PInvoke_Boxed(indirectResult, (IntPtr)valueSwiftBuffer", csOutput);
        Assert.Contains("internal static unsafe partial class ValueProviderStorage_PInvoke", csOutput);
    }

    [Fact]
    public void Emit_GenericEnum_PayloadSizeUsesHelperPInvokeAccessor()
    {
        // Regression: when emitting a generic enum, `_payloadSize` MUST go through the
        // helper class metadata accessor PInvoke (`{Type}_PInvoke.PInvoke_getMetadata`)
        // — never `SwiftObjectHelper<{Type}<T>>.GetTypeMetadata().Size`. The latter
        // crashes Mono's generic sharing (mini-generic-sharing.c:2759) because it tries
        // to compile a nested generic instantiation without the type argument's metadata.
        //
        // Historically the emission was wrapped in a `Lazy<nuint>` to defer the call
        // until first use, working around a separate PAC trap on NativeAOT/arm64e
        // caused by missing protocol-witness-table args on the metadata accessor. That
        // PAC trap is now fixed end-to-end (constrained generic metadata and witness-table args),
        // so the lazy wrapper has been removed and the field initializer is eager again.
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

        // The eager helper-PInvoke field initializer must be emitted. The PInvoke call is
        // wrapped in TypeMetadata.RegisterAndGetSize so RunClassConstructor → static field
        // init populates both TypeMetadata.Cache AND the NewFromPayloadDispatcher factory
        // (via NewFromPayloadCore), avoiding reflection for MarshalFromSwift on closed
        // generic instantiations.
        Assert.Contains(
            "static nuint _payloadSize = TypeMetadata.RegisterAndGetSize(typeof(ValueProviderStorage<T>), ValueProviderStorage_PInvoke.PInvoke_getMetadata(TypeMetadataRequest.Complete,",
            csOutput);
        Assert.Contains("NewFromPayloadCore", csOutput);
        // The Mono-JIT-crashing form must NEVER appear for generic enums.
        Assert.DoesNotContain(
            "static nuint _payloadSize = SwiftObjectHelper<ValueProviderStorage<T>>",
            csOutput);
        // The historical lazy workaround must be gone.
        Assert.DoesNotContain("_payloadSizeLazy", csOutput);
        Assert.DoesNotContain("global::System.Lazy<nuint>", csOutput);
    }

    [Fact]
    public void Emit_GenericEnum_TryGetBareTypeParameterPayload_EmitsClassVsStructDispatch()
    {
        // TryGet<Case> on a generic enum whose payload is a bare
        // type parameter must runtime-dispatch between class T and non-class T, and each
        // branch must transfer ownership correctly to whatever SafeHandle MarshalFromSwift
        // produces. Two correctness invariants:
        //
        //   1. Class T (Kind == Class, !IsValueType, !ISwiftStruct): payload bytes ARE a
        //      heap class pointer. The enum-level InitializeWithCopy that filled enumCopy
        //      already deposited an isa-correct +1 on that payload (swift_retain for a
        //      pure-Swift T, objc_retain for an @objc:NSObject-rooted T), and enumCopy is
        //      never VWT-destroyed on the success path — so that +1 is ours to hand off.
        //      We dereference *(IntPtr*)enumCopy and pass it straight to MarshalFromSwift<T>,
        //      whose NewFromPayload ADOPTS exactly one reference (consuming the copy's +1).
        //      An extra explicit retain here would over-retain by +1 per extraction and the
        //      payload would never reach refcount 0 (issue #40 — the original leak).
        //
        //   2. Non-class T (Kind != Class, includes ISwiftStruct, primitives, value
        //      structs): heap-allocate a buffer, InitializeWithCopy from the stack source,
        //      hand the heap pointer to MarshalFromSwift. ISwiftObject T transfers buffer
        //      ownership to its SafeHandle (which frees on dispose); non-ISwiftObject T
        //      reads by value and we Destroy + Free ourselves. Passing the stack buffer
        //      address directly to MarshalFromSwift would crash SwiftSafeHandle.ReleaseHandle
        //      via NativeMemory.Free on a non-heap pointer.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Holder", moduleDecl, isFrozen: true);
        enumDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        var wrappedCase = CreateCase("wrapped");
        wrappedCase.AssociatedValues.Add(new NamedTypeSpec("τ_0_0"));
        enumDecl.Cases.Add(wrappedCase);
        enumDecl.Cases.Add(CreateCase("empty"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public bool TryGetWrapped", csOutput);
        Assert.Contains("metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);", csOutput);
        // Hoisted runtime metadata used by both branches.
        Assert.Contains("var __value_meta = global::Swift.Runtime.TypeMetadata.GetTypeMetadataOrThrow<T>();", csOutput);
        // Class-T branch: metadata kind dispatch + pointer dereference, then adopt the
        // enum-copy's existing +1 directly via MarshalFromSwift (no explicit retain).
        Assert.Contains("__value_meta.Kind == global::Swift.Runtime.TypeMetadataKind.Class", csOutput);
        Assert.Contains("var __value_classPtr = *(IntPtr*)(enumCopy);", csOutput);
        // No explicit retain: MarshalFromSwift ADOPTS the +1 the enum-level InitializeWithCopy
        // already deposited on the never-destroyed enumCopy. An extra retain (either family)
        // over-retains by +1 per extraction and the payload never deallocs (issue #40).
        Assert.DoesNotContain("global::Swift.Runtime.Arc.UnknownObjectRetain(__value_classPtr);", csOutput);
        Assert.DoesNotContain("global::Swift.Runtime.Arc.Retain(__value_classPtr);", csOutput);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<T>(__value_classPtr)", csOutput);
        // Non-class fallback: heap-alloc + InitializeWithCopy + ownership-transfer cleanup
        // for non-ISwiftObject T. Must NOT pass the stack buffer pointer directly.
        Assert.Contains("void* __value_heap = global::System.Runtime.InteropServices.NativeMemory.Alloc(__value_meta.Size);", csOutput);
        Assert.Contains("__value_meta.ValueWitnessTable->InitializeWithCopy(__value_heap, (void*)(enumCopy), __value_meta);", csOutput);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<T>(new IntPtr(__value_heap))", csOutput);
        Assert.Contains("if (!typeof(global::Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof(T)))", csOutput);
        Assert.Contains("__value_meta.ValueWitnessTable->Destroy(__value_heap, __value_meta);", csOutput);
        Assert.Contains("global::System.Runtime.InteropServices.NativeMemory.Free(__value_heap);", csOutput);
        // The pre-fix shape — passing the stack buffer pointer to MarshalFromSwift —
        // must not regress.
        Assert.DoesNotContain("SwiftMarshal.MarshalFromSwift<T>(new IntPtr(enumCopy))", csOutput);
        Assert.DoesNotContain("SwiftMarshal.MarshalFromSwift<T>(*(IntPtr*)(enumCopy))", csOutput);
    }

    [Fact]
    public void Emit_GenericEnum_TryGetSugaredTypeParameterPayload_ResolvesAppleShape()
    {
        // E.1 Apple-framework regression: the τ_0_0 case above only exercises the
        // source-compiled ABI shape that swift-api-digester emits for our BindingTests
        // fixtures. Apple framework ABI JSON (e.g. StoreKit2.VerificationResult<SignedType>)
        // encodes generic-parameter payloads with the SUGARED declarator name —
        // NamedTypeSpec("SignedType"), not NamedTypeSpec("τ_0_0"). That sugared shape
        // bypasses TypeSpecHelpers.IsGenericTypeParameter (length-≤3 simple-letter
        // shortlist) so GetCSharpTypeNameForEnumCase fell through to AnyType and
        // EmitTryGetMethod silently skipped — leaving VerificationResult<T> with
        // CaseTag + DebugDescription only and no payload extractor.
        //
        // The fix detects the Apple shape via the enum's own genericParams list
        // (matching SugaredTypeName / TypeName), so this test mirrors the τ_0_0
        // fixture but feeds the bare sugared name through the case payload.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("VerificationResult", moduleDecl, isFrozen: true);
        enumDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "SignedType",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        var verifiedCase = CreateCase("verified");
        verifiedCase.AssociatedValues.Add(new NamedTypeSpec("SignedType"));
        enumDecl.Cases.Add(verifiedCase);
        enumDecl.Cases.Add(CreateCase("invalid"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // The TryGet method MUST emit and MUST use the resolved C# generic parameter
        // name (TSignedType) — not the AnyType fallback and not the raw "SignedType".
        Assert.Contains("public bool TryGetVerified([MaybeNullWhen(false)] out TSignedType value)", csOutput);
        // The bare-generic-parameter marshalling branch in EnumHandler.Marshalling.cs
        // must fire: class-T metadata-kind dispatch + dereference + adopt-the-copy
        // MarshalFromSwift (no explicit retain), vs non-class heap-alloc + InitializeWithCopy
        // + ownership-transfer cleanup. Without the marshalling-side gate change (dropping the
        // redundant IsGenericTypeParameter pre-check), the body would silently fall through to
        // the AnyType branch and emit MarshalFromSwift<global::Swift.AnyType>.
        Assert.Contains("var __value_meta = global::Swift.Runtime.TypeMetadata.GetTypeMetadataOrThrow<TSignedType>();", csOutput);
        Assert.Contains("__value_meta.Kind == global::Swift.Runtime.TypeMetadataKind.Class", csOutput);
        Assert.Contains("var __value_classPtr = *(IntPtr*)(enumCopy);", csOutput);
        // No explicit retain — MarshalFromSwift adopts the enum-copy's existing +1 (issue #40).
        Assert.DoesNotContain("global::Swift.Runtime.Arc.UnknownObjectRetain(__value_classPtr);", csOutput);
        Assert.DoesNotContain("global::Swift.Runtime.Arc.Retain(__value_classPtr);", csOutput);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TSignedType>(__value_classPtr)", csOutput);
        Assert.Contains("void* __value_heap = global::System.Runtime.InteropServices.NativeMemory.Alloc(__value_meta.Size);", csOutput);
        Assert.Contains("__value_meta.ValueWitnessTable->InitializeWithCopy(__value_heap, (void*)(enumCopy), __value_meta);", csOutput);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TSignedType>(new IntPtr(__value_heap))", csOutput);
        Assert.Contains("if (!typeof(global::Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof(TSignedType)))", csOutput);
        Assert.Contains("__value_meta.ValueWitnessTable->Destroy(__value_heap, __value_meta);", csOutput);
        Assert.Contains("global::System.Runtime.InteropServices.NativeMemory.Free(__value_heap);", csOutput);
        // The pre-fix stack-pointer shape (passing the stack buffer pointer to MarshalFromSwift) must not regress.
        Assert.DoesNotContain("SwiftMarshal.MarshalFromSwift<TSignedType>(new IntPtr(enumCopy))", csOutput);
        Assert.DoesNotContain("SwiftMarshal.MarshalFromSwift<TSignedType>(*(IntPtr*)(enumCopy))", csOutput);
        // AnyType must never leak into the TryGet signature for a generic-param payload —
        // that was the silent-skip symptom the sugared-name fix eliminates.
        Assert.DoesNotContain("out global::Swift.AnyType value", csOutput);
    }

    [Fact]
    public void Emit_GenericEnum_CaseFactorySugaredTypeParameter_ResolvesAppleShape()
    {
        // The original sugared-name fix resolved the TryGet path via a
        // local AnyType bypass but left GetCSharpTypeNameForEnumCase still pre-gated on
        // TypeSpecHelpers.IsGenericTypeParameter (length-≤3 simple-letter shortlist).
        // EmitEnumCaseWithAssociatedValues (the static case factory) shares that helper,
        // so for Apple-shape sugared payloads it kept seeing AnyType and bailed at the
        // factory's own AnyType gate — emitting the read-only `Verified` accessor only,
        // never the constructable factory.
        //
        // The actual fix drops the IsGenericTypeParameter pre-gate inside
        // GetCSharpTypeNameForEnumCase. The extended TryGetGenericTypeParameterName
        // already handles τ_X_Y, T+digit, AND multi-character sugared names; non-matches
        // fall through to the typedb lookup unchanged. This test guards the factory
        // side: for VerificationResult<SignedType>.verified(SignedType), the static
        // factory must emit with the resolved TSignedType parameter, not AnyType.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("VerificationResult", moduleDecl, isFrozen: true);
        enumDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "SignedType",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        var verifiedCase = CreateCase("verified");
        verifiedCase.AssociatedValues.Add(new NamedTypeSpec("SignedType"));
        enumDecl.Cases.Add(verifiedCase);
        enumDecl.Cases.Add(CreateCase("invalid"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // The static case factory MUST emit with TSignedType in its parameter list —
        // the constructable surface (Verified(T payload)) is what consumers call to
        // round-trip a payload through the C# → Swift boundary.
        Assert.Contains("Verified(TSignedType", csOutput);
        // The factory must return the bound generic enum instance, not AnyType.
        Assert.Contains("VerificationResult<TSignedType> Verified", csOutput);
        // AnyType must never leak into the factory parameter list — that was the
        // case-factory-skip symptom guarded here.
        Assert.DoesNotContain("Verified(global::Swift.AnyType", csOutput);
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
        // Caseless enums with static members must emit those members, not just nested types.
        // Real example: a caseless enum with static properties like commonCountryCodes, forceModalPresentation, etc.
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

    private static OperatorDecl CreateOperator(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new OperatorDecl
        {
            Name = name,
            OperatorSymbol = name,
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = new MethodDecl
            {
                Name = name,
                MangledName = $"$s10TestModule{enumDecl.Name}O{name}oiySbAC_ACtFZ",
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
        enum1.Cases.Add(CreateCase("timeout"));
        enum1.Methods.Add(CreateStringRawValueInitializer(enum1, moduleDecl));
        container1.Types.Add(enum1);

        // Create second nested enum: TestModule.Container2.ErrorType (non-frozen → class emission)
        var container2 = CreateStructDecl("Container2", moduleDecl);
        var enum2 = CreateNestedEnumDecl("ErrorType", container2, moduleDecl, isFrozen: false);
        enum2.RawValueTypeName = "String";
        enum2.Cases.Add(CreateCase("failed"));
        enum2.Cases.Add(CreateCase("expired"));
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
        typeDatabase.AsyncLibraryName = "DocScanSwiftBindings";
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
        typeDatabase.AsyncLibraryName = "DocScanSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("ErrorCode", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Cases.Add(CreateCase("timeout"));
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        // Context-based tracking: tests use default context (no parallelism)
        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // The wrapper P/Invoke (InitWithRawValue) should use AsyncLibraryName
        Assert.Contains("[LibraryImport(\"DocScanSwiftBindings\", EntryPoint = \"SBW_TestModule_ErrorCode_InitWithRawValue\"", csOutput);
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
        enumDecl.Cases.Add(CreateCase("timeout"));
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

        var imagePipelineModule = new ModuleTypeDatabase("ImagePipeline", "/tmp/ImagePipeline.dylib");
        imagePipelineModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageResponse"),
                MetadataAccessor = "$s13ImagePipeline13ImageResponseVMa",
                Flags = TypeRecordFlags.None, // NOT frozen — ClassWithOpaquePayload
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(imagePipelineModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithCrossModuleClass()
    {
        // Two modules: `Lib` (where the enum is declared) and `Dep` (which owns the class payload).
        // An enum in one module carries a payload type owned by another.
        var typeDatabase = CreateTypeDatabase();
        var depModule = new ModuleTypeDatabase("Dep", "/tmp/Dep.dylib");
        depModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Dep.ForeignClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Dep", "ForeignClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Dep.ForeignClass"),
                MetadataAccessor = "$s3Dep12ForeignClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(depModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("Lib", "/tmp/Lib.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithCrossModuleFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var depModule = new ModuleTypeDatabase("Dep", "/tmp/Dep.dylib");
        depModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Dep.ForeignPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Dep", "ForeignPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Dep.ForeignPoint"),
                MetadataAccessor = "$s3Dep12ForeignPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(depModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("Lib", "/tmp/Lib.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithCrossModuleNonFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var depModule = new ModuleTypeDatabase("Dep", "/tmp/Dep.dylib");
        depModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Dep.ForeignConfig"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Dep", "ForeignConfig"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Dep.ForeignConfig"),
                MetadataAccessor = "$s3Dep13ForeignConfigVMa",
                Flags = TypeRecordFlags.None, // NOT frozen — ClassWithOpaquePayload shape
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(depModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("Lib", "/tmp/Lib.dylib"));
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
        // Body should extract container via ExistentialContainerFactory
        Assert.Contains("ExistentialContainerFactory.GetOrCreate", csOutput);
    }

    [Fact]
    public void Emit_ExistentialWithoutProxy_UsesAnyError()
    {
        // Swift.Error is a well-known protocol → maps to Swift.Foundation.AnyError (not ExistentialContainer1)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LoadError", moduleDecl, isFrozen: true);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Should use AnyError (well-known runtime type, no proxy)
        Assert.Contains("Swift.Foundation.AnyError", csOutput);
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
        Assert.Contains("out Swift.Foundation.AnyError value1", csOutput);
        // Proxy wrapping for known protocol
        Assert.Contains("new ImageProcessingProxy(", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithNonFrozenStructParam_UsesIntPtrAndPayloadExtract()
    {
        // Non-frozen struct as enum case associated value → P/Invoke uses IntPtr + .Payload.DangerousGetHandle()
        var typeDatabase = CreateTypeDatabaseWithNonFrozenStruct();
        var moduleDecl = CreateModuleDecl("ImagePipeline");
        var enumDecl = CreateEnumDecl("ImageError", moduleDecl, isFrozen: false);
        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new NamedTypeSpec("ImagePipeline.ImageResponse"));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // P/Invoke parameter should use IntPtr (not the C# class name)
        Assert.Contains("IntPtr imageResponse)", csOutput);
        // Call site should extract the SafeHandle payload
        Assert.Contains(".Payload.DangerousGetHandle()", csOutput);
        // P/Invoke declaration should use IntPtr, not ImageResponse
        Assert.Contains("PInvoke_Failed(SwiftIndirectResult", csOutput);
        Assert.DoesNotContain("PInvoke_Failed(SwiftIndirectResult result, ImagePipeline.ImageResponse", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithCrossModuleClassPayload_EmitsFactoryAndExtractor()
    {
        // Regression: an enum in module `Lib` with `.completed(payload: Dep.ForeignClass)`
        // must emit BOTH the `Completed` factory AND the `TryGetCompleted` extractor when the
        // payload type lives in a *different* module. The bug had the factory + extractor silently
        // dropped while the sibling `failed(error: any Swift.Error)` case still emitted — this
        // test locks the cross-module class-payload code path so the TypeDatabase lookup,
        // projection, and guard wiring stay symmetric.
        var typeDatabase = CreateTypeDatabaseWithCrossModuleClass();
        var moduleDecl = CreateModuleDecl("Lib");
        var enumDecl = CreateEnumDecl("CrossModResult", moduleDecl, isFrozen: false);

        var completedCase = CreateCase("completed");
        completedCase.AssociatedValues.Add(new NamedTypeSpec("Dep.ForeignClass") { TypeLabel = "payload" });
        enumDecl.Cases.Add(completedCase);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }) { TypeLabel = "error" });
        enumDecl.Cases.Add(failedCase);

        enumDecl.Cases.Add(CreateCase("canceled"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory: signature must use the cross-module class type
        Assert.Contains("public static unsafe CrossModResult Completed(Dep.ForeignClass payload)", csOutput);
        // Extractor: signature must use the cross-module class type
        Assert.Contains("public bool TryGetCompleted([MaybeNullWhen(false)] out Dep.ForeignClass value)", csOutput);
        // Class-payload extraction ADOPTS the +1 the enum-level InitializeWithCopy already
        // deposited on the never-destroyed enum-copy buffer: the class pointer is read and
        // handed straight to MarshalFromSwift, whose NewFromPayload consumes exactly one
        // reference. No explicit retain — an extra one (either family) would over-retain an
        // @objc:NSObject-rooted payload by +1/extraction (issue #40 — the leak).
        Assert.Contains("SwiftMarshal.MarshalFromSwift<Dep.ForeignClass>(_value_classPtr)", csOutput);
        Assert.DoesNotContain("Arc.UnknownObjectRetain(", csOutput);
        Assert.DoesNotContain("Arc.Retain(_value_classPtr)", csOutput);
        // Sibling Failed case still emits via AnyError (well-known proxy)
        Assert.Contains("public bool TryGetFailed([MaybeNullWhen(false)] out Swift.Foundation.AnyError value)", csOutput);
        // Sentinel: the payload must NOT have collapsed to AnyType (that's the bug shape)
        Assert.DoesNotContain("out Swift.AnyType", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithCrossModuleFrozenStructPayload_EmitsFactoryAndExtractor()
    {
        // Regression (frozen-struct variant): cross-module `.completed(payload: Dep.ForeignPoint)`
        // where the payload is a `@frozen` struct must round-trip through factory + extractor with
        // the foreign struct's namespace preserved.
        var typeDatabase = CreateTypeDatabaseWithCrossModuleFrozenStruct();
        var moduleDecl = CreateModuleDecl("Lib");
        var enumDecl = CreateEnumDecl("CrossModFrozenResult", moduleDecl, isFrozen: false);

        var completedCase = CreateCase("completed");
        completedCase.AssociatedValues.Add(new NamedTypeSpec("Dep.ForeignPoint") { TypeLabel = "payload" });
        enumDecl.Cases.Add(completedCase);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }) { TypeLabel = "error" });
        enumDecl.Cases.Add(failedCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory + extractor must both name the cross-module frozen struct
        Assert.Contains("Completed(Dep.ForeignPoint payload)", csOutput);
        Assert.Contains("TryGetCompleted([MaybeNullWhen(false)] out Dep.ForeignPoint value)", csOutput);
        // Sentinel: must not collapse to AnyType or IntPtr (those are non-frozen / unknown shapes)
        Assert.DoesNotContain("out Swift.AnyType", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithCrossModuleNonFrozenStructPayload_EmitsFactoryAndExtractor()
    {
        // Regression (non-frozen-struct variant): cross-module `.completed(payload: Dep.ForeignConfig)`
        // where the payload is a non-`@frozen` struct (`ClassWithOpaquePayload` shape) must round-trip
        // through the `SwiftSafeHandle<T>` + `InitializeWithCopy` heap path with the foreign namespace
        // preserved.
        var typeDatabase = CreateTypeDatabaseWithCrossModuleNonFrozenStruct();
        var moduleDecl = CreateModuleDecl("Lib");
        var enumDecl = CreateEnumDecl("CrossModNonFrozenResult", moduleDecl, isFrozen: false);

        var completedCase = CreateCase("completed");
        completedCase.AssociatedValues.Add(new NamedTypeSpec("Dep.ForeignConfig") { TypeLabel = "payload" });
        enumDecl.Cases.Add(completedCase);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }) { TypeLabel = "error" });
        enumDecl.Cases.Add(failedCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Factory + extractor must both name the cross-module non-frozen struct
        Assert.Contains("Completed(Dep.ForeignConfig payload)", csOutput);
        Assert.Contains("TryGetCompleted([MaybeNullWhen(false)] out Dep.ForeignConfig value)", csOutput);
        // Non-frozen struct payload extracts via SafeHandle path
        Assert.Contains(".Payload.DangerousGetHandle()", csOutput);
        // Sentinel: must not collapse to AnyType
        Assert.DoesNotContain("out Swift.AnyType", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithTupleContainingAnyType_UsesIntPtrAndPayloadExtractForUnknownElement()
    {
        // Tuple associated value where one element is unknown → resolves to AnyType (Kind=Protocol).
        // GetPInvokeArgument recurses into each tuple element; the unknown element hits the AnyType
        // branch and emits .Payload.DangerousGetHandle(). This is the (nint, AnyType) tuple-element scenario.
        var typeDatabase = CreateTypeDatabase();
        var animationModule = new ModuleTypeDatabase("VectorAnimation", "/tmp/VectorAnimation.dylib");
        typeDatabase.AddModuleDatabase(animationModule);

        var moduleDecl = CreateModuleDecl("VectorAnimation");
        var enumDecl = CreateEnumDecl("AnimationResult", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        // Tuple: (Int, UnknownType) — Int is registered, UnknownType resolves to AnyType
        dataCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("VectorAnimation.UnknownType")
        }));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // P/Invoke signature: unknown element becomes IntPtr in the ValueTuple
        Assert.Contains("ValueTuple<long, IntPtr> value)", csOutput);
        // Call site: known element passes directly, unknown element extracts SafeHandle payload
        Assert.Contains("value.Item2.Payload.DangerousGetHandle()", csOutput);
        // The AnyType class name should NOT appear in the P/Invoke ValueTuple
        Assert.DoesNotContain("ValueTuple<long, Swift.AnyType>", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithDirectAnyTypePayload_SkipsTryGetMethod()
    {
        // Single associated value that resolves to AnyType (Kind=Protocol) — extraction
        // cannot round-trip: AnyType.GetTypeMetadata and MarshalToSwift both throw, and
        // SwiftSafeHandle<AnyType> would NativeMemory.Free the stackalloc'd enumCopy
        // address on dispose. EnumHandler.CaseInspection.cs:138 must skip TryGet emission.
        var typeDatabase = CreateTypeDatabase();
        var animationModule = new ModuleTypeDatabase("VectorAnimation", "/tmp/VectorAnimation.dylib");
        typeDatabase.AddModuleDatabase(animationModule);

        var moduleDecl = CreateModuleDecl("VectorAnimation");
        var enumDecl = CreateEnumDecl("AnimationResult", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        // Single payload: UnknownType resolves to AnyType (module exists, type unregistered)
        dataCase.AssociatedValues.Add(new NamedTypeSpec("VectorAnimation.UnknownType"));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // TryGet for the AnyType payload case must NOT be emitted
        Assert.DoesNotContain("TryGetData(", csOutput);
        // The unsafe out parameter that would otherwise crash must not appear
        Assert.DoesNotContain("out Swift.AnyType value", csOutput);
        // Sanity: case still participates in tag enumeration
        Assert.Contains("Data = 0", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithTupleContainingAnyType_SkipsTryGetMethod()
    {
        // Companion to Emit_EnumCaseWithTupleContainingAnyType_UsesIntPtrAndPayloadExtractForUnknownElement:
        // the construction (factory) path emits, but the extraction (TryGet) path must skip.
        // EnumHandler.CaseInspection.cs:312 catches direct AnyType inside any tuple element —
        // NewFromPayload would wrap the source pointer in SwiftSafeHandle<AnyType>, and the
        // dispose path would NativeMemory.Free that pointer (stackalloc'd enumCopy → invalid free).
        var typeDatabase = CreateTypeDatabase();
        var animationModule = new ModuleTypeDatabase("VectorAnimation", "/tmp/VectorAnimation.dylib");
        typeDatabase.AddModuleDatabase(animationModule);

        var moduleDecl = CreateModuleDecl("VectorAnimation");
        var enumDecl = CreateEnumDecl("AnimationResult", moduleDecl, isFrozen: false);
        var dataCase = CreateCase("data");
        dataCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("VectorAnimation.UnknownType")
        }));
        enumDecl.Cases.Add(dataCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // TryGet must NOT be emitted for the tuple-with-AnyType case
        Assert.DoesNotContain("TryGetData(", csOutput);
        // The dangerous extraction signature must not appear
        Assert.DoesNotContain("out Swift.AnyType", csOutput);
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
        Assert.Contains("(string, IImageProcessing) value)", csOutput);
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
        Assert.Contains("public static unsafe Message Text(string value)", csOutput);
        // Body: converts string → SwiftString for P/Invoke
        Assert.Contains("using var __value = new SwiftString(value);", csOutput);
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
        // Reproduces a CS0315 bug: a simple enum used in a tuple associated value. The emitter
        // must use GetTypeMetadataOrThrow<nint>() (matching the Swift ABI backing type),
        // not SwiftObjectHelper<T> (requires ISwiftObject).
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
        // Reproduces: an enum with CGFloat raw value.
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

    [Theory]
    [InlineData("Double", "double")]
    [InlineData("CGFloat", "double")]
    [InlineData("Float", "float")]
    public void Emit_NonStringRawValueEnum_UsesCaseByIndexWhenWrapperLibAvailable(string swiftRawType, string expectedCSharpType)
    {
        // ABI JSON lacks actual raw values, so ordinal-based FromRawValue(i) fails
        // for enums with non-sequential raw values (e.g., Unit: TimeInterval where seconds=1).
        // When a wrapper library is available, CaseByIndex constructs cases directly.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Unit", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = swiftRawType;
        enumDecl.Cases.Add(CreateCase("seconds"));
        enumDecl.Cases.Add(CreateCase("milliseconds"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, $"Swift.{swiftRawType}"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Static case properties must use CaseByIndex, NOT FromRawValue(ordinal)
        Assert.Contains("PInvoke_CaseByIndex(0)", csOutput);
        Assert.Contains("PInvoke_CaseByIndex(1)", csOutput);
        Assert.DoesNotContain("FromRawValue(0)", csOutput);
        Assert.DoesNotContain("FromRawValue(1)", csOutput);
        // CaseByIndex P/Invoke must target the wrapper library
        Assert.Contains("TestModuleSwiftBindings", csOutput);
        Assert.Contains("CaseByIndex", csOutput);
        // FromRawValue method itself must still exist for user code
        Assert.Contains($"FromRawValue({expectedCSharpType} rawValue)", csOutput);
        // Swift wrapper must emit CaseByIndex function
        Assert.Contains("SBW_TestModule_Unit_CaseByIndex", swiftOutput);
        Assert.Contains("case 0: value = .seconds", swiftOutput);
        Assert.Contains("case 1: value = .milliseconds", swiftOutput);
    }

    [Theory]
    [InlineData("Double", "double")]
    [InlineData("Float", "float")]
    [InlineData("CGFloat", "double")]
    public void Emit_NonFrozenBlittableRawValueEnum_EmitsCdeclWrapper(string swiftRawType, string expectedCSharpType)
    {
        // Non-frozen blittable enum init(rawValue:) must use @_cdecl wrapper to avoid
        // CallConvSwift + SwiftIndirectResult crash on Mono JIT.
        // Integral raw types (Int, UInt, etc.) are not tested here — they emit as C# enum value types
        // via EmitSimpleEnum which doesn't use the class-based RawRepresentable path.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Unit", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = swiftRawType;
        enumDecl.Cases.Add(CreateCase("seconds"));
        enumDecl.Cases.Add(CreateCase("milliseconds"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, $"Swift.{swiftRawType}"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Swift wrapper must emit @_cdecl function for init(rawValue:)
        Assert.Contains("@_cdecl(\"SBW_TestModule_Unit_InitWithRawValue\")", swiftOutput);
        Assert.Contains($"_ rawValue: {swiftRawType}", swiftOutput);
        Assert.Contains("TestModule.Unit(rawValue: rawValue)", swiftOutput);
        Assert.Contains("MemoryLayout<TestModule.Unit?>.size", swiftOutput);

        // C# P/Invoke must target the wrapper with Cdecl, not the raw mangled symbol
        Assert.Contains("PInvoke_InitWithRawValue_Wrapper((IntPtr)resultBuffer, rawValue)", csOutput);
        Assert.Contains("TestModuleSwiftBindings", csOutput);
        Assert.Contains("SBW_TestModule_Unit_InitWithRawValue", csOutput);
        Assert.Contains($"IntPtr resultPtr, {expectedCSharpType} rawValue", csOutput);
        // Must NOT contain SwiftIndirectResult (that's the direct P/Invoke pattern)
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);
    }

    [Fact]
    public void Emit_NonFrozenBlittableRawValueEnum_NoWrapperWithoutWrapperLib()
    {
        // Without wrapper library, non-frozen blittable enum falls back to direct P/Invoke
        var typeDatabase = CreateTypeDatabase();
        // AsyncLibraryName NOT set — no wrapper lib
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Unit", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "Double";
        enumDecl.Cases.Add(CreateCase("seconds"));
        enumDecl.Cases.Add(CreateCase("milliseconds"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, "Swift.Double"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Without wrapper lib, no @_cdecl wrapper
        Assert.DoesNotContain("SBW_TestModule_Unit_InitWithRawValue", swiftOutput);
        // Falls back to direct SwiftIndirectResult P/Invoke
        Assert.Contains("SwiftIndirectResult", csOutput);
    }

    [Fact]
    public void Emit_NonStringRawValueEnum_FallsBackToOrdinalWithoutWrapperLib()
    {
        // Without wrapper library (manual mode), non-string enums fall back to FromRawValue(ordinal)
        var typeDatabase = CreateTypeDatabase();
        // AsyncLibraryName NOT set — simulates manual mode
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Unit", moduleDecl, isFrozen: false);
        enumDecl.RawValueTypeName = "Double";
        enumDecl.Cases.Add(CreateCase("seconds"));
        enumDecl.Cases.Add(CreateCase("milliseconds"));
        enumDecl.Methods.Add(CreateTypedRawValueInitializer(enumDecl, moduleDecl, "Swift.Double"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Without wrapper library, falls back to ordinal-based FromRawValue
        Assert.Contains("FromRawValue(0)", csOutput);
        Assert.DoesNotContain("CaseByIndex", csOutput);
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
    public void Emit_RawRepresentableEnumPropertyGetter_UsesTagSwitchNotRawValue()
    {
        // Regression: RawRepresentable enum property getters used rawValue: force-unwrap
        // which crashes when C# sequential tags don't match Swift raw values (e.g.,
        // an enum with OSStatus raw values like -25293, but C# uses 0,1,2...).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("success"));
        enumDecl.Cases.Add(CreateCase("authFailed"));
        enumDecl.Cases.Add(CreateCase("itemNotFound"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));
        enumDecl.Properties.Add(CreateInstanceIntProperty("errorCode", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Property getter Swift wrapper must use switch, NOT rawValue: force-unwrap
        Assert.DoesNotContain("rawValue: tag)!", swiftOutput);
        Assert.DoesNotContain("rawValue: Int32(tag))!", swiftOutput);
        // Must use switch-based reconstruction
        Assert.Contains("switch tag", swiftOutput);
        Assert.Contains("case 0: value = .success", swiftOutput);
        Assert.Contains("case 1: value = .authFailed", swiftOutput);
        Assert.Contains("case 2: value = .itemNotFound", swiftOutput);
    }

    [Fact]
    public void Emit_RawRepresentableEnumMethodReturn_UsesTagSwitchNotRawValue()
    {
        // Regression: methods returning a RawRepresentable enum used result.rawValue
        // which returns non-sequential Swift raw values (e.g., OSStatus -25293) instead
        // of the sequential C# tag values (0, 1, 2...). The return path must use the
        // same switch-based enum-to-tag conversion as the input path.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("success"));
        enumDecl.Cases.Add(CreateCase("authFailed"));
        enumDecl.Cases.Add(CreateCase("itemNotFound"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));
        enumDecl.Methods.Add(CreateStaticMethod("getCurrent", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Return path must use switch on result, NOT result.rawValue
        Assert.DoesNotContain("result.rawValue", swiftOutput);
        // Must use switch-based enum-to-tag conversion
        Assert.Contains("switch result", swiftOutput);
        Assert.Contains("case .success: return 0", swiftOutput);
        Assert.Contains("case .authFailed: return 1", swiftOutput);
        Assert.Contains("case .itemNotFound: return 2", swiftOutput);
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
    public void Emit_SimpleEnumWithOptionalStringProperty_EmitsNullableGetter()
    {
        // LocalizedError conformance: errorDescription etc. return `String?`. The simple-enum
        // extension emitter must accept Optional<Swift.String> and emit a `string?`-returning
        // extension method whose Swift wrapper returns `UnsafeMutableRawPointer?` (nil = None).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("MyError", moduleDecl, isFrozen: true);
        enumDecl.Cases.Add(CreateCase("permissionDenied"));
        enumDecl.Cases.Add(CreateCase("unknown"));
        enumDecl.Properties.Add(CreateInstanceOptionalStringProperty("errorDescription", enumDecl, moduleDecl));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        Assert.Contains("public enum MyError : int", csOutput);
        // C# extension returns string?, checks IntPtr.Zero → null
        Assert.Contains("string? GetErrorDescription(this MyError self)", csOutput);
        Assert.Contains("if (resultPtr == IntPtr.Zero) return null;", csOutput);
        Assert.Contains("Encoding.UTF8.GetString", csOutput);
        Assert.Contains("PInvoke_SBW_Free", csOutput);
        // Swift wrapper returns nullable pointer and guards for nil
        Assert.Contains("UnsafeMutableRawPointer?", swiftOutput);
        Assert.Contains("guard let result: String", swiftOutput);
        Assert.Contains("else { return nil }", swiftOutput);
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
    public void Emit_StaticMethodWithEnumParam_RawRepresentable_UsesTagSwitch()
    {
        // RawRepresentable enum params must also use tag switch (not rawValue initializer)
        // because C# enum values are sequential tags — ABI JSON lacks raw values, so
        // rawValue: would fail for enums with non-sequential raw values (e.g., OSStatus codes).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "Int32";
        enumDecl.Cases.Add(CreateCase("active"));
        enumDecl.Cases.Add(CreateCase("inactive"));
        enumDecl.Methods.Add(CreateRawValueInitializer(enumDecl, moduleDecl));
        enumDecl.Methods.Add(CreateStaticMethodWithEnumParam("isValid", enumDecl, moduleDecl));

        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Should NOT use rawValue initializer (C# tags don't match Swift raw values)
        Assert.DoesNotContain("rawValue:", swiftOutput);
        // Should use switch-based conversion
        Assert.Contains("switch", swiftOutput);
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

    private static PropertyDecl CreateInstanceOptionalStringProperty(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        var optStringSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = optStringSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s10TestModule{enumDecl.Name.Length}{enumDecl.Name}O{name}SSSgvg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new() { SwiftTypeSpec = optStringSpec, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
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

    #region @_cdecl Enum Case Factory ABI Tests

    [Fact]
    public void Emit_CdeclEnumCaseWithString_UsesUtf8PtrLen()
    {
        // @_cdecl enum case factory with string associated value must use UTF-8 pointer + length
        // in P/Invoke (NativeAOT-safe), not SwiftString.Buffer (struct marshalling fails on NativeAOT)
        var typeDatabase = CreateTypeDatabaseWithString();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Message", moduleDecl, isFrozen: false);

        var textCase = CreateCase("text");
        textCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        enumDecl.Cases.Add(textCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# P/Invoke should use IntPtr + int (UTF-8 pointer + length)
        Assert.Contains("IntPtr valueUtf8Ptr", csOutput);
        Assert.Contains("int valueUtf8Len", csOutput);
        Assert.DoesNotContain("Swift.SwiftString.Buffer", csOutput);
        // C# body should encode to UTF-8 bytes
        Assert.Contains("Encoding.UTF8.GetBytes", csOutput);
        // C# body should use fixed block for pinning
        Assert.Contains("fixed (byte*", csOutput);
        // Swift side should use UTF-8 reconstruction
        Assert.Contains("Utf8Ptr: UnsafePointer<UInt8>", swiftOutput);
        Assert.Contains("Utf8Len: Int", swiftOutput);
        Assert.Contains("UnsafeBufferPointer", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseWithExistential_UsesIntPtrContainer()
    {
        // @_cdecl enum case factory with existential associated value must use
        // IntPtr in P/Invoke (pointer to pinned container), NOT by-value or ref
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Event", moduleDecl, isFrozen: false);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageProcessing") }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // C# P/Invoke should use IntPtr (pointer to heap-allocated container)
        Assert.Contains("IntPtr value", csOutput);
        // C# body should extract container and heap-allocate for NativeAOT safety
        Assert.Contains("Container", csOutput);
        Assert.Contains("NativeMemory.Alloc", csOutput);
        Assert.Contains("Unsafe.Copy", csOutput);
        // The heap container is cleaned up via DestroyAndFreeExistential, which runs the existential
        // value-witness destroy (only when the owns-bit says a value was boxed at +1) and then frees
        // the heap. This replaced the unconditional inline NativeMemory.Free, which leaked the boxed
        // payload (swift_allocBox) for value conformers.
        Assert.Contains("DestroyAndFreeExistential", csOutput);
        // The owns-bit must be threaded out of GetOrCreate so the finally can decide whether to destroy.
        Assert.Contains("Owns", csOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseWithPrimitive_UsesCdeclCallingConvention()
    {
        // @_cdecl enum case factory with primitive value should use CallConvCdecl
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);

        var activeCase = CreateCase("active");
        activeCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(activeCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# should use CallConvCdecl, not CallConvSwift
        Assert.Contains("CallConvCdecl", csOutput);
        // P/Invoke should NOT have SwiftIndirectResult
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);
        // Should have IntPtr resultPtr as last param
        Assert.Contains("IntPtr resultPtr", csOutput);
        // Swift wrapper should have @_cdecl
        Assert.Contains("@_cdecl", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseWithTuple_UsesPointerTransport()
    {
        // @_cdecl enum case factory with tuple associated value must use IntPtr
        // (pointer to tuple memory) in P/Invoke, NOT the by-value tuple type.
        // Swift wrapper receives UnsafeRawPointer and does .load(as: TupleType.self).
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Coordinate", moduleDecl, isFrozen: false);

        var pointCase = CreateCase("point");
        pointCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        }));
        enumDecl.Cases.Add(pointCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // C# P/Invoke should use IntPtr for tuple (pointer transport), not ValueTuple
        Assert.Contains("IntPtr value, IntPtr resultPtr", csOutput);
        Assert.DoesNotContain("ValueTuple<long, bool> value", csOutput);
        // C# body should store tuple in a local and take its address
        Assert.Contains("Tuple = value;", csOutput);
        Assert.Contains("(&valueTuple)", csOutput);
        // Should use CallConvCdecl
        Assert.Contains("CallConvCdecl", csOutput);
        // Swift side should receive UnsafeRawPointer and load tuple
        Assert.Contains("UnsafeRawPointer", swiftOutput);
        Assert.Contains(".assumingMemoryBound(to:", swiftOutput);
        Assert.Contains("@_cdecl", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseWithProjectedTuple_UsesDirectPInvoke()
    {
        // Tuple with string element: C# IntPtr (8 bytes) vs Swift String (16 bytes).
        // Memory layouts don't match, so @_cdecl pointer transport would give Swift
        // the wrong data. The case factory uses direct P/Invoke (SwiftIndirectResult pattern).
        var typeDatabase = CreateTypeDatabaseWithString();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Message", moduleDecl, isFrozen: false);

        var taggedCase = CreateCase("tagged");
        taggedCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int")
        }));
        enumDecl.Cases.Add(taggedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // The Tagged case factory uses CallConvCdecl (SwiftIndirectResult pattern)
        Assert.Contains("SwiftIndirectResult", csOutput);
        Assert.Contains("CallConvCdecl", csOutput);
        // No @_cdecl case factory wrapper should be emitted on the Swift side
        Assert.DoesNotContain("SBW_TestModule_Message_tagged", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseWithExistentialTuple_DoesNotEmitCdeclWrapper()
    {
        // Tuple with existential element: ExistentialContainer layout may not match
        // Swift's tuple element layout. The @_cdecl gate rejects this, so no wrapper
        // is emitted. (The case factory may also be skipped by other type resolution gates.)
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Event", moduleDecl, isFrozen: false);

        var failedCase = CreateCase("failed");
        failedCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.ImageProcessing") }),
            new NamedTypeSpec("Swift.Int")
        }));
        enumDecl.Cases.Add(failedCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // No @_cdecl case factory wrapper should be emitted for this case
        Assert.DoesNotContain("SBW_TestModule_Event_failed", swiftOutput);
        // The _sbw_case_ prefix is used for case factory @_cdecl wrappers
        Assert.DoesNotContain("_sbw_case_failed", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseFactory_UsesWrapperLibraryPath()
    {
        // Regression test: @_cdecl enum case factory P/Invoke must target the wrapper
        // library (AsyncLibraryName), not the original library. The SBW_ symbol is
        // compiled into the wrapper xcframework, not the original framework.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: false);

        var activeCase = CreateCase("active");
        activeCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(activeCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // The case factory P/Invoke must use the wrapper library
        Assert.Contains("[LibraryImport(\"TestModuleSwiftBindings\", EntryPoint = \"SBW_TestModule_Status_active_", csOutput);
        // Must NOT use the original module library for SBW_ symbols
        Assert.DoesNotContain("[LibraryImport(\"/tmp/TestModule.dylib\", EntryPoint = \"SBW_", csOutput);
    }

    [Fact]
    public void Emit_CdeclEnumCaseFactory_UnlabeledAssocValue_NoColonLabel()
    {
        // Regression test: unlabeled associated values must produce no argument label
        // in the Swift wrapper, not "(: value0)" which is invalid Swift syntax.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Error", moduleDecl, isFrozen: false);

        var errorCase = CreateCase("statusCodeUnacceptable");
        // Unlabeled: TypeLabel is null
        errorCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int") { TypeLabel = null });
        enumDecl.Cases.Add(errorCase);
        enumDecl.Cases.Add(CreateCase("none"));

        var (_, swiftOutput) = EmitEnum(enumDecl, typeDatabase);

        // Must NOT contain "(: " — that's the bug pattern from unlabeled values
        Assert.DoesNotContain("(: ", swiftOutput);
        // Must contain the correct unlabeled construction
        Assert.Contains("statusCodeUnacceptable(value0)", swiftOutput);
    }

    [Fact]
    public void Emit_XcframeworkEnum_MetadataFallback_EmitsTryCatchWithDylibFallback()
    {
        // Regression test: when the wrapper DLL is unavailable (e.g., wrapper compilation
        // failed for a sub-module), GetTypeMetadata() must fall back to the dylib's
        // CallConvSwift metadata accessor instead of throwing TypeInitializationException.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("CardScanSheetResult", moduleDecl, isFrozen: false);

        var completedCase = CreateCase("completed");
        completedCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(completedCase);
        enumDecl.Cases.Add(CreateCase("cancelled"));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Must have try/catch fallback pattern
        Assert.Contains("try", csOutput);
        Assert.Contains("return PInvoke_getMetadata();", csOutput);
        Assert.Contains("catch (System.DllNotFoundException)", csOutput);
        Assert.Contains("catch (System.EntryPointNotFoundException)", csOutput);
        Assert.Contains("return PInvoke_getMetadata_fallback();", csOutput);

        // Primary P/Invoke targets wrapper DLL with Cdecl calling convention
        Assert.Contains("LibraryImport(\"TestModuleSwiftBindings\"", csOutput);
        Assert.Contains("PInvoke_getMetadata", csOutput);

        // Fallback P/Invoke targets the original dylib
        Assert.Contains("PInvoke_getMetadata_fallback", csOutput);
        Assert.Contains("LibraryImport(\"/tmp/TestModule.dylib\"", csOutput);
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol()
    {
        var typeDatabase = CreateTypeDatabaseWithString();

        var testModule = new ModuleTypeDatabase("TestModule_protocols", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IImageProcessing"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
                MetadataAccessor = "$s10TestModule15ImageProcessingMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion

    #region Keyword Label Tests (S1 — verbatim prefix in compound names)

    [Fact]
    public void Emit_EnumCaseWithKeywordLabel_StringParam_UsesValidCompoundNames()
    {
        // S1 bug: enum case with keyword label "in" → SanitizeParameterName returns "@in"
        // Compound variable names were emitted as "__@in" (invalid) instead of "__in" (valid).
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("FilterScope", moduleDecl, isFrozen: false);

        var includeCase = CreateCase("include");
        var stringType = new NamedTypeSpec("Swift.String") { TypeLabel = "in" };
        includeCase.AssociatedValues.Add(stringType);
        enumDecl.Cases.Add(includeCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Method signature should use @in (valid verbatim identifier)
        Assert.Contains("string @in", csOutput);
        // Compound variable name must NOT contain __@in (@ after other chars is invalid)
        Assert.DoesNotContain("__@in", csOutput);
        // Compound variable name should use __in (stripped prefix)
        Assert.Contains("__in", csOutput);
        Assert.Contains("new SwiftString(@in)", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithKeywordLabel_IntParam_UsesValidCompoundNames()
    {
        // Same S1 bug with non-string keyword label — no compound variable needed,
        // but verify the parameter name in the signature is correct (@for).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("FilterScope", moduleDecl, isFrozen: false);

        var excludeCase = CreateCase("exclude");
        var intType = new NamedTypeSpec("Swift.Int") { TypeLabel = "for" };
        excludeCase.AssociatedValues.Add(intType);
        enumDecl.Cases.Add(excludeCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Parameter in method signature should be escaped: long @for
        Assert.Contains("@for", csOutput);
        // No compound variable names should have __@for
        Assert.DoesNotContain("__@for", csOutput);
    }

    [Fact]
    public void Emit_EnumCaseWithMultipleKeywordLabels_UsesValidCompoundNames()
    {
        // Multiple keyword-labeled associated values (matches FilterScope.swift fixture)
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("FilterScope", moduleDecl, isFrozen: false);

        var customCase = CreateCase("custom");
        var operatorType = new NamedTypeSpec("Swift.String") { TypeLabel = "operator" };
        var classType = new NamedTypeSpec("Swift.String") { TypeLabel = "class" };
        customCase.AssociatedValues.Add(operatorType);
        customCase.AssociatedValues.Add(classType);
        enumDecl.Cases.Add(customCase);

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Both keyword params should use valid verbatim identifiers in the signature
        Assert.Contains("@operator", csOutput);
        Assert.Contains("@class", csOutput);
        // Neither should produce invalid compound names
        Assert.DoesNotContain("__@operator", csOutput);
        Assert.DoesNotContain("__@class", csOutput);
        // Valid compound names should be present
        Assert.Contains("__operator", csOutput);
        Assert.Contains("__class", csOutput);
    }

    #endregion

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

    [Fact]
    public void Emit_StringRawValueEnum_UsesCustomRawValues()
    {
        // When EnumCaseDecl.RawValue is set, ToRawValue/FromRawValue use actual raw values
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("LogLevel", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        var debugCase = CreateCase("debug");
        debugCase.RawValue = "[DEBUG]";
        var infoCase = CreateCase("info");
        infoCase.RawValue = "[INFO]";
        enumDecl.Cases.Add(debugCase);
        enumDecl.Cases.Add(infoCase);
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // ToRawValue must use custom raw values, not case names
        Assert.Contains("\"[DEBUG]\"", csOutput);
        Assert.Contains("\"[INFO]\"", csOutput);
        Assert.DoesNotContain("=> \"debug\"", csOutput);
        Assert.DoesNotContain("=> \"info\"", csOutput);
    }

    [Fact]
    public void Emit_StringRawValueEnum_FallsBackToCaseNameWhenNoRawValue()
    {
        // When EnumCaseDecl.RawValue is null, case name is used (Swift default)
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Status", moduleDecl, isFrozen: true);
        enumDecl.RawValueTypeName = "String";
        enumDecl.Cases.Add(CreateCase("active"));  // No RawValue set
        enumDecl.Cases.Add(CreateCase("inactive")); // No RawValue set
        enumDecl.Methods.Add(CreateStringRawValueInitializer(enumDecl, moduleDecl));

        var (csOutput, _) = EmitEnum(enumDecl, typeDatabase);

        // Must fall back to case names
        Assert.Contains("\"active\"", csOutput);
        Assert.Contains("\"inactive\"", csOutput);
    }
}
