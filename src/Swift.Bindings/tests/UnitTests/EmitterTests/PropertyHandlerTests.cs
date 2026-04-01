// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for property accessors (getters and setters).
/// </summary>
public class PropertyHandlerTests
{
    #region AccessorDecl Tests

    [Fact]
    public void GetAccessorDecl_CanBeCreated()
    {
        var methodDecl = CreateMethodDecl("TestProperty_Get", MethodType.Instance);
        var accessor = new GetAccessorDecl { Method = methodDecl };

        Assert.NotNull(accessor);
        Assert.NotNull(accessor.Method);
        Assert.Equal("TestProperty_Get", accessor.Method.Name);
    }

    [Fact]
    public void SetAccessorDecl_CanBeCreated()
    {
        var methodDecl = CreateMethodDecl("TestProperty_Set", MethodType.Instance);
        var accessor = new SetAccessorDecl { Method = methodDecl };

        Assert.NotNull(accessor);
        Assert.NotNull(accessor.Method);
        Assert.Equal("TestProperty_Set", accessor.Method.Name);
    }

    [Fact]
    public void AccessorDecl_GetterAndSetter_AreDifferentTypes()
    {
        var getterMethod = CreateMethodDecl("TestProperty_Get", MethodType.Instance);
        var setterMethod = CreateMethodDecl("TestProperty_Set", MethodType.Instance);

        AccessorDecl getter = new GetAccessorDecl { Method = getterMethod };
        AccessorDecl setter = new SetAccessorDecl { Method = setterMethod };

        Assert.IsType<GetAccessorDecl>(getter);
        Assert.IsType<SetAccessorDecl>(setter);
        Assert.NotEqual(getter.GetType(), setter.GetType());
    }

    #endregion

    #region PropertyDecl Accessor Tests

    [Fact]
    public void PropertyDecl_WithOnlyGetter_IsReadOnly()
    {
        var property = CreatePropertyDecl("ReadOnlyProp", hasGetter: true, hasSetter: false);

        Assert.Single(property.Accessors);
        Assert.Single(property.Accessors.OfType<GetAccessorDecl>());
        Assert.Empty(property.Accessors.OfType<SetAccessorDecl>());
    }

    [Fact]
    public void PropertyDecl_WithGetterAndSetter_IsReadWrite()
    {
        var property = CreatePropertyDecl("ReadWriteProp", hasGetter: true, hasSetter: true);

        Assert.Equal(2, property.Accessors.Count);
        Assert.Single(property.Accessors.OfType<GetAccessorDecl>());
        Assert.Single(property.Accessors.OfType<SetAccessorDecl>());
    }

    [Fact]
    public void PropertyDecl_CanFilterAccessorsByType()
    {
        var property = CreatePropertyDecl("MixedProp", hasGetter: true, hasSetter: true);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Contains("_Get", getter!.Method.Name);
        Assert.Contains("_Set", setter!.Method.Name);
    }

    [Fact]
    public void PropertyDecl_StaticProperty_HasStaticAccessors()
    {
        var property = CreatePropertyDecl("StaticProp", hasGetter: true, hasSetter: true, isStatic: true);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Equal(MethodType.Static, getter!.Method.MethodType);
        Assert.Equal(MethodType.Static, setter!.Method.MethodType);
    }

    [Fact]
    public void PropertyDecl_InstanceProperty_HasInstanceAccessors()
    {
        var property = CreatePropertyDecl("InstanceProp", hasGetter: true, hasSetter: true, isStatic: false);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Equal(MethodType.Instance, getter!.Method.MethodType);
        Assert.Equal(MethodType.Instance, setter!.Method.MethodType);
    }

    #endregion

    #region Setter Method Signature Tests

