// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class OperatorHandlerOutputTests
{
    [Fact]
    public void EmitOperator_BinaryEquality_EmitsWrapperAndPInvoke()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");

        var output = EmitOperator(op, typeDatabase);

        Assert.Contains("operator ==", output);
        Assert.Contains("left", output);
        Assert.Contains("right", output);
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\", EntryPoint =", output);
        Assert.Contains("PInvoke_op_Equality", output);
    }

    [Fact]
    public void ValidateAndEmitPairs_WithOnlyEquality_SynthesizesInequality()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        handler.ValidateAndEmitPairs(csWriter, new List<OperatorDecl> { op }, "Point", new HashSet<string> { "==" });

        var output = writer.ToString();
        Assert.Contains("public static bool operator !=(Point left, Point right)", output);
        Assert.Contains("return !(left == right);", output);
    }

    [Fact]
    public void EmitOperator_UnsupportedSymbol_EmitsNothing()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("??", parentType, moduleDecl, "Swift.Bool");

        var output = EmitOperator(op, typeDatabase);

        Assert.Equal(string.Empty, output);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
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

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);
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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
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
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static OperatorDecl CreateBinaryOperator(string symbol, StructDecl parentType, ModuleDecl moduleDecl, string returnType)
    {
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = "$s10TestModule5PointV2eeoiySbAC_ACtFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec(returnType),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "left",
                    PrivateName = "left",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "right",
                    PrivateName = "right",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentType,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = true,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };
    }

    [Fact]
    public void EmitOperator_NonFrozenClassReturn_EmitsIndirectResult()
    {
        // Bug #1: Operators returning non-frozen class types need SwiftIndirectResult allocation
        var typeDatabase = CreateTypeDatabaseWithType("TestModule", "BigNum", TypeRecordFlags.None, TypeRecordKind.Struct);
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentType = new StructDecl
        {
            Name = "BigNum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BigNum"),
            MangledName = "$s10TestModule6BigNumVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false, // Non-frozen
            MetadataAccessor = "$s10TestModule6BigNumVMa"
        };

        var op = CreateBinaryOperator("/", parentType, moduleDecl, "TestModule.BigNum",
            "TestModule.BigNum", "TestModule.BigNum");

        var output = EmitOperator(op, typeDatabase);

        // Should allocate memory and create SwiftIndirectResult
        Assert.Contains("TypeMetadata.GetTypeMetadataOrThrow<TestModule.BigNum>()", output);
        Assert.Contains("NativeMemory.Alloc((nuint)returnMetadata.Size)", output);
        Assert.Contains("new SwiftIndirectResult(_cdeclBuf)", output);
        // Should call P/Invoke without return prefix (void return via indirect result)
        Assert.Contains("PInvoke_op_Division(swiftIndirectResult", output);
        // Should marshal the result back
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.BigNum>(new IntPtr(swiftIndirectResult.Value))", output);
    }

    [Fact]
    public void EmitOperator_ShiftWithGenericOperand_SkipsOperator()
    {
        // Bug #4: Shift operators with generic second operand can't be expressed in C#
        // Operator is skipped before type resolution, so BigNum doesn't need to be in the type database
        var typeDatabase = CreateTypeDatabaseWithType("TestModule", "BigNum", TypeRecordFlags.None, TypeRecordKind.Struct);
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentType = new StructDecl
        {
            Name = "BigNum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BigNum"),
            MangledName = "$s10TestModule6BigNumVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule6BigNumVMa"
        };

        // Create a shift operator with a generic second operand
        var method = new MethodDecl
        {
            Name = ">>",
            MangledName = "$s10TestModule6BigNumV2ggoiyA2C_xtSjRzlFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.BigNum"), Name = string.Empty, PrivateName = string.Empty, IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.BigNum"), Name = "arg0", PrivateName = "lhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("τ_0_0"), Name = "arg1", PrivateName = "rhs", IsInOut = false, IsGeneric = true, ParentDecl = parentType, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl> { new("τ_0_0", "τ_0_0", new(), new()) },
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var op = new OperatorDecl
        {
            Name = ">>",
            OperatorSymbol = ">>",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };

        var output = EmitOperator(op, typeDatabase);

        // Should emit nothing (operator skipped)
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void EmitOperator_GenericTypeOperator_RemapsT1ToT0()
    {
        // Bug #10: Operators on generic types use T1 (method-own) instead of T0 (type-level)
        var typeDatabase = CreateTypeDatabaseWithType("TestModule", "Container", TypeRecordFlags.None, TypeRecordKind.Struct);
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create a generic parent type with one type parameter
        var parentType = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$s10TestModule9ContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { new("τ_0_0", "τ_0_0", new(), new()) },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule9ContainerVMa"
        };

        // Create an == operator with method-own generic τ_1_0 (which maps to T1 by default)
        // The parameter types use NamedTypeSpec with generic parameters referencing τ_1_0
        var method = new MethodDecl
        {
            Name = "==",
            MangledName = "$s10TestModule9ContainerV2eeoiySbACyxG_AEtSQRzlFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), Name = string.Empty, PrivateName = string.Empty, IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("τ_1_0")),
                    Name = "arg0", PrivateName = "lhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Container", new NamedTypeSpec("τ_1_0")),
                    Name = "arg1", PrivateName = "rhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl
                }
            },
            // Method has both type-level (τ_0_0) and method-own (τ_1_0) generic params
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "τ_0_0", new(), new()),
                new("τ_1_0", "τ_1_0", new(), new())
            },
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var op = new OperatorDecl
        {
            Name = "==",
            OperatorSymbol = "==",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };

        // Use PInvokeHelperContext since generic types need it
        var pinvokeHelperContext = new PInvokeHelperContext("Container",
            new List<string> { "T0", "T1" });

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());
        var result = handler.EmitOperator(csWriter, op, typeDatabase, pinvokeHelperContext);

        var output = writer.ToString();

        // G4 fix: operators on generic types requiring buffer marshalling are now skipped
        Assert.False(result);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void EmitOperator_NestedType_UsesOriginalNameFromTypeDatabase()
    {
        // TypeDatabase is no longer modified by rename logic — operators use original type names
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
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

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Register with original CSharpTypeName (TypeDatabase is no longer mutated by rename logic)
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ContentType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parent.ContentType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ContentType"),
                MetadataAccessor = "$s10TestModule11ContentTypeVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = new StructDecl
        {
            Name = "ContentType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ContentType"),
            MangledName = "$s10TestModule11ContentTypeVN",
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
            MetadataAccessor = "$s10TestModule11ContentTypeVMa"
        };

        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool",
            "TestModule.ContentType", "TestModule.ContentType");

        var output = EmitOperator(op, typeDatabase);

        // Operator parameter types should reference the original name (ContentType)
        Assert.Contains("ContentType", output);
    }

    [Fact]
    public void ValidateAndEmitPairs_NestedType_SynthesizedOperatorUsesOriginalName()
    {
        // Synthesized paired operators (e.g., != from ==) use the original type name
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("ContentType", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        // Pass the original type name (TypeDatabase is no longer mutated)
        handler.ValidateAndEmitPairs(csWriter, new List<OperatorDecl> { op }, "ContentType", new HashSet<string> { "==" });

        var output = writer.ToString();
        Assert.Contains("public static bool operator !=(ContentType left, ContentType right)", output);
    }

    private static OperatorDecl CreateBinaryOperator(string symbol, StructDecl parentType,
        ModuleDecl moduleDecl, string returnType, string leftType, string rightType)
    {
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = "$s10TestModule_operator",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec(returnType), Name = string.Empty, PrivateName = string.Empty, IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec(leftType), Name = "arg0", PrivateName = "lhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec(rightType), Name = "arg1", PrivateName = "rhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };
    }

    [Fact]
    public void EmitOperator_IndirectResult_EmitsUnsafeBlock()
    {
        // Regression: After WU5 removed unsafe from class declarations, operators with
        // indirect result (non-frozen return types) need their own unsafe { } block.
        // Without this, pointer operations in the operator body produce CS0214.
        var typeDatabase = CreateTypeDatabaseWithType("TestModule", "BigNum", TypeRecordFlags.None, TypeRecordKind.Struct);
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentType = new StructDecl
        {
            Name = "BigNum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BigNum"),
            MangledName = "$s10TestModule6BigNumVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule6BigNumVMa"
        };

        var op = CreateBinaryOperator("+", parentType, moduleDecl, "TestModule.BigNum",
            "TestModule.BigNum", "TestModule.BigNum");

        var output = EmitOperator(op, typeDatabase);

        Assert.Contains("unsafe {", output);
    }

    [Fact]
    public void EmitOperator_IndirectResult_EmitsTryCatchForMemoryCleanup()
    {
        // Issue P: Operators with indirect result (non-frozen return) must wrap
        // NativeMemory.Alloc in try/catch to free the buffer on exception.
        var typeDatabase = CreateTypeDatabaseWithType("TestModule", "BigNum", TypeRecordFlags.None, TypeRecordKind.Struct);
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentType = new StructDecl
        {
            Name = "BigNum",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BigNum"),
            MangledName = "$s10TestModule6BigNumVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule6BigNumVMa"
        };

        var op = CreateBinaryOperator("+", parentType, moduleDecl, "TestModule.BigNum",
            "TestModule.BigNum", "TestModule.BigNum");

        var output = EmitOperator(op, typeDatabase);

        // Must have try/catch guarding the NativeMemory.Alloc.
        // catch-only (not finally) because NewFromPayload takes ownership of the buffer
        // for non-frozen types — using finally would double-free.
        Assert.Contains("try", output);
        Assert.Contains("catch { NativeMemory.Free(_cdeclBuf); throw; }", output);
        Assert.DoesNotContain("finally", output);
    }

    [Fact]
    public void EmitOperator_FrozenReturn_NoUnsafeBlock()
    {
        // Frozen struct return types don't need indirect result, so no unsafe block needed.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateStructDecl("Point", moduleDecl);
        var op = CreateBinaryOperator("==", parentType, moduleDecl, "Swift.Bool");

        var output = EmitOperator(op, typeDatabase);

        Assert.DoesNotContain("unsafe {", output);
    }

    private static TypeDatabase CreateTypeDatabaseWithType(string moduleName, string typeName,
        TypeRecordFlags flags, TypeRecordKind kind)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
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

        var module = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{typeName}"),
                MetadataAccessor = $"$s10{moduleName}{typeName.Length}{typeName}VMa",
                Flags = flags,
                Kind = kind
            });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    private static string EmitOperator(OperatorDecl op, TypeDatabase typeDatabase)
    {
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        var handler = new OperatorHandler(new NullLogger<OperatorHandler>());
        handler.EmitOperator(csWriter, op, typeDatabase);
        return writer.ToString();
    }

    [Fact]
    public void EmitOperator_ClassParentNoCdeclWrapper_UsesCallConvSwift()
    {
        // A MusicKit AnyMusicProperty.== pattern: when the operator's parent is a class, ShouldEmitOperatorWrapper returns false
        // (line 848 of OperatorHandler.cs: only frozen structs get the @_cdecl operator wrapper),
        // so the EntryPoint stays the original Swift-mangled `$s…` symbol. That symbol uses
        // Swift's calling convention; pairing it with CallConvCdecl reads garbage from the
        // cdecl-return register instead of Swift's. The fix in OperatorHandler.EmitOperatorPInvoke
        // makes the CC conditional on usesCdeclWrapper.
        var typeDatabase = CreateTypeDatabaseWithClass("TestModule", "AnyMusicProperty");
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentType = new ClassDecl
        {
            Name = "AnyMusicProperty",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AnyMusicProperty"),
            MangledName = "$s10TestModule16AnyMusicPropertyCN",
            IsFinal = false,
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

        var method = new MethodDecl
        {
            Name = "==",
            // Real-world MusicKit AnyMusicProperty == mangled symbol shape (Swift CC).
            MangledName = "$s10TestModule16AnyMusicPropertyC2eeoiySbAC_ACtFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), Name = string.Empty, PrivateName = string.Empty, IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.AnyMusicProperty"), Name = "lhs", PrivateName = "lhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { SwiftTypeSpec = new NamedTypeSpec("TestModule.AnyMusicProperty"), Name = "rhs", PrivateName = "rhs", IsInOut = false, IsGeneric = false, ParentDecl = parentType, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var op = new OperatorDecl
        {
            Name = "==",
            OperatorSymbol = "==",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl
        };

        var output = EmitOperator(op, typeDatabase);

        // The P/Invoke must keep the Swift-mangled EntryPoint AND advertise CallConvSwift.
        // The previous bug paired the same EntryPoint with CallConvCdecl, which is the
        // mismatch that returned bogus bool values from a register Swift never populated.
        Assert.Contains("EntryPoint = \"$s10TestModule16AnyMusicPropertyC2eeoiySbAC_ACtFZ\"", output);
        Assert.Contains("CallConvSwift", output);
        Assert.DoesNotContain("CallConvCdecl", output);
    }

    private static TypeDatabase CreateTypeDatabaseWithClass(string moduleName, string typeName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
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

        var module = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{typeName}"),
                MetadataAccessor = $"$s10{moduleName}{typeName.Length}{typeName}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    // NOTE: the PInvokeHelperContext branch in OperatorHandler.EmitOperatorPInvoke
    // (OperatorHandler.cs:638) carries the same CallingConvention selection as the
    // direct path covered by EmitOperator_ClassParentNoCdeclWrapper_UsesCallConvSwift.
    // An end-to-end regression test for that branch is not viable today: the G4 skip
    // gate at OperatorHandler.cs:208–224 fires for every generic-type operator whose
    // P/Invoke parameter naming differs from the wrapper signature (the buffer-
    // marshalling preamble case), so generic-class operators on Bool returns are
    // skipped before reaching the helper-collect line. The fix at line 654
    // ("CallingConvention = callingConvention") is structural symmetry with the
    // direct branch and is documented inline at lines 640–646; if a future change
    // loosens the G4 gate, the matching test belongs alongside whichever shape it
    // newly admits.
}
