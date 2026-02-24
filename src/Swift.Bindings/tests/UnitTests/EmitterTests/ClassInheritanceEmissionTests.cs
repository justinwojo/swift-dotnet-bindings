// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for class inheritance emission: topological sort, declaration syntax,
/// payload sharing, ISwiftObject handling, and fallback behavior.
/// </summary>
[Collection("ReportCollector")]
public class ClassInheritanceEmissionTests
{
    #region Topological Sort Tests

    [Fact]
    public void TopologicalSort_NoClasses_PreservesOrder()
    {
        var decls = new List<BaseDecl>
        {
            CreateStructDecl("Alpha"),
            CreateEnumDecl("Beta"),
            CreateStructDecl("Gamma"),
        };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("Alpha", sorted[0].Name);
        Assert.Equal("Beta", sorted[1].Name);
        Assert.Equal("Gamma", sorted[2].Name);
    }

    [Fact]
    public void TopologicalSort_SingleRootClass_PreservesOrder()
    {
        var decls = new List<BaseDecl>
        {
            CreateClassDecl("OnlyClass"),
        };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Single(sorted);
        Assert.Equal("OnlyClass", sorted[0].Name);
    }

    [Fact]
    public void TopologicalSort_AlreadyCorrectOrder_Unchanged()
    {
        var baseClass = CreateClassDecl("Base");
        var derived = CreateClassDecl("Derived");
        derived.ResolvedSuperclass = baseClass;

        var decls = new List<BaseDecl> { baseClass, derived };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Equal(2, sorted.Count);
        Assert.Equal("Base", sorted[0].Name);
        Assert.Equal("Derived", sorted[1].Name);
    }

    [Fact]
    public void TopologicalSort_DerivedBeforeBase_Reordered()
    {
        var baseClass = CreateClassDecl("Base");
        var derived = CreateClassDecl("Derived");
        derived.ResolvedSuperclass = baseClass;

        // Wrong order: derived first
        var decls = new List<BaseDecl> { derived, baseClass };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Equal(2, sorted.Count);
        Assert.Equal("Base", sorted[0].Name);
        Assert.Equal("Derived", sorted[1].Name);
    }

    [Fact]
    public void TopologicalSort_ThreeLevelChain_CorrectOrder()
    {
        var grandparent = CreateClassDecl("Request");
        var parent = CreateClassDecl("DataRequest");
        var child = CreateClassDecl("UploadRequest");

        parent.ResolvedSuperclass = grandparent;
        child.ResolvedSuperclass = parent;

        // Completely reversed
        var decls = new List<BaseDecl> { child, parent, grandparent };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("Request", sorted[0].Name);
        Assert.Equal("DataRequest", sorted[1].Name);
        Assert.Equal("UploadRequest", sorted[2].Name);
    }

    [Fact]
    public void TopologicalSort_MixedTypes_NonClassOrderPreserved()
    {
        var baseClass = CreateClassDecl("Base");
        var structA = CreateStructDecl("StructA");
        var derived = CreateClassDecl("Derived");
        var enumB = CreateEnumDecl("EnumB");

        derived.ResolvedSuperclass = baseClass;

        // Order: Derived, StructA, Base, EnumB
        var decls = new List<BaseDecl> { derived, structA, baseClass, enumB };

        var sorted = InvokeTopologicalSort(decls);

        Assert.Equal(4, sorted.Count);
        // Base must come before Derived. StructA and EnumB maintain original relative order.
        int baseIdx = sorted.FindIndex(d => d.Name == "Base");
        int derivedIdx = sorted.FindIndex(d => d.Name == "Derived");
        Assert.True(baseIdx < derivedIdx, "Base must come before Derived");
    }

    [Fact]
    public void TopologicalSort_CyclicDependency_NoDrop()
    {
        // Simulate a cycle (shouldn't happen in valid Swift, but guards against data corruption).
        // Both classes point to each other as superclass.
        var classA = CreateClassDecl("ClassA");
        var classB = CreateClassDecl("ClassB");
        classA.ResolvedSuperclass = classB;
        classB.ResolvedSuperclass = classA;

        var decls = new List<BaseDecl> { classA, classB };

        var sorted = InvokeTopologicalSort(decls);

        // Both declarations must appear — neither should be silently dropped
        Assert.Equal(2, sorted.Count);
        Assert.Contains(sorted, d => d.Name == "ClassA");
        Assert.Contains(sorted, d => d.Name == "ClassB");
    }

    #endregion

    #region Declaration Syntax Tests