    [Fact]
    public void SetterMethod_HasVoidReturnType()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        // First element in CSSignature is the return type
        var returnType = setter.Method.CSSignature[0];
        Assert.IsType<TupleTypeSpec>(returnType.SwiftTypeSpec);
        Assert.True(((TupleTypeSpec)returnType.SwiftTypeSpec).IsEmptyTuple);
    }

    [Fact]
    public void SetterMethod_HasValueParameter()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        // Second element in CSSignature is the value parameter
        Assert.Equal(2, setter.Method.CSSignature.Count);
        var valueParam = setter.Method.CSSignature[1];
        Assert.Equal("value", valueParam.Name);
    }

    [Fact]
    public void SetterMethod_HasCorrectPropertyType()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true, propertyType: "Swift.Int");
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        var valueParam = setter.Method.CSSignature[1];
        Assert.IsType<NamedTypeSpec>(valueParam.SwiftTypeSpec);
        // NameWithoutModule returns just the type name, not the full qualified name
        Assert.Equal("Int", ((NamedTypeSpec)valueParam.SwiftTypeSpec).NameWithoutModule);
    }

    #endregion

    #region Property Emission Tests

    [Fact]
    public void Emit_WithGetterAndSetter_EmitsAccessorMethodsAndProperty()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Counter", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int", hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("public virtual long Count", csOutput);
        Assert.Contains("get => Count_Get();", csOutput);
        Assert.Contains("set => Count_Set(value);", csOutput);
        Assert.Contains("public long Count_Get()", csOutput);
        Assert.Contains("public void Count_Set(", csOutput);
    }

    [Fact]
    public void Emit_WithNoAccessors_EmitsNothing()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Counter", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int", hasGetter: false, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_WhenPropertyNameMatchesContainingType_AppendsValueSuffix()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Animation", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "animation", "Swift.Int", hasGetter: true, hasSetter: false);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("public virtual long AnimationValue", csOutput);
        Assert.DoesNotContain("public virtual long Animation\n", csOutput);
    }

    [Fact]
    public void Emit_AsyncStreamProperty_EmitsAsyncEnumerableAndSwiftWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Feed", moduleDecl);
        var property = new PropertyDecl
        {
            Name = "updates",
            SwiftTypeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int")),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Contains("public IAsyncEnumerable<long> Updates", csOutput);
        Assert.Contains("private static unsafe byte updates_AsyncStream_OnElement", csOutput);
        Assert.Contains("PInvoke_Feed_updates_AsyncStream", csOutput);
        Assert.Contains("public func Feed_updates_AsyncStream", swiftOutput);
        Assert.Contains("for await element in __self.updates", swiftOutput);
        Assert.Contains("@_cdecl(", swiftOutput);
        Assert.Contains("_ self_: UnsafeMutableRawPointer", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncStreamProperty_UsesAsyncLibraryNameForPInvoke()
    {
        // Issue L: AsyncStream P/Invoke should use AsyncLibraryName (wrapper library)
        // rather than the module's native library path. The @_cdecl wrapper lives
        // in the Swift wrapper library, not the original framework.
        var typeDatabase = CreateTypeDatabaseWithInt();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Feed", moduleDecl);
        var property = new PropertyDecl
        {
            Name = "updates",
            SwiftTypeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int")),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // P/Invoke should import from the wrapper library, not from the native dylib
        Assert.Contains("LibraryImport(\"TestModuleSwiftBindings\"", csOutput);
        Assert.DoesNotContain("LibraryImport(\"/tmp/TestModule.dylib\"", csOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsupportedClosureFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var propertyType = new NamedTypeSpec("TestModule.Box", unsupportedClosure);
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "handler", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Unsupported closure fallback\",", csOutput);
        Assert.Contains("public virtual TestModule.Box<object> Handler", csOutput);
    }

    [Fact]
    public void Emit_PropertyWithExistentialBoundGeneric_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var propertyType = new NamedTypeSpec("TestModule.Box", existentialArg);
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "cache", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");
        CreateStructDeclForEmission("LottieVector3D", moduleDecl);

        var propertyType = new NamedTypeSpec(
            constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
            new NamedTypeSpec("TestModule.LottieVector3D"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "storage", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_OptionalExistentialProperty_WithUnsatisfiedAccessorConstraint_SkipsPropertyWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var optionalExistentialType = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", optionalExistentialType, hasGetter: true, hasSetter: true);

        // Simulate accessor signatures that use an unsatisfied constrained bound generic.
        var unsatisfiedAccessorType = new NamedTypeSpec(
            "TestModule.ValueProviderStorage",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")));
        foreach (var accessor in property.Accessors)
        {
            if (accessor is GetAccessorDecl)
            {
                accessor.Method.CSSignature[0].SwiftTypeSpec = unsatisfiedAccessorType;
            }
            else if (accessor is SetAccessorDecl)
            {
                accessor.Method.CSSignature[1].SwiftTypeSpec = unsatisfiedAccessorType;
            }
        }

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsatisfiedGetterReturnConstraint_SkipsPropertyWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var propertyType = new NamedTypeSpec("TestModule.Payload");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", propertyType, hasGetter: true, hasSetter: false);

        var getter = property.Accessors.OfType<GetAccessorDecl>().Single();
        getter.Method.CSSignature[0].SwiftTypeSpec = new NamedTypeSpec(
            "TestModule.ValueProviderStorage",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")));

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithSwiftStringAccessor_EmitsWithStringConversionBridge()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "name", "Swift.String", hasGetter: true, hasSetter: true);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        // Property type should be idiomatic string
        Assert.Contains("public virtual string Name", csOutput);
        // Getter should use block-bodied with using disposal (SwiftString is IDisposable)
        Assert.Contains("get { using var __ret = Name_Get(); return __ret.ToString(); }", csOutput);
        // Setter should use block-bodied with using disposal (new SwiftString creates IDisposable)
        Assert.Contains("set { using var __val = new SwiftString(value); Name_Set(__val); }", csOutput);
    }

    [Fact]
    public void Emit_CdeclPropertyStringGetter_UsesGlobalSystemQualification()
    {
        // @_cdecl string property getters decode Utf8Slice via Marshal.PtrToStringUTF8.
        // Must use global::System to avoid shadowing when a type member is named "System"
        // (e.g. XMLCoder.XMLDocumentType.System).
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DocType", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "name", "Swift.String", hasGetter: true, hasSetter: false);

        // Set UsesCdeclPropertyWrapper on the getter to trigger the Utf8Slice decode path
        var getter = property.Accessors.OfType<GetAccessorDecl>().Single();
        getter.Method.UsesCdeclPropertyWrapper = true;

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // @_cdecl string property getters now use SwiftMarshal.ReadUtf8Slice helper
        Assert.Contains("SwiftMarshal.ReadUtf8Slice", csOutput);
    }

    [Fact]
    public void Emit_SwiftArrayProperty_EmitsIReadOnlyListWithCorrectDisposal()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Container", moduleDecl);
        var arrayType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "items", arrayType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be IReadOnlyList<long>
        Assert.Contains("public virtual IReadOnlyList<long> Items", csOutput);
        // Getter: SwiftArray IS the returned IReadOnlyList — NO using (disposing would invalidate it)
        Assert.DoesNotContain("using var __ret", csOutput);
        Assert.Contains("get => Items_Get();", csOutput);
        // Setter: FromEnumerable creates a disposable SwiftArray — needs using
        Assert.Contains("using var __val", csOutput);
        Assert.Contains("SwiftArray<long>.FromEnumerable(value)", csOutput);
    }

    [Fact]
    public void Emit_SwiftOptionalStringProperty_EmitsNullableStringWithDisposal()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Profile", moduleDecl);
        var optionalStringType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "nickname", optionalStringType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be string?
        Assert.Contains("public virtual string? Nickname", csOutput);
        // Getter: SwiftOptional is IDisposable — needs using
        Assert.Contains("using var __ret", csOutput);
        // Setter: SwiftOptional.NewSome/NewNone creates IDisposable — needs using
        Assert.Contains("using var __val", csOutput);
    }

    [Fact]
    public void Emit_SwiftOptionalIntProperty_EmitsNullableLongWithDisposal()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Settings", moduleDecl);
        var optionalIntType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "timeout", optionalIntType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be long?
        Assert.Contains("public virtual long? Timeout", csOutput);
        // Getter: SwiftOptional is IDisposable — needs using, explicit HasValue/Some check
        // (implicit operator T?(SwiftOptional<T>) is broken for value types)
        Assert.Contains("using var __ret", csOutput);
        Assert.Contains("HasValue", csOutput);
        Assert.Contains(".Some", csOutput);
        // Setter: SwiftOptional.NewSome/NewNone creates IDisposable — needs using
        Assert.Contains("using var __val", csOutput);
        Assert.Contains("SwiftOptional<long>.NewSome", csOutput);
    }

    [Fact]
    public void Emit_OptionalUIFontProperty_EmitsNullableUIFont()
    {
        // CQ-7: Optional<UIKit.UIFont> should emit UIKit.UIFont? (ObjCBridged)
        // not SwiftOptional<UIKit.UIFont>.
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Label", moduleDecl);
        var optionalUIFontType = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("UIKit.UIFont"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "font", optionalUIFontType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be UIKit.UIFont? (not SwiftOptional<UIKit.UIFont>)
        Assert.Contains("UIKit.UIFont?", csOutput);
        Assert.DoesNotContain("SwiftOptional", csOutput);
    }

    [Fact]
    public void Emit_URLProperty_EmitsNSUrlViaObjCBridge()
    {
        var typeDatabase = CreateTypeDatabaseWithFoundationTypes();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("WebClient", moduleDecl);
        var urlType = new NamedTypeSpec("Foundation.URL");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "baseUrl", urlType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be Foundation.NSUrl (ObjC bridgeable)
        Assert.Contains("Foundation.NSUrl", csOutput);
        // ObjCBridgeableProjection: getter returns IntPtr, wrapped via GetNSObject
        Assert.Contains("GetNSObject<Foundation.NSUrl>", csOutput);
        // ObjCBridgeableProjection: setter extracts .Handle (IntPtr) from NSUrl
        Assert.Contains(".Handle", csOutput);
    }

    [Fact]
    public void Emit_DataProperty_EmitsByteArrayWithoutDisposal()
    {
        var typeDatabase = CreateTypeDatabaseWithFoundationTypes();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataStore", moduleDecl);
        var dataType = new NamedTypeSpec("Foundation.Data");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "payload", dataType, hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Property type should be byte[] (DataProjection)
        Assert.Contains("byte[]", csOutput);
        // Getter: Data is a struct (NOT IDisposable) — expression-bodied, no using
        Assert.Contains("get => Payload_Get().ToByteArray();", csOutput);
        Assert.DoesNotContain("using var __ret", csOutput);
        // Setter: Data is a struct — expression-bodied, no using
        Assert.Contains("set => Payload_Set(Swift.Data.FromByteArray(value));", csOutput);
        Assert.DoesNotContain("using var __val", csOutput);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name, MethodType methodType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Private
        };
    }

    private static PropertyDecl CreatePropertyDecl(
        string name,
        bool hasGetter,
        bool hasSetter,
        bool isStatic = false,
        string propertyType = "Swift.Int")
    {
        var accessors = new List<AccessorDecl>();
        var methodType = isStatic ? MethodType.Static : MethodType.Instance;

        if (hasGetter)
        {
            var getterMethod = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s{name}g",
                MethodType = methodType,
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
                        ModuleDecl = null
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private
            };
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        }

        if (hasSetter)
        {
            var setterMethod = new MethodDecl
            {
                Name = $"{name}_Set",
                MangledName = $"$s{name}s",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    // Return type (void)
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = TupleTypeSpec.Empty,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    },
                    // Value parameter
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = new NamedTypeSpec(propertyType),
                        Name = "value",
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private
            };
            accessors.Add(new SetAccessorDecl { Method = setterMethod });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(propertyType),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDeclWithTypeSpec(
        string name,
        TypeSpec typeSpec,
        bool hasGetter,
        bool hasSetter,
        bool isStatic = false)
    {
        var accessors = new List<AccessorDecl>();
        var methodType = isStatic ? MethodType.Static : MethodType.Instance;

        if (hasGetter)
        {
            var getterMethod = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s{name}g",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = typeSpec,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private
            };
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        }

        if (hasSetter)
        {
            var setterMethod = new MethodDecl
            {
                Name = $"{name}_Set",
                MangledName = $"$s{name}s",
                MethodType = methodType,
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
                        ModuleDecl = null
                    },
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = typeSpec,
                        Name = "value",
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private
            };
            accessors.Add(new SetAccessorDecl { Method = setterMethod });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static TypeDatabase CreateTypeDatabaseWithInt()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFoundationTypes()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Data"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                MetadataAccessor = "$s10Foundation4DataVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSData")
            });
        typeDatabase.AddModuleDatabase(foundationModule);
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDeclForEmission(string moduleName)
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
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDeclForEmission(string className, ModuleDecl moduleDecl)
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
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateStructDeclForEmission(string structName, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = structName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{structName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VN",
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
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateGenericStructDecl(string structName, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
    {
        var structDecl = CreateStructDeclForEmission(structName, moduleDecl);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: typeParameterName,
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(
                        Path: new[] { "τ_0_0" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(constraintProtocolName),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        return structDecl;
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

    private static PropertyDecl CreateEmittablePropertyDeclWithTypeSpec(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string name,
        TypeSpec propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        var accessors = new List<AccessorDecl>();
        var property = new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = propertyType,
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
                            SwiftTypeSpec = propertyType,
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
                            SwiftTypeSpec = propertyType,
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

    private static (string csOutput, string swiftOutput) EmitProperty(PropertyDecl property, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new PropertyHandler(new NullLogger<PropertyHandler>());
        var env = handler.Marshal(property, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    #endregion

    #region G2 — Optional Existential Property Pass-Through Tests

    [Fact]
    public void Emit_OptionalExistentialProperty_EmitsSimplePassThrough()
    {
        // G2 fix: Optional-existential properties emit simple pass-through
        // instead of going through TypeConversionHandler (which would apply
        // SwiftOptional conversion, producing incorrect ToNullable() calls).
        var typeDatabase = CreateTypeDatabaseWithInt();
        RegisterProtocol(typeDatabase, "TestModule.DataCaching");

        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Processor", moduleDecl);

        var optionalExistentialType = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));
        // Accessor CSSignature uses a registered type (Swift.Int) because the unit test
        // TypeDatabase doesn't have Swift.Optional registered — using the raw optional-existential
        // TypeSpec would cause the accessor pre-flight (SignatureHandler.GetWrapperSignature()
        // → ContainsPlaceholder) to skip the property before reaching EmitGetter/EmitSetter.
        // In production, the parser resolves accessor CSSignature types separately from the
        // property's SwiftTypeSpec. This test isolates the G2 fix: when isOptionalExistential=true,
        // EmitGetter/EmitSetter emit simple pass-through delegation.
        var registeredType = new NamedTypeSpec("Swift.Int");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", registeredType, hasGetter: true, hasSetter: true);
        property.SwiftTypeSpec = optionalExistentialType;

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Existential pass-through: simple delegation without type conversion
        Assert.Contains("get => DataCache_Get();", csOutput);
        Assert.Contains("set => DataCache_Set(value);", csOutput);
        // Should NOT apply SwiftOptional conversion (.ToNullable / new SwiftOptional)
        Assert.DoesNotContain("ToNullable", csOutput);
    }

    [Fact]
    public void Emit_OptionalExistentialProperty_SetterGetsCdeclWrapper()
    {
        // Fix A: Optional<existential> setters must get @_cdecl wrappers, not CallConvSwift.
        // NativeAOT can't lower Optional<ExistentialContainer> through Swift calling convention.
        var typeDatabase = CreateTypeDatabaseWithInt();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings"; // Enable xcframework mode
        RegisterProtocol(typeDatabase, "TestModule.DataCaching");

        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Processor", moduleDecl);

        var optionalExistentialType = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));
        var registeredType = new NamedTypeSpec("Swift.Int");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", registeredType, hasGetter: true, hasSetter: true);
        property.SwiftTypeSpec = optionalExistentialType;

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        // Both getter AND setter should get @_cdecl wrappers in Swift
        Assert.Contains("SBW_Get_TestModule_Processor_dataCache", swiftOutput);
        Assert.Contains("SBW_Set_TestModule_Processor_dataCache", swiftOutput);
        // C# setter P/Invoke uses CallConvCdecl
        Assert.Contains("CallConvCdecl", csOutput);
        // Setter property body marshals value to (IntPtr, bool) via existential container
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IDataCaching>(__v)", csOutput);
        Assert.Contains("NativeMemory.Alloc", csOutput);
        Assert.Contains("Unsafe.Copy(__heap, ref __container)", csOutput);
        Assert.Contains("__hasVal", csOutput);
        // Setter passes decomposed args — NOT simple pass-through
        Assert.DoesNotContain("set => DataCache_Set(value)", csOutput);
    }

    #endregion

    #region G1 — Generic Type Params in Properties Tests

    [Fact]
    public void Emit_PropertyWithGenericTypeParameter_ResolvesToT0NotAnyType()
    {
        // G1 fix: Properties on generic types where the property type is τ_0_0
        // resolve to "T0" via GenericContext, not "AnyType".
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Container", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        // Create property with a registered type initially, then override to generic param
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "value", "Swift.Int", hasGetter: true, hasSetter: false);
        property.SwiftTypeSpec = new NamedTypeSpec("τ_0_0");

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Generic type parameter τ_0_0 should resolve to T (from SugaredTypeName)
        Assert.Contains("public virtual T Value", csOutput);
        Assert.DoesNotContain("AnyType", csOutput);
    }

    #endregion

    #region H2 Bug 1 — Optional Tuple Property

    [Fact]
    public void Emit_OptionalTupleProperty_EmitsTupleTypeNotAnyType()
    {
        // H2 Bug 1: Optional<(BigUInt, BigUInt)> property fell through to AnyType
        // because TranslateTypeSpecWithGenerics only handled NamedTypeSpec, not TupleTypeSpec.
        // Fix: Added TupleTypeSpec branch before the final fallback.
        var typeDatabase = CreateTypeDatabaseWithInt();

        // Register Swift.Optional so bound generic resolution doesn't fall back to AnyType
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Optional"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("RSAPrimes", moduleDecl);

        // Create a tuple type (Int, Int) to simulate (BigUInt, BigUInt)
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        // Wrap in Optional — property SwiftTypeSpec is Optional<tuple>
        var optionalTupleType = new NamedTypeSpec("Swift.Optional", tupleType);

        // Use a registered accessor type to avoid pre-flight skip, then override property TypeSpec
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "primes", "Swift.Int", hasGetter: true, hasSetter: false);
        property.SwiftTypeSpec = optionalTupleType;

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // The property type should contain tuple syntax "(", not AnyType
        Assert.Contains("(", csOutput);
        Assert.DoesNotContain("AnyType", csOutput);
    }

    #endregion

    #region Existential Property Tests

    [Fact]
    public void PropertyDecl_WithSingleProtocolExistential_CanBeCreated()
    {
        // "any Equatable" is represented as NamedTypeSpec with IsAny=true
        var existentialType = new NamedTypeSpec("Swift.Equatable") { IsAny = true };
        var property = CreatePropertyDeclWithTypeSpec("delegate", existentialType, hasGetter: true, hasSetter: false);

        Assert.NotNull(property);
        Assert.Equal("delegate", property.Name);
        Assert.IsType<NamedTypeSpec>(property.SwiftTypeSpec);
        Assert.True(((NamedTypeSpec)property.SwiftTypeSpec).IsAny);
    }

    [Fact]
    public void PropertyDecl_WithProtocolComposition_CanBeCreated()
    {
        // "any P1 & P2" is represented as ProtocolListTypeSpec
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        var property = CreatePropertyDeclWithTypeSpec("constraint", protocolList, hasGetter: true, hasSetter: false);

        Assert.NotNull(property);
        Assert.IsType<ProtocolListTypeSpec>(property.SwiftTypeSpec);
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_SingleProtocol_ReturnsTrue()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        Assert.True(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_EightProtocols_ReturnsTrue()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocols = Enumerable.Range(1, 8)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.True(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_NineProtocols_ReturnsFalse()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocols = Enumerable.Range(1, 9)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.False(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_GetCSharpExistentialType_SingleProtocol_ReturnsContainer1()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var result = handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    [Fact]
    public void ExistentialHandler_GetCSharpExistentialType_TwoProtocols_ReturnsContainer2()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        var result = handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer2", result);
    }

    [Fact]
    public void ExistentialHandler_GetPublicExistentialType_MetatypeProtocol_ReturnsObject()
    {
        // H2 Bug 6: "Any.Type" (metatype existential) is parsed as NamedTypeSpec("Any.Type")
        // with IsAny=true. When converted to ProtocolListTypeSpec, the single "protocol"
        // is "Any.Type" which has no TypeRecord → GetPublicExistentialType returns "object".
        // WrapperEmitter.Return.cs now checks this and emits "return result;" instead
        // of attempting to construct a non-existent TypeProxy.
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        // Simulates the Any.Type metatype existential
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Any.Type") });
        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("object", result);
    }

    [Fact]
    public void ExistentialHandler_GetPublicExistentialType_StdlibError_ReturnsAnyError()
    {
        // Swift.Error is a well-known protocol that maps to Swift.AnyError
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") });
        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("Swift.AnyError", result);
    }

    [Fact]
    public void ExistentialHandler_GetPublicExistentialType_RegisteredProtocol_ReturnsInterfaceName()
    {
        // A real protocol with a TypeRecord returns the interface name, not "object".
        var typeDatabase = CreateTypeDatabaseWithInt();
        RegisterProtocol(typeDatabase, "TestModule.DataCaching");
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") });
        var result = handler.GetPublicExistentialType(protocolList);

        Assert.Equal("IDataCaching", result);
    }

    #region 5D.1 — Generic Type Property Accessor PInvokeHelperContext Threading

    [Fact]
    public void Emit_PropertyAccessorInGenericType_UsesPInvokeHelperClass()
    {
        // 5D.1 fix: PropertyHandler calls methodHandler.Emit directly for accessors,
        // bypassing HandleBaseDecl. Without explicit PInvokeHelperContext injection,
        // accessor P/Invoke declarations are emitted inline in the generic type → CS7042.
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("GenericBox", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int", hasGetter: true, hasSetter: false);

        var (csOutput, _) = EmitPropertyInGenericContext(property, typeDatabase);

        // Accessor P/Invoke should be routed to the helper class, not emitted inline
        Assert.Contains("GenericBox_PInvoke", csOutput);
        // Should NOT contain a bare [LibraryImport] inside the generic type
        Assert.DoesNotContain("[LibraryImport", csOutput);
    }

    #endregion

    #region WU4 — Generic Type AsyncStream Guard

    [Fact]
    public void PropertyHandler_AsyncStreamInGenericType_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Container", moduleDecl);

        // Create an AsyncStream<Int> property
        var asyncStreamType = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int"));
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "values", "Swift.Int", hasGetter: true, hasSetter: false);
        property.SwiftTypeSpec = asyncStreamType;

        // Emit in a generic type context
        var (csOutput, swiftOutput) = EmitPropertyInGenericContext(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    private static (string csOutput, string swiftOutput) EmitPropertyInGenericContext(PropertyDecl property, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new PropertyHandler(new NullLogger<PropertyHandler>());
        var env = handler.Marshal(property, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var context = new TypeHandlerContext(pinvokeCtx, new(), null);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion

    #region Explicit Interface Accessor Shape Tests

    [Fact]
    public void Emit_ExplicitInterfaceImpl_UsesProtocolAccessorShape_NotConcreteType()
    {
        // Regression test for Finding 1: When a protocol declares a property as { get }
        // but the concrete type has { get; set; }, the explicit interface implementation
        // must use the protocol's accessor shape (get-only), not the concrete type's.
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");

        // Class named "Displayable" — a property named "displayable" triggers CS0542 rename
        var classDecl = CreateClassDeclForEmission("Displayable", moduleDecl);

        // Protocol "Displayable" declares the property as get-only
        var protocolDecl = new ProtocolDecl
        {
            Name = "Displayable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Displayable"),
            MangledName = "$s10TestModule11DisplayablePPN",
            Properties = new List<PropertyDecl>
            {
                CreatePropertyDecl("displayable", hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Register protocol in TypeDatabase so GetImplementedInterfaces includes it.
        // Use AddOutOfModuleTypes since the TestModule module database already exists.
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (protocolDecl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDisplayable"),
                SwiftTypeName = protocolDecl.SwiftTypeName,
                Kind = TypeRecordKind.Protocol,
                Flags = TypeRecordFlags.None,
                MetadataAccessor = "",
            })
        });

        // Concrete type conforms to the protocol
        classDecl.Conformances.Add(new TypeConformance(
            ConformingType: classDecl.SwiftTypeName,
            Protocol: protocolDecl.SwiftTypeName,
            ProtocolConformanceDescriptor: "$s10TestModule11DisplayableCAA0C0AAWP"));

        // Concrete property has BOTH getter and setter
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "displayable", "Swift.Int",
            hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // The main property should be renamed to DisplayableValue (CS0542 avoidance)
        Assert.Contains("public virtual long DisplayableValue", csOutput);
        // Explicit interface implementation should exist
        Assert.Contains("IDisplayable.Displayable", csOutput);
        // The explicit interface impl should have ONLY a getter (matching protocol shape)
        Assert.Contains("get => DisplayableValue;", csOutput);
        // Should NOT have a setter in the explicit interface implementation
        Assert.DoesNotContain("set => DisplayableValue", csOutput);
    }

    [Fact]
    public void Emit_ExplicitInterfaceImpl_WithProtocolGetSet_EmitsBothAccessors()
    {
        // Counter-case: when the protocol declares { get; set; }, both should be emitted.
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Displayable", moduleDecl);

        // Protocol declares { get; set; }
        var protocolDecl = new ProtocolDecl
        {
            Name = "Displayable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Displayable"),
            MangledName = "$s10TestModule11DisplayablePPN",
            Properties = new List<PropertyDecl>
            {
                CreatePropertyDecl("displayable", hasGetter: true, hasSetter: true)
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Register protocol in TypeDatabase so GetImplementedInterfaces includes it.
        // Use AddOutOfModuleTypes since the TestModule module database already exists.
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (protocolDecl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDisplayable"),
                SwiftTypeName = protocolDecl.SwiftTypeName,
                Kind = TypeRecordKind.Protocol,
                Flags = TypeRecordFlags.None,
                MetadataAccessor = "",
            })
        });

        classDecl.Conformances.Add(new TypeConformance(
            ConformingType: classDecl.SwiftTypeName,
            Protocol: protocolDecl.SwiftTypeName,
            ProtocolConformanceDescriptor: "$s10TestModule11DisplayableCAA0C0AAWP"));

        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "displayable", "Swift.Int",
            hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("IDisplayable.Displayable", csOutput);
        Assert.Contains("get => DisplayableValue;", csOutput);
        Assert.Contains("set => DisplayableValue = value;", csOutput);
    }

    #endregion

    private class MockPropertyTypeDatabase : ITypeDatabase
    {
        public string AsyncLibraryName => null!;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            record = null!;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region ObjC Optional Accessor Conversions

    [Fact]
    public void GetOptionalAccessorGetterConversion_ObjCBridged_ReturnsGetNSObject()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "result");

        Assert.NotNull(conversion);
        Assert.Contains("GetNSObject<UIKit.UIImage>", conversion);
        Assert.Contains("IntPtr.Zero", conversion);
        Assert.DoesNotContain("SwiftOptional", conversion);
        Assert.False(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ObjCBridged_ReturnsHandleOrZero()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.NotNull(conversion);
        Assert.Contains(".Handle", conversion);
        Assert.Contains("IntPtr.Zero", conversion);
        Assert.DoesNotContain("SwiftOptional", conversion);
        Assert.False(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorGetterConversion_ObjCBridged_HasDuplicatedResultExpr()
    {
        // Issue N: ObjCBridged Optional getter produces a ternary that references
        // the result expression twice (null check + bridge call). When the result
        // expression is a method call, this means calling P/Invoke twice.
        // The PropertyHandler.EmitProjectedGetter detects this and caches the result.
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (conversion, _) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "PInvoke_get_Foo()");

        Assert.NotNull(conversion);
        // The conversion contains the method call TWICE (ternary check + bridge)
        var methodCall = "PInvoke_get_Foo()";
        var firstIdx = conversion!.IndexOf(methodCall, System.StringComparison.Ordinal);
        Assert.True(firstIdx >= 0, "Conversion should contain the method call");
        var secondIdx = conversion.IndexOf(methodCall, firstIdx + 1, System.StringComparison.Ordinal);
        Assert.True(secondIdx >= 0, "ObjCBridged Optional conversion duplicates the method call (triggers caching in EmitProjectedGetter)");
    }

    [Fact]
    public void GetOptionalAccessorGetterConversion_ObjCBridged_CachedExprIsSingleCall()
    {
        // Bug fix: When EmitProjectedGetter detects the double-call pattern, it re-derives
        // the conversion using a cached variable (__ptr). Verify the cached conversion only
        // references the cached variable — no method call duplication (ARC leak).
        var inner = new ObjCBridgedProjection("Foundation.NSUrlResponse");
        var opt = new OptionalProjection(inner);

        var (cachedConversion, _) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "__ptr");

        Assert.NotNull(cachedConversion);
        // Cached conversion should reference __ptr exactly twice (nil check + bridge call)
        // but since __ptr is a local variable (not a P/Invoke), there's no ARC leak
        Assert.Contains("__ptr == IntPtr.Zero", cachedConversion!);
        Assert.Contains("GetNSObject<Foundation.NSUrlResponse>(__ptr)", cachedConversion);
        // Must NOT contain any method call pattern (parentheses after name)
        Assert.DoesNotContain("()", cachedConversion);
    }

    #endregion

    #region ObjCRooted Container Accessor Conversion Tests

    [Fact]
    public void GetDictAccessorSetterConversion_ObjCRootedKey_SkipsElementConversion()
    {
        // ObjCRooted keys should NOT get .Handle element conversion — accessor takes typed value directly
        var key = new ObjCRootedClassProjection("UIKit.UIViewController");
        var value = new StringProjection();
        var dict = new DictionaryProjection(key, value, isParameter: true);

        var (conversion, _) = PropertyHandler.GetDictAccessorSetterConversion(dict, "value");

        // Should have value conversion for String but NOT key conversion (.Handle)
        Assert.NotNull(conversion);
        Assert.DoesNotContain("kvp.Key.Handle", conversion!);
    }

    [Fact]
    public void GetDictAccessorSetterConversion_ObjCRootedValue_SkipsElementConversion()
    {
        var key = new StringProjection();
        var value = new ObjCRootedClassProjection("UIKit.UIView");
        var dict = new DictionaryProjection(key, value, isParameter: true);

        var (conversion, _) = PropertyHandler.GetDictAccessorSetterConversion(dict, "value");

        // Should have key conversion for String but NOT value conversion (.Handle)
        Assert.NotNull(conversion);
        Assert.DoesNotContain("kvp.Value.Handle", conversion!);
    }

    [Fact]
    public void GetDictAccessorSetterConversion_BothObjCRooted_NoElementConversion()
    {
        var key = new ObjCRootedClassProjection("UIKit.UIViewController");
        var value = new ObjCRootedClassProjection("UIKit.UIView");
        var dict = new DictionaryProjection(key, value, isParameter: true);

        var (conversion, _) = PropertyHandler.GetDictAccessorSetterConversion(dict, "value");

        // Neither key nor value should have element conversion — passthrough
        Assert.NotNull(conversion);
        Assert.DoesNotContain(".Handle", conversion!);
        Assert.DoesNotContain(".Select", conversion!);
    }

    [Fact]
    public void GetSetAccessorSetterConversion_ObjCRooted_SkipsElementConversion()
    {
        var elem = new ObjCRootedClassProjection("UIKit.UIView");
        var set = new SetProjection(elem, isParameter: true);

        var (conversion, _) = PropertyHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.NotNull(conversion);
        Assert.DoesNotContain(".Handle", conversion!);
        Assert.DoesNotContain(".Select", conversion!);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ObjCRooted_UsesSwiftOptionalWrapper()
    {
        var inner = new ObjCRootedClassProjection("UIKit.UIView");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion!);
        Assert.Contains("NewSome", conversion!);
        Assert.Contains("NewNone", conversion!);
        Assert.True(requiresDisposal);
    }

    #endregion

    #region Optional Value Type Getter Conversion Tests

    [Fact]
    public void GetOptionalAccessorGetterConversion_BlittableInt_UsesExplicitHasValueCheck()
    {
        // Regression test: implicit operator T?(SwiftOptional<T>) is broken for value types.
        // T is unconstrained, so T? in IL is T (not Nullable<T>). default(int) returns 0,
        // causing None to appear as Some(0). The getter must use explicit HasValue/Some check.
        var inner = new BlittableProjection("int");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "__ret");

        Assert.NotNull(conversion);
        Assert.Contains("HasValue", conversion!);
        Assert.Contains(".Some", conversion);
        Assert.DoesNotContain("(int?)__ret)", conversion); // Must NOT use implicit operator cast
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorGetterConversion_BlittableBool_UsesExplicitHasValueCheck()
    {
        var inner = new BoolProjection();
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "__ret");

        Assert.NotNull(conversion);
        Assert.Contains("HasValue", conversion!);
        Assert.Contains(".Some", conversion);
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorGetterConversion_SimpleEnum_UsesExplicitHasValueCheck()
    {
        var inner = new SimpleEnumProjection("MyModule.MyEnum", "int");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "__ret");

        Assert.NotNull(conversion);
        Assert.Contains("HasValue", conversion!);
        Assert.Contains(".Some", conversion);
        Assert.True(requiresDisposal);
    }

    #endregion

    #region Async Property Emission Tests

    [Fact]
    public void Emit_AsyncPropertyGetter_EmitsTaskReturningMethod()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "count", "Swift.Int");
        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Should emit a Task-returning method, not a C# property
        Assert.Contains("Task<", csOutput);
        Assert.Contains("GetCount", csOutput);
        Assert.Contains("CancellationToken", csOutput);
        // Should NOT emit property syntax
        Assert.DoesNotContain("get =>", csOutput);
        Assert.DoesNotContain("get;", csOutput);
    }

    [Fact]
    public void Emit_AsyncPropertyGetter_SwiftWrapper_UsesPropertyAccessSyntax()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "count", "Swift.Int");
        var (_, swiftOutput) = EmitProperty(property, typeDatabase);

        // Swift wrapper should use property access (no parens), not method call
        Assert.Contains("await", swiftOutput);
        // Property access: instance.count (not instance.count())
        Assert.Contains(".count", swiftOutput);
        Assert.DoesNotContain(".count(", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncPropertyGetter_SetsAsyncPropertyNameOnMethodDecl()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "count", "Swift.Int");
        EmitProperty(property, typeDatabase);

        // After emission, the getter's MethodDecl should have AsyncPropertyName set
        var getter = property.Accessors.OfType<GetAccessorDecl>().First();
        Assert.Equal("count", getter.Method.AsyncPropertyName);
    }

    [Fact]
    public void Emit_AsyncPropertyGetter_MarksPropertyAsEmitted()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "count", "Swift.Int");
        EmitProperty(property, typeDatabase);

        Assert.True(property.WasEmitted);
    }

    [Fact]
    public void Emit_AsyncThrowingPropertyGetter_EmitsTaskReturningMethod()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "data", "Swift.Int", throws: true);
        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Contains("Task<", csOutput);
        Assert.Contains("GetData", csOutput);
        // Swift wrapper should include error handling
        Assert.Contains("catch", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncThrowingPropertyGetter_SwiftWrapperUsesTryAwait()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "data", "Swift.Int", throws: true);
        var (_, swiftOutput) = EmitProperty(property, typeDatabase);

        // Async throwing property getter must use "try await" not bare "await"
        Assert.Contains("try await", swiftOutput);
        Assert.DoesNotContain("\n            await __self.data\n", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncNonThrowingPropertyGetter_SwiftWrapperUsesAwait()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataFetcher", moduleDecl);

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "value", "Swift.Int", throws: false);
        var (_, swiftOutput) = EmitProperty(property, typeDatabase);

        // Non-throwing async property getter should use "await" without "try"
        Assert.Contains("await", swiftOutput);
        Assert.DoesNotContain("try await", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncPropertyInGenericType_SkipsEmission()
    {
        // Async properties require [UnmanagedCallersOnly] callbacks which are illegal
        // inside generic types (CS8895). Same gate as AsyncStream properties.
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("DataTask", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: "TValue",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        var property = CreateAsyncPropertyDecl(classDecl, moduleDecl, "value", "Swift.Int");
        var (csOutput, swiftOutput) = EmitPropertyInGenericContext(property, typeDatabase);

        // Should skip — no C# or Swift output
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
        Assert.False(property.WasEmitted);
    }

    /// <summary>
    /// Creates a PropertyDecl with an async getter accessor, suitable for emission tests.
    /// </summary>
    private static PropertyDecl CreateAsyncPropertyDecl(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string name,
        string propertyType,
        bool throws = false)
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

        accessors.Add(new GetAccessorDecl
        {
            Method = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s10TestModule11DataFetcherC{name.Length}{name}Sivg",
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
                Throws = throws,
                IsAsync = true,
                Visibility = Visibility.Public
            }
        });

        classDecl.Properties.Add(property);
        return property;
    }

    #endregion
}
