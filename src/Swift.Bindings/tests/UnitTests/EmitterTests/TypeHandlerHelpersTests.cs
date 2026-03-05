// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeHandlerHelpers — GetImplementedInterfaces, EqualityMethodsWriter.
/// </summary>
public class TypeHandlerHelpersTests
{
    #region GetImplementedInterfaces Tests

    [Fact]
    public void GetImplementedInterfaces_MinimalType_IncludesISwiftObjectAndIDisposable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "Loader", "TestModule", typeDatabase);

        Assert.Contains("ISwiftObject", interfaces);
        Assert.Contains("IDisposable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_EquatableType_IncludesIEquatable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains("IEquatable<Point>", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_HashableType_SkipsHashableInterface()
    {
        // Hashable is a marker — not emitted as a C# interface
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule5PointVSHAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Hashable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_NoTypeRecord_Excluded()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.SomeProtocol"),
                "$s10TestModule5PointVOtherModuleSomeProtocolMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        // Cross-module protocol without a TypeRecord in the database should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("SomeProtocol"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithTypeRecord_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Renderable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Widget", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Renderable"),
                "$s10TestModule6WidgetVOtherModuleRenderableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Widget", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Renderable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithMembers_Excluded()
    {
        // Protocol with 3 emitted members — cross-module conformance would cause CS0535
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Drawable", emittedMemberCount: 3);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Canvas", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Drawable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Canvas", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Drawable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_OldDatabase_NullMemberCount_Excluded()
    {
        // Old database without EmittedMemberCount — conservatively skip
        var typeDatabase = CreateTypeDatabaseWithProtocolNullMemberCount("OtherModule", "Legacy");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Adapter", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Adapter"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Legacy"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Adapter", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Legacy"));
    }

    [Fact]
    public void GetImplementedInterfaces_SameModuleProtocol_WithMembers_NotAffectedByGate()
    {
        // Same-module protocols are NOT gated by EmittedMemberCount (validated by CanFullyImplementProtocol)
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable", emittedMemberCount: 5);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        // Same-module: EmittedMemberCount gate does NOT apply
        Assert.Contains(interfaces, i => i.Contains("Describable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithAssociatedTypes_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPATInModule("OtherModule", "AsyncSequence");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Stream", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Stream"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.AsyncSequence"),
                "$s10TestModule6StreamVOtherModuleAsyncSequenceMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Stream", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("AsyncSequence"));
    }

    [Fact]
    public void GetImplementedInterfaces_ProtocolWithAssociatedType_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.MyIterator"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                "$s10TestModule10MyIteratorVIterableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "MyIterator", "TestModule", typeDatabase);

        // Protocols with associated types should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("Iterable"));
    }

    [Fact]
    public void GetImplementedInterfaces_SwiftErrorConformance_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftError();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDeclWithConformances("ParseError", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                "$s10TestModule10ParseErrorOs0E0AAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "ParseError", "TestModule", typeDatabase);

        // Swift.Error maps to AnyError (a runtime type), not an IError interface
        Assert.DoesNotContain(interfaces, i => i.Contains("IError"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_InheritingEmptyProtocol_Included()
    {
        // Protocol with 0 direct members inheriting an empty marker protocol (EmittedMemberCount=0).
        // Total requirements = 0 direct + 0 inherited = 0 → should be emitted.
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "Taggable", parentEmittedMemberCount: 0);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Item", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Taggable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Item", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Taggable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_InheritingNonEmptyProtocol_Excluded()
    {
        // Protocol with 0 direct members inheriting a non-empty protocol (EmittedMemberCount=3).
        // Total requirements = 0 direct + 1 inherited with members = 1 → should be excluded.
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "StrictTaggable", parentEmittedMemberCount: 3);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Item", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.StrictTaggable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Item", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("StrictTaggable"));
    }

    [Fact]
    public void GetImplementedInterfaces_SameModuleProtocol_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule5PointVDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Describable"));
    }

    #endregion

    #region ConformanceDescriptor Tests

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithTypeRecord_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Renderable");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Renderable"),
                "$s10TestModule6WidgetVOtherModuleRenderableMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        Assert.Contains("typeof(IRenderable)", result);
        Assert.Contains("\"$s10TestModule6WidgetVOtherModuleRenderableMc\"", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_NoTypeRecord_Excluded()
    {
        var typeDatabase = CreateTypeDatabase();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Unknown"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        Assert.DoesNotContain("Unknown", result);
    }

    [Fact]
    public void ConformanceDescriptor_SwiftErrorConformance_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftError();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                "$s10TestModule10ParseErrorOs0E0AAMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "ParseError", typeDatabase);

        // Swift.Error maps to AnyError (a runtime type), not an IError interface
        Assert.DoesNotContain("IError", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithMembers_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Drawable", emittedMemberCount: 3);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Drawable"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Canvas", typeDatabase);

        Assert.DoesNotContain("Drawable", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_OldDatabase_NullMemberCount_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocolNullMemberCount("OtherModule", "Legacy");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Adapter"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Legacy"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Adapter", typeDatabase);

        Assert.DoesNotContain("Legacy", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_InheritingEmptyProtocol_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "Taggable", parentEmittedMemberCount: 0);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Taggable"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Item", typeDatabase);

        Assert.Contains("typeof(ITaggable)", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_InheritingNonEmptyProtocol_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "StrictTaggable", parentEmittedMemberCount: 3);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.StrictTaggable"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Item", typeDatabase);

        Assert.DoesNotContain("StrictTaggable", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithAssociatedTypes_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPATInModule("OtherModule", "AsyncSequence");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Stream"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.AsyncSequence"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Stream", typeDatabase);

        Assert.DoesNotContain("AsyncSequence", result);
    }

    [Fact]
    public void ConformanceDescriptor_EmptySymbol_ExcludedFromDictionary()
    {
        // Empty conformance symbol should be filtered out — LoadFromSymbol("lib", "") crashes at runtime.
        var typeDatabase = CreateTypeDatabase();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                ""), // Empty symbol
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule6WidgetVSHAAMc") // Valid symbol
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        // Hashable should be present (valid symbol), Equatable should be filtered out (empty symbol)
        Assert.Contains("$s10TestModule6WidgetVSHAAMc", result);
        Assert.DoesNotContain("IEquatable", result);
    }

    #endregion

    #region EqualityMethodsWriter Tests

    [Fact]
    public void WriteSwiftEquatable_Equatable_EmitsEquals()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableAndHashable_EmitsSwiftHashableGetHashCode()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc1"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$sMc2"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftHashable.GetHashCode(this)", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableNotHashable_EmitsReturnZero()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("return 0;", result);
        Assert.DoesNotContain("SwiftHashable", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitEqualityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("operator ==(", result);
        // != should still be emitted since only == is explicit
        Assert.Contains("operator !=(", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitInequalityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: true);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("operator ==(", result);
        Assert.DoesNotContain("operator !=(", result);
    }

    #endregion

    #region ToStringHelper Tests

    [Fact]
    public void TryGetDescriptionPropertyName_WithDescription_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));

        Assert.True(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out var name));
        Assert.Equal("Description", name);
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WithoutDescription_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_StaticDescription_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var prop = CreateDescriptionProperty(moduleDecl);
        prop.IsStatic = true;
        classDecl.Properties.Add(prop);

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WrongType_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "description",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = CreateMinimalMethodDecl(moduleDecl) } },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WithRename_ReturnsRenamedName()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));
        var renames = new Dictionary<string, string> { { "Description", "DescriptionValue" } };

        Assert.True(ToStringHelper.TryGetDescriptionPropertyName(classDecl, renames, out var name));
        Assert.Equal("DescriptionValue", name);
    }

    [Fact]
    public void EmitToString_WithDescription_EmitsOverride()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));

        ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, null);

        Assert.Contains("public override string ToString() => Description;", output.ToString());
    }

    [Fact]
    public void EmitToString_WithoutDescription_EmitsNothing()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, null);

        Assert.Equal("", output.ToString());
    }

    private static PropertyDecl CreateDescriptionProperty(ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = "description",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = CreateMinimalMethodDecl(moduleDecl) } },
            ParentDecl = null!,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateMinimalMethodDecl(ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = "description.get",
            MangledName = "$sTest",
            IsAccessor = true,
            IsFinal = false,
            IsConstructor = false,
            MethodType = MethodType.Instance,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null!,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region Helper Methods

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
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithPAT()
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
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IIterable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithPATInModule(string module, string name)
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
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string module, string name, int emittedMemberCount = 0)
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
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = emittedMemberCount
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolNullMemberCount(string module, string name)
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
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                // EmittedMemberCount intentionally null — simulates old database
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithSwiftError()
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
        // Register Swift.Error as a distinct TypeRecord instance (not the singleton)
        // to verify logical identity check, not reference equality.
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "AnyError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static ClassDecl CreateClassDeclWithConformances(string name, ModuleDecl moduleDecl, params TypeConformance[] conformances)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(conformances),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a TypeDatabase with a protocol that inherits from a parent protocol.
    /// When parentEmittedMemberCount is 0, the produced EmittedMemberCount should be 0
    /// (inheriting from an empty marker protocol doesn't add requirements).
    /// When parentEmittedMemberCount > 0, the produced EmittedMemberCount should be > 0.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithInheritingProtocol(string module, string name, int parentEmittedMemberCount)
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
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        // Register the parent protocol with the given member count
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.BaseMarker"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", "IBaseMarker"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.BaseMarker"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = parentEmittedMemberCount
            });
        // Register the child protocol with EmittedMemberCount reflecting inherited requirements.
        // This simulates what ProtocolHandler.Emit would compute after the fix:
        // 0 direct members + (parentEmittedMemberCount > 0 ? 1 : 0) inherited with requirements.
        int childEmittedMemberCount = parentEmittedMemberCount > 0 ? 1 : 0;
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = childEmittedMemberCount
            });
        typeDatabase.AddModuleDatabase(targetModule);
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
    }

    #endregion

    #region OptionSet/RawRepresentable Imply Hashable

    [Fact]
    public void OptionSetConformance_ImpliesHashable()
    {
        var conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Caches"),
                SwiftTypeName.FromModuleQualifiedName("Swift.OptionSet"),
                ProtocolConformanceDescriptor: string.Empty)
        };

        bool impliesHashable = conformances.Any(c =>
            c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
            c.Protocol.Name == "OptionSet" ||
            c.Protocol.Name == "RawRepresentable");

        Assert.True(impliesHashable,
            "OptionSet conformance should be treated as implying Hashable");
    }

    [Fact]
    public void RawRepresentableConformance_ImpliesHashable()
    {
        var conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
                SwiftTypeName.FromModuleQualifiedName("Swift.RawRepresentable"),
                ProtocolConformanceDescriptor: string.Empty)
        };

        bool impliesHashable = conformances.Any(c =>
            c.Protocol.Name == "OptionSet" ||
            c.Protocol.Name == "RawRepresentable");

        Assert.True(impliesHashable);
    }

    #endregion

    #region Extension Marshalling — ObjC-Rooted Classification

    [Fact]
    public void ClassifyParameterType_ObjCRooted_ReturnsObjCClass()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.STPAPIClient"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "STPAPIClient"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.STPAPIClient"),
                MetadataAccessor = "testAccessor",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });
        typeDatabase.AddModuleDatabase(testModule);

        var result = ExtensionMarshallingHelper.ClassifyParameterType(
            new NamedTypeSpec("TestModule.STPAPIClient"), typeDatabase);

        Assert.Equal(ExtensionMarshallingHelper.ParamKind.ObjCClass, result);
    }

    [Fact]
    public void ClassifyReturnType_ObjCRooted_ReturnsObjCClass()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.STPAPIClient"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "STPAPIClient"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.STPAPIClient"),
                MetadataAccessor = "testAccessor",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });
        typeDatabase.AddModuleDatabase(testModule);

        var result = ExtensionMarshallingHelper.ClassifyReturnType(
            new NamedTypeSpec("TestModule.STPAPIClient"), typeDatabase);

        Assert.Equal(ExtensionMarshallingHelper.ReturnKind.ObjCClass, result);
    }

    [Fact]
    public void GetPInvokeArgExpression_ObjCClass_UsesHandle()
    {
        var expr = ExtensionMarshallingHelper.GetPInvokeArgExpression("client", ExtensionMarshallingHelper.ParamKind.ObjCClass);
        Assert.Equal("client.Handle", expr);
    }

    [Fact]
    public void GetPInvokeArgExpression_SwiftClass_UsesPayload()
    {
        var expr = ExtensionMarshallingHelper.GetPInvokeArgExpression("pipeline", ExtensionMarshallingHelper.ParamKind.SwiftClass);
        Assert.Equal("pipeline.Payload.DangerousGetHandle()", expr);
    }

    #endregion

    #region Test Helpers (Struct Factory)

    private static StructDecl CreateStructDeclWithConformances(string name, ModuleDecl moduleDecl, params TypeConformance[] conformances)
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
            Conformances = new List<TypeConformance>(conformances),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    #endregion
}
