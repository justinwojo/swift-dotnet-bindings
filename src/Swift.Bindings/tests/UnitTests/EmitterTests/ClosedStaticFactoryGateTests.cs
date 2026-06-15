// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ClosedStaticFactoryGate"/> — the predicate that admits
/// the WeatherKit-shape static accessor where a generic parent
/// <c>Foo&lt;T&gt;</c> exposes <c>static var preset: Foo&lt;ConcreteT&gt;</c>.
/// The 5 gating conditions live in the gate; this suite pins each one.
/// </summary>
public class ClosedStaticFactoryGateTests
{
    private const string TestModule = "TestModule";

    [Fact]
    public void IsClosedStaticFactoryAccessor_HappyPath_ReturnsTrue()
    {
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);

        // SwiftABIParser copies the parent's GenericParameters onto an accessor
        // that has no GenericSig of its own (the closed-static-factory shape).
        // Confirm the gate admits the accessor even with MethodDecl.IsGeneric == true,
        // which is the production state seen by downstream emission code.
        Assert.True(accessor.IsGeneric);
        Assert.True(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_InstanceAccessor_ReturnsFalse()
    {
        // Condition #1: accessor must be Static.
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);
        accessor.MethodType = MethodType.Instance;

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_NonGenericParent_ReturnsFalse()
    {
        // Condition #2: parent must be generic. Without the open T, this isn't
        // the WeatherKit shape — it's a plain static accessor and goes through
        // the normal emission path.
        var parent = CreateNonGenericStructDecl("Query");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_HasValueParameters_ReturnsFalse()
    {
        // Condition #3: getter-only — CSSignature.Count must be 1 (return slot).
        // Setters/multi-param accessors carry the parent T in their value param
        // and need full metadata threading.
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);
        accessor.CSSignature.Add(new ArgumentDecl
        {
            Name = "newValue",
            PrivateName = "newValue",
            SwiftTypeSpec = new NamedTypeSpec($"{TestModule}.StatA"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        });

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ReturnTypeIsBareGenericParam_ReturnsFalse()
    {
        // Condition #4: return must be a bound generic — not a bare T, which
        // leaks the parent's open parameter directly.
        var parent = CreateGenericStructDecl("Query", "T");
        var bareReturn = new NamedTypeSpec("τ_0_0");
        var accessor = CreateStaticAccessor("preset", parent, bareReturn);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ReturnTypeIsDifferentNominal_ReturnsFalse()
    {
        // Condition #4: return type must be the same nominal type as the parent.
        // A static factory that returns some other type entirely is a different
        // shape and is not covered by this admission.
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "OtherType", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("preset", parent, returnSpec);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ReturnReferencesOpenT_ReturnsFalse()
    {
        // Condition #5: return spec must NOT reference the parent's open generic
        // parameter — `Query<T>.preset: Query<T>` is the open shape and would
        // need full metadata threading.
        var parent = CreateGenericStructDecl("Query", "T");
        var openReturn = new NamedTypeSpec($"{TestModule}.Query");
        openReturn.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var accessor = CreateStaticAccessor("preset", parent, openReturn);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ReturnReferencesOpenTNested_ReturnsFalse()
    {
        // Condition #5 (nested): the open T may be buried inside another bound
        // generic argument (e.g. `Query<Box<T>>`). The recursive walk in
        // WrapperValidation.TypeSpecReferencesGenericParam must catch it.
        var parent = CreateGenericStructDecl("Query", "T");
        var nestedReturn = new NamedTypeSpec($"{TestModule}.Query");
        var box = new NamedTypeSpec($"{TestModule}.Box");
        box.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        nestedReturn.GenericParameters.Add(box);
        var accessor = CreateStaticAccessor("preset", parent, nestedReturn);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_PropertyOverload_NonStaticProperty_ReturnsFalse()
    {
        // PropertyDecl+MethodDecl overload: the property's IsStatic flag is
        // checked first, so a non-static property must be rejected even if the
        // accessor MethodDecl somehow carries MethodType.Static.
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);
        var property = CreatePropertyDecl("presetA", isStatic: false, returnSpec);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(property, accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_PropertyOverload_StaticProperty_ReturnsTrue()
    {
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);
        var property = CreatePropertyDecl("presetA", isStatic: true, returnSpec);

        Assert.True(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(property, accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_PropertyOnlyOverload_WithSetter_ReturnsFalse()
    {
        // PropertyDecl-only overload is consumed by wrapper eligibility paths in
        // PropertyWrapperEmitter (ShouldEmitWrapper, CanEmitGenericClassPropertyWrapper).
        // Settable static properties carry a value parameter on the setter accessor and
        // do not satisfy the gate; admitting the property here would skip helper gates
        // that the setter still depends on.
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var getterMethod = CreateStaticAccessor("presetA", parent, returnSpec);
        var setterMethod = CreateStaticAccessor("presetA", parent, returnSpec);
        setterMethod.CSSignature.Add(new ArgumentDecl
        {
            Name = "newValue",
            PrivateName = "newValue",
            SwiftTypeSpec = new NamedTypeSpec($"{TestModule}.StatA"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        });
        var property = CreatePropertyDeclWithAccessors("presetA", isStatic: true, returnSpec,
            new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod },
            });

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(property));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_PropertyOnlyOverload_GetterOnly_ReturnsTrue()
    {
        var parent = CreateGenericStructDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var getterMethod = CreateStaticAccessor("presetA", parent, returnSpec);
        var property = CreatePropertyDeclWithAccessors("presetA", isStatic: true, returnSpec,
            new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
            });

        Assert.True(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(property));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ClassParent_ReturnsFalse()
    {
        // The wrapper emits the IndirectResult (resultPtr) shape, so the P/Invoke must
        // also classify as IndirectResult. CdeclReturnMapping returns ClassPointer
        // (needsResultPtr=false, direct IntPtr return) for class returns, which would
        // produce an ABI-mismatched P/Invoke against the resultPtr-shaped wrapper.
        // SameNominalAsParent forces return-kind == parent-kind, so guarding parent on
        // StructDecl keeps the two sides in lockstep.
        var parent = CreateGenericClassDecl("Query", "T");
        var returnSpec = MakeBoundReturn(TestModule, "Query", innerName: $"{TestModule}.StatA");
        var accessor = CreateStaticAccessor("presetA", parent, returnSpec);

        Assert.False(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    [Fact]
    public void IsClosedStaticFactoryAccessor_ShortNameMatch_ReturnsTrue()
    {
        // Return TypeSpec may be parsed without a module prefix — only the
        // last name component is present. The gate falls back to short-name
        // match against the parent's nominal name.
        var parent = CreateGenericStructDecl("Query", "T");
        var unqualifiedReturn = new NamedTypeSpec("Query");
        unqualifiedReturn.GenericParameters.Add(new NamedTypeSpec("StatA"));
        var accessor = CreateStaticAccessor("preset", parent, unqualifiedReturn);

        Assert.True(ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(accessor));
    }

    #region Helpers

    private static StructDecl CreateGenericStructDecl(string name, string genericParamFriendlyName)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{TestModule}.{name}"),
            MangledName = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}VMa",
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0",
                    genericParamFriendlyName,
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            }
        };
    }

    private static ClassDecl CreateGenericClassDecl(string name, string genericParamFriendlyName)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{TestModule}.{name}"),
            MangledName = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}C",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFinal = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0",
                    genericParamFriendlyName,
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            }
        };
    }

    private static StructDecl CreateNonGenericStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{TestModule}.{name}"),
            MangledName = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}VMa",
        };
    }

