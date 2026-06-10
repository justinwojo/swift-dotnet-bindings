// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ObjCOverridePropertyWrapperEmitter, which generates @_silgen_name Swift wrappers
/// for property accessors that override ObjC-inherited properties (missing Tj dispatch thunks).
/// </summary>
public class ObjCOverridePropertyWrapperEmitterTests
{
    // ==================== ShouldEmitWrapper Tests ====================

    [Fact]
    public void ShouldEmitWrapper_ObjCRootedOverride_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: true);
        property.IsOverride = true;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.True(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NotObjCRooted_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyModule");
        typeDb.AsyncLibraryName = "MyModuleSwiftBindings";

        var classDecl = CreateClassDecl("MyClass", moduleDecl, isObjCRooted: false);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int",
            hasGetter: true, hasSetter: true);
        property.IsOverride = true;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NotOverride_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        var classDecl = CreateClassDecl("AnimationView", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "loopMode", "Swift.Int",
            hasGetter: true, hasSetter: true);
        // IsOverride defaults to false

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NoWrapperLibrary_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        // AsyncLibraryName not set — no wrapper library

        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: true);
        property.IsOverride = true;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_StaticProperty_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: false);
        property.IsOverride = true;
        property.IsStatic = true;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClass_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        var classDecl = CreateClassDecl("GenericView", moduleDecl, isObjCRooted: true);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: true);
        property.IsOverride = true;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalMetatypeProperty_ReturnsFalse()
    {
        // Bug-2 pin: ObjC override wrapper path is independent of PropertyWrapperEmitter.
        // An ObjC-rooted override with Optional<AnyClass.Type> must be rejected here
        // too — otherwise ExistentialBypassEmitter renders the property type as a bare
        // "Type" token in the @_silgen_name accessor wrapper.
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        var classDecl = CreateClassDecl("AnimationView", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "registeredClass", "Swift.Int",
            hasGetter: true, hasSetter: true);
        property.IsOverride = true;

        // Replace the property type with Optional<AnyClass.Type>
        var optionalMetatype = new NamedTypeSpec("Swift.Optional");
        optionalMetatype.GenericParameters.Add(new NamedTypeSpec("AnyClass.Type"));
        property.SwiftTypeSpec = optionalMetatype;

        var env = CreateAccessorEnv(property.Accessors[0], typeDb);
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(property, env));
    }

    [Fact]
    public void ShouldEmitWrapper_PropertyInResolvedAncestor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimation");
        typeDb.AsyncLibraryName = "VectorAnimationSwiftBindings";

        // Create parent class with the property (resolved ancestor)
        var parentClass = CreateClassDecl("BaseView", moduleDecl, isObjCRooted: true);
        var parentProperty = CreateEmittablePropertyDecl(parentClass, moduleDecl, "animation", "Swift.Int",
            hasGetter: true, hasSetter: true);
        parentProperty.WasEmitted = true; // Mark the PropertyDecl as emitted

        // Create child class that overrides the property
        var childClass = CreateClassDecl("AnimationView", moduleDecl, isObjCRooted: true);
        childClass.ResolvedSuperclass = parentClass;
        var childProperty = CreateEmittablePropertyDecl(childClass, moduleDecl, "animation", "Swift.Int",
            hasGetter: true, hasSetter: true);
        childProperty.IsOverride = true;

        var env = CreateAccessorEnv(childProperty.Accessors[0], typeDb);
        // Property IS found in resolved ancestor → no wrapper needed
        Assert.False(ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper(childProperty, env));
    }

    // ==================== Symbol Name Tests ====================

    [Fact]
    public void GetAccessorSymbolName_Getter_HasCorrectFormat()
    {
        var symbol = ObjCOverridePropertyWrapperEmitter.GetAccessorSymbolName(
            "VectorAnimation", "AnimationViewBase", "contentMode", isGetter: true);
        Assert.Equal("SBSW_Get_VectorAnimation_AnimationViewBase_contentMode", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_Setter_HasCorrectFormat()
    {
        var symbol = ObjCOverridePropertyWrapperEmitter.GetAccessorSymbolName(
            "VectorAnimation", "AnimationViewBase", "contentMode", isGetter: false);
        Assert.Equal("SBSW_Set_VectorAnimation_AnimationViewBase_contentMode", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_NestedType_DotReplacedWithUnderscore()
    {
        var symbol = ObjCOverridePropertyWrapperEmitter.GetAccessorSymbolName(
            "MyModule", "Outer.Inner", "prop", isGetter: true);
        Assert.Equal("SBSW_Get_MyModule_Outer_Inner_prop", symbol);
    }

    // ==================== Swift Getter Wrapper Tests ====================

    [Fact]
    public void EmitSwiftGetterWrapper_EmitsCorrectSwiftCode()
    {
        var (moduleDecl, _) = CreateTestEnvironment("VectorAnimation");
        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: false);

        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ObjCOverridePropertyWrapperEmitter.EmitSwiftGetterWrapper(
            writer, property, "SBW_Get_VectorAnimation_AnimationViewBase_contentMode", ctx);

        var output = sw.ToString();
        Assert.Contains("@_silgen_name(\"SBW_Get_VectorAnimation_AnimationViewBase_contentMode\")", output);
        Assert.Contains("_ self_: VectorAnimation.AnimationViewBase", output);
        // RenderSwiftTypeSpec strips module prefix: "Swift.Int" → "Int"
        Assert.Contains("-> Int", output);
        Assert.Contains("return self_.contentMode", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_EmitsCorrectSwiftCode()
    {
        var (moduleDecl, _) = CreateTestEnvironment("VectorAnimation");
        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: false, hasSetter: true);

        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ObjCOverridePropertyWrapperEmitter.EmitSwiftSetterWrapper(
            writer, property, "SBW_Set_VectorAnimation_AnimationViewBase_contentMode", ctx);

        var output = sw.ToString();
        Assert.Contains("@_silgen_name(\"SBW_Set_VectorAnimation_AnimationViewBase_contentMode\")", output);
        Assert.Contains("_ newValue: Int", output);
        Assert.Contains("_ self_: VectorAnimation.AnimationViewBase", output);
        Assert.Contains("self_.contentMode = newValue", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_MainActorIsolated_EmitsAnnotation()
    {
        var (moduleDecl, _) = CreateTestEnvironment("VectorAnimation");
        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        classDecl.IsMainActorIsolated = true;
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: false);

        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        ObjCOverridePropertyWrapperEmitter.EmitSwiftGetterWrapper(
            writer, property, "SBW_Get_VectorAnimation_AnimationViewBase_contentMode", ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_DuplicateSymbol_SkipsSecondEmission()
    {
        var (moduleDecl, _) = CreateTestEnvironment("VectorAnimation");
        var classDecl = CreateClassDecl("AnimationViewBase", moduleDecl, isObjCRooted: true);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "contentMode", "Swift.Int",
            hasGetter: true, hasSetter: false);

        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);

        // Emit first time
        ObjCOverridePropertyWrapperEmitter.EmitSwiftGetterWrapper(
            writer, property, "SBW_Get_VectorAnimation_AnimationViewBase_contentMode", ctx);

        var firstOutput = sw.ToString();

        // Emit second time with same symbol
        ObjCOverridePropertyWrapperEmitter.EmitSwiftGetterWrapper(
            writer, property, "SBW_Get_VectorAnimation_AnimationViewBase_contentMode", ctx);

        var secondOutput = sw.ToString();
        // The second emission should not add anything
        Assert.Equal(firstOutput, secondOutput);
    }

    // ==================== ModuleEmissionContext Tracking Tests ====================

    [Fact]
    public void ModuleEmissionContext_TracksObjCPropertyWrapperSymbols()
    {
        var ctx = new ModuleEmissionContext();
        Assert.False(ctx.HasObjCPropertyWrapperSymbol("SBW_Get_Test"));
        Assert.True(ctx.TryAddObjCPropertyWrapperSymbol("SBW_Get_Test"));
        Assert.True(ctx.HasObjCPropertyWrapperSymbol("SBW_Get_Test"));
        Assert.False(ctx.TryAddObjCPropertyWrapperSymbol("SBW_Get_Test")); // Already added
    }

    // ==================== Test Helpers ====================

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string moduleName)
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase(moduleName, $"/path/to/{moduleName}");
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return (moduleDecl, typeDb);
    }

    private static ClassDecl CreateClassDecl(string className, ModuleDecl moduleDecl, bool isObjCRooted = false)
    {
        var classDecl = new ClassDecl
        {
            Name = className,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{className}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{className.Length}{className}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsObjCRooted = isObjCRooted,
            SuperclassUsr = isObjCRooted ? "c:objc(cs)NSObject" : null,
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static PropertyDecl CreateEmittablePropertyDecl(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string name,
        string propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        var accessors = new List<AccessorDecl>();
        var property = new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(propertyType),
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s{name}g",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(propertyType),
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = classDecl,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            });
        }

        if (hasSetter)
        {
            accessors.Add(new SetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Set",
                    MangledName = $"$s{name}s",
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
                            ModuleDecl = moduleDecl
                        },
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(propertyType),
                            Name = "value",
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = classDecl,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            });
        }

        classDecl.Properties.Add(property);
        return property;
    }

    private static MethodEnvironment CreateAccessorEnv(AccessorDecl accessor, TypeDatabase typeDb)
    {
        return new MethodEnvironment(accessor.Method, typeDb);
    }
}