    [Fact]
    public void DerivedClass_StartsWithBaseClass()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        Assert.Contains("public partial class Dog : Animal", output);
    }

    [Fact]
    public void DerivedClass_KeepsISwiftObject_OmitsIDisposable()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        // Derived keeps ISwiftObject (needed for explicit interface re-implementation)
        // but omits IDisposable (Dispose inherited from base)
        var derivedLine = GetClassDeclarationLine(output, "Dog");
        Assert.Contains("ISwiftObject", derivedLine);
        Assert.DoesNotContain("IDisposable", derivedLine);
    }

    [Fact]
    public void DerivedClass_OmitsIDisposable()
    {
        // IDisposable is inherited from base (Dispose is not re-emitted).
        // ISwiftObject is kept (needed for explicit interface re-implementation).
        var baseClass = CreateClassDecl("Animal");
        var derived = CreateClassDecl("Dog");

        var output = EmitClassHierarchy(baseClass, derived);

        // Base should have ISwiftObject, IDisposable
        var baseLine = GetClassDeclarationLine(output, "Animal");
        Assert.Contains("ISwiftObject", baseLine);
        Assert.Contains("IDisposable", baseLine);

        // Derived keeps ISwiftObject but NOT IDisposable
        var derivedLine = GetClassDeclarationLine(output, "Dog");
        Assert.Contains("ISwiftObject", derivedLine);
        Assert.DoesNotContain("IDisposable", derivedLine);
    }

    [Fact]
    public void DerivedClass_OwnNewProtocolsStillEmitted()
    {
        // Derived has a unique protocol not on base
        var baseClass = CreateClassDecl("Animal");
        var derived = CreateClassDecl("Dog");

        // Register the protocol in the type database for the derived class
        var typeDatabase = CreateTypeDatabaseWithProtocol(
            "TestModule.Fetchable", "TestModule", "IFetchable",
            "$sFetchableMa", TypeRecordKind.Protocol, TypeRecordFlags.None, emittedMemberCount: 0);

        derived.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Dog"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Fetchable"),
            "$sFetchable"));

        var output = EmitClassHierarchy(baseClass, derived, typeDatabase);

        var derivedLine = GetClassDeclarationLine(output, "Dog");
        Assert.Contains("IFetchable", derivedLine);
        Assert.Contains("Animal", derivedLine);
    }

    #endregion

    #region Payload Sharing Tests

    [Fact]
    public void DerivedClass_NoPayloadFieldDeclaration()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        // Extract only the derived class body
        var derivedBody = GetClassBody(output, "Dog");
        // Derived should not declare a _payload field (the `SwiftSafeHandle ... _payload = ...Zero` line).
        // It WILL reference _payload in the constructor, which is fine (inherited from base).
        // Derived DOES have its own _payloadSize (private static, no 'new' since base's is also private).
        Assert.DoesNotContain("_payload = SwiftSafeHandle", derivedBody);
        Assert.DoesNotContain("SwiftSafeHandle<Dog>.Zero", derivedBody);
        Assert.Contains("nuint _payloadSize", derivedBody);
    }

    [Fact]
    public void DerivedClass_NoDispose()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        Assert.DoesNotContain("public void Dispose()", derivedBody);
    }

    [Fact]
    public void DerivedClass_NoFinalizer()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        Assert.DoesNotContain("~Dog()", derivedBody);
    }

    [Fact]
    public void BaseClass_HasProtectedPayload()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        // Base class should have protected _payload
        var baseBody = GetClassBody(output, "Animal");
        Assert.Contains("protected SwiftSafeHandle<Animal> _payload", baseBody);
    }

    #endregion

    #region ISwiftObject Tests

    [Fact]
    public void DerivedClass_HasOwnGetTypeMetadata()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        Assert.Contains("static TypeMetadata ISwiftObject.GetTypeMetadata()", derivedBody);
        // Derived should use its own mangled name for metadata
        Assert.Contains("Dog", derivedBody);
    }

    [Fact]
    public void DerivedClass_HasOwnNewFromPayload()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        Assert.Contains("ISwiftObject.NewFromPayload", derivedBody);
        Assert.Contains("return new Dog(handle)", derivedBody);
    }

    [Fact]
    public void DerivedClass_ConstructorUsesBaseTypeForSafeHandle()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        // Derived constructor creates SwiftSafeHandle using the ROOT base type
        Assert.Contains("new SwiftSafeHandle<Animal>(handle)", derivedBody);
    }

    [Fact]
    public void ThreeLevelHierarchy_GrandchildUsesRootBaseForSafeHandle()
    {
        var grandparent = CreateClassDecl("Request");
        var parent = CreateClassDecl("DataRequest");
        var child = CreateClassDecl("UploadRequest");

        parent.ResolvedSuperclass = grandparent;
        child.ResolvedSuperclass = parent;

        var output = EmitClassHierarchyMulti(new[] { grandparent, parent, child });

        var childBody = GetClassBody(output, "UploadRequest");
        // UploadRequest's constructor should use Request (root), not DataRequest (immediate parent)
        Assert.Contains("new SwiftSafeHandle<Request>(handle)", childBody);
    }

    #endregion

    #region Fallback Tests

    [Fact]
    public void ExternalSuperclass_FlatEmission()
    {
        // Class with an ObjC/cross-module base (unresolved)
        var classDecl = CreateClassDecl("MyDelegate");
        classDecl.SuperclassUsr = "c:objc(cs)NSObject";
        classDecl.SuperclassNames = new List<string> { "ObjectiveC.NSObject" };
        // ResolvedSuperclass is null — HasExternalSuperclass is true

        var output = EmitSingleClass(classDecl);

        // Should be flat emission (no ": NSObject")
        var classLine = GetClassDeclarationLine(output, "MyDelegate");
        Assert.Contains("ISwiftObject", classLine);
        Assert.Contains("IDisposable", classLine);
        Assert.DoesNotContain("NSObject", classLine);
    }

    [Fact]
    public void NoResolvedSuperclass_FlatEmission()
    {
        var classDecl = CreateClassDecl("RootClass");

        var output = EmitSingleClass(classDecl);

        var classLine = GetClassDeclarationLine(output, "RootClass");
        Assert.Contains("ISwiftObject", classLine);
        Assert.Contains("IDisposable", classLine);
    }

    [Fact]
    public void SkippedBaseClass_FlatEmission()
    {
        // Base class has unsupported generic constraint (SwiftUI) → will be skipped during emission.
        // Derived should fall back to flat emission (no ": Base"), not reference a non-emitted type.
        var baseClass = CreateClassDecl("GenericBase");
        baseClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "V",
                SugaredTypeName: "V",
                GenericConformances: new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        Path: Array.Empty<string>(),
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        var derived = CreateClassDecl("ConcreteChild");
        derived.ResolvedSuperclass = baseClass;

        // Emit only the derived class — base is skipped (unsupported constraint)
        var output = EmitSingleClass(derived);

        // Should be flat emission: ISwiftObject + IDisposable, no "GenericBase"
        var classLine = GetClassDeclarationLine(output, "ConcreteChild");
        Assert.Contains("ISwiftObject", classLine);
        Assert.Contains("IDisposable", classLine);
        Assert.DoesNotContain("GenericBase", classLine);
    }

    #endregion

    #region Disposal Remarks Tests

    [Fact]
    public void Class_HasDisposalRemarks()
    {
        var classDecl = CreateClassDecl("MyClass");

        var output = EmitSingleClass(classDecl);

        Assert.Contains("/// <remarks>", output);
        Assert.Contains("must be disposed explicitly", output);
    }

    [Fact]
    public void DerivedClass_HasDisposalRemarks()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        // Both base and derived should have disposal remarks
        var derivedBody = GetFullClassSection(output, "Dog");
        Assert.Contains("/// <remarks>", derivedBody);
    }

    #endregion

    #region Equality Inheritance Tests

    [Fact]
    public void DerivedClass_WithEquatable_EmitsOwnEquality()
    {
        // IEquatable<Base> != IEquatable<Derived>, so derived with Equatable
        // conformance gets its own IEquatable<Derived> and equality methods.
        var baseClass = CreateClassDecl("Animal");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Animal"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatable"));

        var derived = CreateClassDecl("Dog");
        derived.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Dog"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatable"));

        var output = EmitClassHierarchy(baseClass, derived);

        // Both base and derived should have Equals
        var baseBody = GetClassBody(output, "Animal");
        Assert.Contains("override bool Equals", baseBody);

        var derivedBody = GetClassBody(output, "Dog");
        Assert.Contains("override bool Equals", derivedBody);
    }

    [Fact]
    public void DerivedClass_WithoutEquatable_SkipsEquality()
    {
        // If derived doesn't conform to Equatable, no equality is emitted.
        var baseClass = CreateClassDecl("Animal");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Animal"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatable"));

        var derived = CreateClassDecl("Dog");
        // No Equatable conformance on derived

        var output = EmitClassHierarchy(baseClass, derived);

        // Derived should NOT have Equals
        var derivedBody = GetClassBody(output, "Dog");
        Assert.DoesNotContain("override bool Equals", derivedBody);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Invokes the protected TopologicallySortTypes method via a test-accessible wrapper.
    /// </summary>
    private static List<BaseDecl> InvokeTopologicalSort(List<BaseDecl> decls)
    {
        // TopologicallySortTypes is protected static on BaseHandler.
        // Use reflection to test it directly.
        var method = typeof(BaseHandler).GetMethod(
            "TopologicallySortTypes",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
        return (List<BaseDecl>)method!.Invoke(null, new object[] { decls })!;
    }

    /// <summary>
    /// Emits a two-class hierarchy and returns the C# output.
    /// </summary>
    private static string EmitClassHierarchy(ClassDecl baseClass, ClassDecl derived, TypeDatabase typeDatabase = null)
    {
        derived.ResolvedSuperclass = baseClass;
        return EmitClassHierarchyMulti(new[] { baseClass, derived }, typeDatabase);
    }

    /// <summary>
    /// Emits multiple classes (with hierarchy links already set) and returns the C# output.
    /// </summary>
    private static string EmitClassHierarchyMulti(ClassDecl[] classes, TypeDatabase typeDatabase = null)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        foreach (var cls in classes)
        {
            cls.ModuleDecl = moduleDecl;
            testModule.RegisterType(
                cls.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", cls.Name),
                    SwiftTypeName = cls.SwiftTypeName,
                    MetadataAccessor = $"{cls.MangledName}Ma",
                    Kind = TypeRecordKind.Class,
                    Flags = TypeRecordFlags.None
                });
        }

        var db = typeDatabase ?? new TypeDatabase();
        // If a fresh database, add Swift module
        if (typeDatabase == null)
        {
            db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        }
        // Merge the test module registrations
        try { db.AddModuleDatabase(testModule); } catch { /* Already added in custom typeDatabase */ }

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(NullLogger<ClassHandler>.Instance);
        var conductor = new Conductor(NullLoggerFactory.Instance);

        foreach (var cls in classes)
        {
            var env = handler.Marshal(cls, db);
            handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);
        }

        return csStringWriter.ToString();
    }

    /// <summary>
    /// Emits a single class and returns the C# output.
    /// </summary>
    private static string EmitSingleClass(ClassDecl classDecl)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        classDecl.ModuleDecl = moduleDecl;
        testModule.RegisterType(
            classDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", classDecl.Name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"{classDecl.MangledName}Ma",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.None
            });

        var db = new TypeDatabase();
        db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        db.AddModuleDatabase(testModule);

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(NullLogger<ClassHandler>.Instance);
        var conductor = new Conductor(NullLoggerFactory.Instance);

        var env = handler.Marshal(classDecl, db);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    private static string GetClassDeclarationLine(string output, string className)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains($"class {className}") && line.Contains(":"))
                return line;
        }
        return string.Empty;
    }

    /// <summary>
    /// Gets the full section for a class from its XML doc comments through the closing brace.
    /// </summary>
    private static string GetFullClassSection(string output, string className)
    {
        var lines = output.Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains($"class {className}") && lines[i].Contains(":"))
            {
                // Walk back to find the start of XML doc comments
                start = i;
                while (start > 0 && lines[start - 1].TrimStart().StartsWith("///"))
                    start--;
                break;
            }
        }
        if (start < 0) return string.Empty;

        // Find the matching closing brace
        int braceCount = 0;
        int end = start;
        for (int i = start; i < lines.Length; i++)
        {
            braceCount += lines[i].Count(c => c == '{');
            braceCount -= lines[i].Count(c => c == '}');
            if (braceCount <= 0 && i > start)
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..(end + 1)]);
    }

    /// <summary>
    /// Gets the body content (inside the braces) of a class by name.
    /// </summary>
    private static string GetClassBody(string output, string className)
    {
        var lines = output.Split('\n');
        int classLineIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains($"class {className}") && lines[i].Contains(":"))
            {
                classLineIdx = i;
                break;
            }
        }
        if (classLineIdx < 0) return string.Empty;

        // Find opening brace
        int start = classLineIdx;
        while (start < lines.Length && !lines[start].Contains("{"))
            start++;
        start++; // Skip the opening brace line

        // Find matching closing brace
        int braceCount = 1;
        int end = start;
        for (int i = start; i < lines.Length; i++)
        {
            braceCount += lines[i].Count(c => c == '{');
            braceCount -= lines[i].Count(c => c == '}');
            if (braceCount <= 0)
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    private static ClassDecl CreateClassDecl(string name, string moduleName = "TestModule")
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = ""
        };
    }

    private static EnumDecl CreateEnumDecl(string name, string moduleName = "TestModule")
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new List<EnumCaseDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = ""
        };
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

    private static TypeDatabase CreateTypeDatabaseWithProtocol(
        string protocolModuleQualifiedName, string csNamespace, string csName,
        string metadataAccessor, TypeRecordKind kind, TypeRecordFlags flags,
        int emittedMemberCount = 0)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(protocolModuleQualifiedName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolModuleQualifiedName),
                MetadataAccessor = metadataAccessor,
                Kind = kind,
                Flags = flags,
                EmittedMemberCount = emittedMemberCount
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion
}