    private static MethodDecl CreateStaticAccessor(string name, TypeDecl parent, TypeSpec returnSpec)
    {
        // Mirror SwiftABIParser behavior: an accessor without its own GenericSig
        // (true for the closed-static-factory shape) receives a copy of the parent
        // TypeDecl.GenericParameters (the τ_0_X entries). The gate itself does not
        // inspect GenericParameters, but downstream PInvokeEmitter /
        // MethodMarshalPlanBuilder paths iterate this list, so fixtures must match
        // production state for the test surface to be faithful.
        var accessor = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{TestModule.Length}{TestModule}{name.Length}{name}vgZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = returnSpec,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null,
                }
            },
            GenericParameters = parent.GenericParameters.ToList(),
            ParentDecl = parent,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        return accessor;
    }

    private static PropertyDecl CreatePropertyDecl(string name, bool isStatic, TypeSpec returnSpec)
    {
        return CreatePropertyDeclWithAccessors(name, isStatic, returnSpec, new List<AccessorDecl>());
    }

    private static PropertyDecl CreatePropertyDeclWithAccessors(string name, bool isStatic, TypeSpec returnSpec, List<AccessorDecl> accessors)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = returnSpec,
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static NamedTypeSpec MakeBoundReturn(string module, string nominal, string innerName)
    {
        var spec = new NamedTypeSpec($"{module}.{nominal}");
        spec.GenericParameters.Add(new NamedTypeSpec(innerName));
        return spec;
    }

    #endregion
}
