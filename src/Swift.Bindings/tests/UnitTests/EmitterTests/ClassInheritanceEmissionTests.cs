// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for class inheritance emission: topological sort, declaration syntax,
/// payload sharing, ISwiftObject handling, and fallback behavior.
/// </summary>
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
    public void RootClass_NoPayloadSizeField()
    {
        // Classes do NOT emit _payloadSize — only structs/enums use it for allocation.
        // Class constructors use Unmanaged.passRetained().toOpaque(), not _payloadSize.
        // Emitting _payloadSize triggers SwiftObjectHelper<T>.GetTypeMetadata().Size at
        // class load time, which can cause SIGABRT.
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var baseBody = GetClassBody(output, "Animal");
        Assert.DoesNotContain("nuint _payloadSize", baseBody);
    }

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
        // Classes do NOT emit _payloadSize — only structs/enums use it for allocation.
        // Class constructors use Unmanaged.passRetained().toOpaque().
        Assert.DoesNotContain("_payload = SwiftSafeHandle", derivedBody);
        Assert.DoesNotContain("SwiftSafeHandle<Dog>.Zero", derivedBody);
        Assert.DoesNotContain("nuint _payloadSize", derivedBody);
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

        // Base class should have protected _handle
        var baseBody = GetClassBody(output, "Animal");
        Assert.Contains("protected SwiftClassHandle<Animal> _handle", baseBody);
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
        Assert.Contains("var obj = new Dog(new SwiftHandle(handle))", derivedBody);
        Assert.Contains("Swift.Runtime.SwiftDisposeScope.TryRegister(obj)", derivedBody);
        Assert.Contains("return obj", derivedBody);
    }

    [Fact]
    public void Class_DeclaresAdoptPayloadConstructionSemantics()
    {
        // Finding 11: every emitted ISwiftObject must declare its PayloadConstructionSemantics
        // (the static-abstract forcing function). A Swift class' NewFromPayload wraps the wire handle
        // directly into the SafeHandle, so it adopts — the seam reads Adopt and leaves the temp alone.
        var output = EmitSingleClass(CreateClassDecl("Widget"));

        var body = GetClassBody(output, "Widget");
        Assert.Contains("ISwiftObject.PayloadConstructionSemantics", body);
        Assert.Contains("global::Swift.Runtime.PayloadConstructionSemantics.Adopt", body);
    }

    [Fact]
    public void DerivedClass_ConstructorUsesBaseTypeForSafeHandle()
    {
        var output = EmitClassHierarchy(
            baseClass: CreateClassDecl("Animal"),
            derived: CreateClassDecl("Dog"));

        var derivedBody = GetClassBody(output, "Dog");
        // Derived constructor creates SwiftClassHandle using the ROOT base type
        Assert.Contains("new SwiftClassHandle<Animal>(handle)", derivedBody);
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
        Assert.Contains("new SwiftClassHandle<Request>(handle)", childBody);
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

    [Fact]
    public void SkippedBaseClass_FlatEmission_ConstructorHandleTypeMatchesPayload()
    {
        // P1 regression: When a class falls back to flat emission because its base is non-emittable,
        // the private constructor's SwiftSafeHandle<T> type must match the _payload field's type.
        // Both should use the current class type, not the non-emittable base.
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

        var output = EmitSingleClass(derived);
        var body = GetClassBody(output, "ConcreteChild");

        // _handle field should be SwiftClassHandle<ConcreteChild>, not SwiftClassHandle<GenericBase<V>>
        Assert.Contains("SwiftClassHandle<ConcreteChild> _handle", body);
        // Private constructor should also use SwiftClassHandle<ConcreteChild>
        Assert.Contains("new SwiftClassHandle<ConcreteChild>(handle)", body);
        // Should NOT reference the non-emittable base type anywhere in the handle types
        Assert.DoesNotContain("SwiftSafeHandle<GenericBase", body);
    }

    [Fact]
    public void SkippedBaseClass_NoNewModifier_AvoidsCS0109()
    {
        // When IsEffectivelyDerived returns false (the Swift superclass doesn't surface
        // as a C# base class because it has unsupported constraints), the C# declaration
        // has no base class — it inherits implicitly from System.Object, which has none
        // of the handle/Payload/Dispose members. Emitting `new` on those declarations
        // produces CS0109 ("the new keyword is not required, no member is hidden").
        // Verified end-to-end against WCDB: the prior emission produced 376 CS0109
        // warnings; gating on actual emission of an in-tree base class drops it to 0.
        var baseClass = CreateClassDecl("SwiftUIBase");
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

        var derived = CreateClassDecl("ConcreteView");
        derived.ResolvedSuperclass = baseClass;
        derived.SuperclassNames = new List<string> { "SwiftUIBase" };

        var output = EmitSingleClass(derived);
        var body = GetClassBody(output, "ConcreteView");

        Assert.Contains("protected SwiftClassHandle<ConcreteView> _handle", body);
        Assert.Contains("public SwiftClassHandle<ConcreteView> Payload", body);
        Assert.DoesNotContain("new protected SwiftClassHandle", body);
        Assert.DoesNotContain("new public SwiftClassHandle", body);
        Assert.DoesNotContain("new void Dispose", body);
    }

    [Fact]
    public void VariadicPackBaseClass_FallsBackToFlatEmission()
    {
        // A base class with a variadic generic parameter pack (`each T`) is skipped
        // entirely by ClassHandler (no C# equivalent), so a subclass must NOT be
        // emitted as `: GenericPackBase<...>` — that would reference a type that is
        // never declared and fail the binding compile with CS0246. The derived class
        // must fall back to flat emission, exactly like the unsupported-constraint
        // skipped-base case above.
        var baseClass = CreateClassDecl("GenericPackBase");
        baseClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "each T",
                SugaredTypeName: "each T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        var derived = CreateClassDecl("PackChild");
        derived.ResolvedSuperclass = baseClass;
        derived.SuperclassNames = new List<string> { "GenericPackBase" };

        Assert.False(ClassHandler.IsEffectivelyDerived(derived));

        var output = EmitSingleClass(derived);
        var body = GetClassBody(output, "PackChild");

        // Flat emission: own handle typing, no reference to the never-emitted base.
        Assert.Contains("protected SwiftClassHandle<PackChild> _handle", body);
        Assert.DoesNotContain("GenericPackBase", output);
    }

    [Fact]
    public void PrePassSkippedBaseClass_FallsBackToFlatEmission()
    {
        // A base class can be skipped for a reason the decl-only ancestor predicates
        // cannot see (e.g. an indeterminate PWT shape, which needs the type database).
        // TypeSkipPrePass records every such type into ReportCollector before handlers
        // run; the ancestor gate must consult that record so the subclass falls back
        // to flat emission instead of referencing a never-emitted base (CS0246).
        var baseClass = CreateClassDecl("PrePassSkippedBase");
        var derived = CreateClassDecl("PrePassChild");
        derived.ResolvedSuperclass = baseClass;
        derived.SuperclassNames = new List<string> { "PrePassSkippedBase" };

        var moduleDecl = CreateModuleDecl("TestModule");
        baseClass.ModuleDecl = moduleDecl;
        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(
                baseClass, SkipReason.IndeterminatePwtShape, "test-recorded skip");

            Assert.False(ClassHandler.IsEffectivelyDerived(derived));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void UnresolvedSuperclass_NoNewModifier_AvoidsCS0109()
    {
        // Same root cause as the skipped-base case: when ResolvedSuperclass is null
        // (cross-module base not in the in-tree TypeDatabase), IsEffectivelyDerived
        // returns false and the emitted C# class has no base class. `new` would
        // produce CS0109.
        var child = CreateClassDecl("ChildClass");
        child.SuperclassNames = new List<string> { "ExternalBase" };
        // ResolvedSuperclass intentionally left null

        var output = EmitSingleClass(child);
        var body = GetClassBody(output, "ChildClass");

        Assert.Contains("protected SwiftClassHandle<ChildClass> _handle", body);
        Assert.Contains("public SwiftClassHandle<ChildClass> Payload", body);
        Assert.DoesNotContain("new protected SwiftClassHandle", body);
        Assert.DoesNotContain("new public SwiftClassHandle", body);
    }

    [Fact]
    public void RootClass_NoNewModifier()
    {
        // Root class (no superclass) should NOT have `new` modifiers.
        var root = CreateClassDecl("StandaloneClass");

        var output = EmitSingleClass(root);
        var body = GetClassBody(output, "StandaloneClass");

        Assert.Contains("protected SwiftClassHandle<StandaloneClass> _handle", body);
        Assert.DoesNotContain("new protected", body);
        Assert.DoesNotContain("new public", body);
    }

    [Fact]
    public void ManyClassesWithSharedUnresolvedBase_NoNewModifierAnywhere()
    {
        // Mirrors WCDB's `Statement*` family: many derived classes share a single
        // unresolved (cross-module / non-emittable) base. WCDB used to emit 376
        // CS0109 warnings here — one per `new`-modified handle/Payload/Dispose member
        // across every derived class. After the gate fix, none of the per-class
        // emissions should carry a `new` modifier.
        var siblings = new[]
        {
            "StatementSelect", "StatementUpdate", "StatementInsert",
            "StatementDelete", "StatementDropTrigger", "StatementVacuum",
        };

        foreach (var name in siblings)
        {
            var derived = CreateClassDecl(name);
            derived.SuperclassNames = new List<string> { "StatementBase" };
            // ResolvedSuperclass intentionally null — base lives outside the in-tree TypeDatabase.

            var output = EmitSingleClass(derived);
            var body = GetClassBody(output, name);

            Assert.Contains($"protected SwiftClassHandle<{name}> _handle", body);
            Assert.DoesNotContain("new protected", body);
            Assert.DoesNotContain("new public", body);
            Assert.DoesNotContain("new void Dispose", body);
        }
    }

    #endregion

    #region Disposal Remarks Tests

    [Fact]
    public void Class_HasDisposalRemarks()
    {
        var classDecl = CreateClassDecl("MyClass");

        var output = EmitSingleClass(classDecl);

        Assert.Contains("/// <remarks>", output);
        // ARC bridge: classes use SwiftClassHandle with automatic ARC release
        Assert.Contains("deterministic cleanup", output);
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

    #region Virtual/Override Dispatch Tests

    [Fact]
    public void NonFinalClass_InstanceMethod_EmitsVirtual()
    {
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: false);
        Assert.Contains("public virtual void Describe()", csOutput);
    }

    [Fact]
    public void FinalClass_InstanceMethod_NoVirtual()
    {
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: true);
        Assert.Contains("public void Describe()", csOutput);
        Assert.DoesNotContain("virtual", csOutput);
    }

    [Fact]
    public void NonFinalClass_FinalMethod_NoVirtual()
    {
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: false, methodIsFinal: true);
        Assert.Contains("public void Describe()", csOutput);
        Assert.DoesNotContain("virtual", csOutput);
    }

    [Fact]
    public void OverrideMethod_WithResolvedBase_EmitsOverride()
    {
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: false,
            methodIsOverride: true, hasResolvedBase: true);
        Assert.Contains("public override void Describe()", csOutput);
    }

    [Fact]
    public void SealedOverrideMethod_WithResolvedBase_EmitsSealedOverride()
    {
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: false,
            methodIsOverride: true, methodIsFinal: true, hasResolvedBase: true);
        Assert.Contains("public sealed override void Describe()", csOutput);
    }

    [Fact]
    public void OverrideMethod_WithExternalBase_EmitsVirtualNotOverride()
    {
        // When a class has an external superclass (e.g., NSObject) with no C# base,
        // override keyword would cause CS0115. Emit virtual instead.
        var (csOutput, _) = EmitMethodOnClass("describe", classIsFinal: false,
            methodIsOverride: true, hasResolvedBase: false);
        Assert.Contains("public virtual void Describe()", csOutput);
        Assert.DoesNotContain("override", csOutput);
    }

    [Fact]
    public void StaticMethod_NoVirtualOrOverride()
    {
        var (csOutput, _) = EmitMethodOnClass("create", classIsFinal: false, isStatic: true);
        Assert.Contains("public static void Create()", csOutput);
        Assert.DoesNotContain("virtual", csOutput);
        Assert.DoesNotContain("override", csOutput);
    }

    [Fact]
    public void Constructor_NoVirtualOrOverride()
    {
        // Constructors go through ConstructorHandler, not MethodHandler.
        // But we can verify at the model level: IsOverride should be parsed but not used.
        var method = CreateVoidMethodDecl("init", isOverride: true);
        method.IsConstructor = true;
        Assert.True(method.IsOverride);
        Assert.True(method.IsConstructor);
        // (Constructor emission is covered by ConstructorHandlerOutputTests;
        //  virtual/override logic in WrapperEmitter.Signature.cs explicitly excludes constructors.)
    }

    [Fact]
    public void AccessorMethod_NoVirtualOrOverride()
    {
        // Accessor methods are private helpers; the property declaration carries the modifier.
        var (csOutput, _) = EmitMethodOnClass("name_Get", classIsFinal: false, isAccessor: true);
        Assert.DoesNotContain("virtual", csOutput);
        Assert.DoesNotContain("override", csOutput);
    }

    [Fact]
    public void NonFinalClass_Property_EmitsVirtualOnProperty()
    {
        var classDecl = CreateClassDecl("Animal");
        var prop = CreateSimplePropertyDecl("name");
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public virtual string Name", csOutput);
    }

    [Fact]
    public void FinalProperty_OnNonFinalClass_NoVirtual()
    {
        var classDecl = CreateClassDecl("Animation");
        var prop = CreateSimplePropertyDecl("startFrame", isFinal: true);
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public string StartFrame", csOutput);
        Assert.DoesNotContain("virtual", csOutput);
    }

    [Fact]
    public void OverrideProperty_WithResolvedBase_EmitsOverride()
    {
        var baseClass = CreateClassDecl("Animal");
        // Base class must have the matching property for override resolution
        var baseProp = CreateSimplePropertyDecl("name");
        baseProp.ParentDecl = baseClass;
        baseProp.WasEmitted = true;
        baseClass.Properties.Add(baseProp);
        var classDecl = CreateClassDecl("Dog");
        classDecl.ResolvedSuperclass = baseClass;
        var prop = CreateSimplePropertyDecl("name", isOverride: true);
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public override string Name", csOutput);
    }

    [Fact]
    public void SealedOverrideProperty_WithResolvedBase_EmitsSealedOverride()
    {
        var baseClass = CreateClassDecl("Animal");
        var baseProp = CreateSimplePropertyDecl("name");
        baseProp.ParentDecl = baseClass;
        baseProp.WasEmitted = true;
        baseClass.Properties.Add(baseProp);
        var classDecl = CreateClassDecl("Dog");
        classDecl.ResolvedSuperclass = baseClass;
        var prop = CreateSimplePropertyDecl("name", isOverride: true, isFinal: true);
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public sealed override string Name", csOutput);
    }

    [Fact]
    public void OverrideProperty_WithExternalBase_EmitsVirtualNotOverride()
    {
        // When class has external superclass (no resolved C# base), emit virtual instead of override.
        var classDecl = CreateClassDecl("AnimatedControl");
        classDecl.SuperclassNames.Add("UIKit.UIControl");
        // ResolvedSuperclass is null — external base
        var prop = CreateSimplePropertyDecl("isEnabled", isOverride: true);
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public virtual string IsEnabled", csOutput);
        Assert.DoesNotContain("override", csOutput);
    }

    [Fact]
    public void StaticProperty_NoVirtualOrOverride()
    {
        var classDecl = CreateClassDecl("Animal");
        var prop = CreateSimplePropertyDecl("species", isStatic: true);
        prop.ParentDecl = classDecl;
        classDecl.Properties.Add(prop);

        var csOutput = EmitPropertyOnClass(prop, classDecl);
        Assert.Contains("public static string Species", csOutput);
        Assert.DoesNotContain("virtual", csOutput);
    }

    [Fact]
    public void OverrideMethod_BaseOverloadSkipped_AncestorCheckReturnsFalse()
    {
        // Base class has two overloads: foo(Swift.Int) emitted, foo(Swift.String) skipped.
        // Derived class overrides foo(Swift.String). The guard must not match foo(Swift.Int)
        // and incorrectly return true — that would cause CS0115 from emitting "override".
        var baseClass = CreateClassDecl("Base");

        // foo(Swift.Int) — emitted
        var baseFooInt = CreateMethodDeclWithParam("foo", "Swift.Int", "value");
        baseFooInt.ParentDecl = baseClass;
        baseFooInt.WasEmitted = true;
        baseClass.Methods.Add(baseFooInt);

        // foo(Swift.String) — skipped by validation gates
        var baseFooString = CreateMethodDeclWithParam("foo", "Swift.String", "value");
        baseFooString.ParentDecl = baseClass;
        baseFooString.WasEmitted = false;
        baseClass.Methods.Add(baseFooString);

        var derivedClass = CreateClassDecl("Derived");
        derivedClass.ResolvedSuperclass = baseClass;

        // Derived overrides foo(Swift.String) — but base's foo(Swift.String) was not emitted
        var derivedFoo = CreateMethodDeclWithParam("foo", "Swift.String", "value");
        derivedFoo.IsOverride = true;

        // Guard should return false — the specific overload was not emitted
        Assert.False(WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedFoo));
    }

    [Fact]
    public void OverrideMethod_BaseOverloadEmitted_AncestorCheckReturnsTrue()
    {
        // Complementary test: when the matching overload IS emitted, the guard returns true.
        var baseClass = CreateClassDecl("Base");

        // foo(Swift.String) — emitted
        var baseFooString = CreateMethodDeclWithParam("foo", "Swift.String", "value");
        baseFooString.ParentDecl = baseClass;
        baseFooString.WasEmitted = true;
        baseClass.Methods.Add(baseFooString);

        // foo(Swift.Int) — also emitted (different overload)
        var baseFooInt = CreateMethodDeclWithParam("foo", "Swift.Int", "value");
        baseFooInt.ParentDecl = baseClass;
        baseFooInt.WasEmitted = true;
        baseClass.Methods.Add(baseFooInt);

        var derivedClass = CreateClassDecl("Derived");
        derivedClass.ResolvedSuperclass = baseClass;

        // Derived overrides foo(Swift.String) — base's foo(Swift.String) was emitted
        var derivedFoo = CreateMethodDeclWithParam("foo", "Swift.String", "value");
        derivedFoo.IsOverride = true;

        Assert.True(WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedFoo));
    }

    [Fact]
    public void CrossModuleOverride_ParentRecord_HasMatchingEmittedMethod_ReturnsTrue()
    {
        // The cross-module immediate-parent path (WrapperEmitter.Signature.cs:366) must
        // verify against the parent module's persisted EmittedClassMethods rather than
        // blindly trusting Swift's IsOverride bit. Happy path: parent emitted describe(),
        // child overrides it → emit C# override.
        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyBaseEntity" };
        derivedClass.CrossModuleSuperclassRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            EmittedClassMethods = new List<EmittedClassMethod>
            {
                new("describe", "Describe", Array.Empty<string>()),
                new("tag", "Tag", Array.Empty<string>()),
            },
        };
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyBaseEntity";

        var derivedDescribe = CreateVoidMethodDecl("describe", isOverride: true);
        derivedDescribe.ParentDecl = derivedClass;

        Assert.True(WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedDescribe));
    }

    [Fact]
    public void CrossModuleOverride_ParentRecord_MissingMethod_ReturnsFalse()
    {
        // Defensive case: the parent's binding generation skipped describe() (e.g. validation
        // gate dropped it because of an unsupported parameter type), so EmittedClassMethods
        // does NOT contain it. Without the verifier the emitter would trust
        // Swift's IsOverride bit and write `override`, producing CS0115 in the child's C# build.
        // The verifier returns false so the caller falls back to `virtual` instead.
        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyBaseEntity" };
        derivedClass.CrossModuleSuperclassRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            // Parent's binding emitted only `tag()` — describe() was skipped.
            EmittedClassMethods = new List<EmittedClassMethod>
            {
                new("tag", "Tag", Array.Empty<string>()),
            },
        };
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyBaseEntity";

        var derivedDescribe = CreateVoidMethodDecl("describe", isOverride: true);
        derivedDescribe.ParentDecl = derivedClass;

        Assert.False(WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedDescribe));
    }

    [Fact]
    public void CrossModuleOverride_MethodOnGrandparent_WalksRecordChain()
    {
        // Swift `override` binds to whichever ancestor first declared the virtual slot. If the
        // immediate cross-module parent doesn't redeclare describe() but its grandparent does,
        // the verifier must walk up via TypeRecord.SuperclassTypeName and find the slot on the
        // grandparent's record — otherwise it would falsely reject a perfectly valid override.
        var typeDatabase = new TypeDatabase();
        var grandparentName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity");
        var parentName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyMidEntity");

        var grandparentRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = grandparentName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            EmittedClassMethods = new List<EmittedClassMethod>
            {
                new("describe", "Describe", Array.Empty<string>()),
            },
        };
        var parentRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyMidEntity"),
            SwiftTypeName = parentName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            SuperclassTypeName = grandparentName,
            // Parent does NOT redeclare describe() — the slot lives on the grandparent.
            EmittedClassMethods = new List<EmittedClassMethod>(),
        };
        var parentModule = new ModuleTypeDatabase("ParentModule", "/fake/ParentModule.dylib");
        parentModule.RegisterType(grandparentName, grandparentRecord);
        parentModule.RegisterType(parentName, parentRecord);
        typeDatabase.AddModuleDatabase(parentModule);

        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyMidEntity" };
        derivedClass.CrossModuleSuperclassRecord = parentRecord;
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyMidEntity";

        var derivedDescribe = CreateVoidMethodDecl("describe", isOverride: true);
        derivedDescribe.ParentDecl = derivedClass;

        Assert.True(WrapperEmitter.HasMethodInResolvedAncestors(
            derivedClass, derivedDescribe, derivedCSharpName: null, typeDatabase: typeDatabase));
    }

    [Fact]
    public void CrossModuleOverride_ParentRecord_LegacyNullList_ReturnsTrue()
    {
        // Backward compatibility: parent module XML databases generated before the
        // EmittedClassMethods field existed leave the property null. The verifier must NOT
        // fail-closed in this case — that would break already-published parent NuGets when a
        // child upgrades to a new generator. Treat null as "unverifiable", trust the Swift
        // IsOverride bit, and preserve the v0.8.x behavior. Newly generated parents (with
        // a populated list) get the strict verification path.
        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyBaseEntity" };
        derivedClass.CrossModuleSuperclassRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            EmittedClassMethods = null,
        };
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyBaseEntity";

        var derivedDescribe = CreateVoidMethodDecl("describe", isOverride: true);
        derivedDescribe.ParentDecl = derivedClass;

        Assert.True(WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedDescribe));
    }

    [Fact]
    public void CrossModuleOverride_ParentRecord_CSharpNameMismatch_ReturnsFalse()
    {
        // NameProvider can rename methods in the parent binding due to property/nested-type
        // collisions or self-returning builder rules — `tag()` returning Self in a class with
        // a `tag` property becomes `WithTag()`, while in a class without that property it
        // stays `Tag()`. Swift name + parameter types alone are NOT sufficient to verify the
        // override target; the verifier must compare the persisted C# name with the derived
        // class's C# name. Here the parent emitted `WithTag()` (because of a builder-pattern
        // rename) but the derived class emits the unqualified `Tag()` — these are different
        // C# methods, and the verifier must reject the override (otherwise CS0115 in the
        // child's C# build).
        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyBaseEntity" };
        derivedClass.CrossModuleSuperclassRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            EmittedClassMethods = new List<EmittedClassMethod>
            {
                new("tag", "WithTag", Array.Empty<string>()),
            },
        };
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyBaseEntity";

        var derivedTag = CreateVoidMethodDecl("tag", isOverride: true);
        derivedTag.ParentDecl = derivedClass;

        // derivedCSharpName "Tag" disagrees with the parent's persisted "WithTag" — reject.
        Assert.False(WrapperEmitter.HasMethodInResolvedAncestors(
            derivedClass, derivedTag, derivedCSharpName: "Tag"));
    }

    [Fact]
    public void CrossModuleOverride_ParentRecord_LegacyEmptyCSharpName_SkipsNameCheck()
    {
        // Backward compat: a parent module XML database generated before EmittedClassMethod
        // gained the CSharpName field deserializes with CSharpName = "" (the missing-attribute
        // default in TypeDatabase.ReadVersion1_0). The verifier must NOT compare against an
        // empty C# name — that would falsely reject every override on legacy records. Empty
        // means "skip the C# name check, fall back to Swift-name-and-params parity".
        var derivedClass = CreateClassDecl("LocalChildEntity", moduleName: "ChildModule");
        derivedClass.SuperclassNames = new List<string> { "ParentModule.DependencyBaseEntity" };
        derivedClass.CrossModuleSuperclassRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ParentModule", "DependencyBaseEntity"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ParentModule.DependencyBaseEntity"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            // Legacy: list is populated but CSharpName missing → empty string
            EmittedClassMethods = new List<EmittedClassMethod>
            {
                new("describe", string.Empty, Array.Empty<string>()),
            },
        };
        derivedClass.CrossModuleSuperclassCSharpName = "ParentModule.DependencyBaseEntity";

        var derivedDescribe = CreateVoidMethodDecl("describe", isOverride: true);
        derivedDescribe.ParentDecl = derivedClass;

        // Even with derivedCSharpName supplied, the empty persisted CSharpName must not
        // cause a false-negative — match by Swift name + params and accept.
        Assert.True(WrapperEmitter.HasMethodInResolvedAncestors(
            derivedClass, derivedDescribe, derivedCSharpName: "Describe"));
    }

    [Fact]
    public void Populator_PreservesEmittedCSharpName_WhenDisambiguated()
    {
        // When two Swift overloads project to the same C# signature, IHandler.HandleBaseDecl
        // resolves each one's name from its own labels/types — a bare `process()` and a
        // `process(value:)` become `Process` and `ProcessValue`. The conductor stamps the
        // resolved name on MethodDecl.EmittedCSharpName. The populator must read THAT value,
        // not recompute via NameProvider (which sees only one method at a time and would
        // produce `Process` for both, corrupting the cross-module override contract).
        var classDecl = CreateClassDecl("Worker", moduleName: "TestModule");
        var first = CreateVoidMethodDecl("process");
        first.WasEmitted = true;
        first.EmittedCSharpName = "Process";
        first.ParentDecl = classDecl;
        var second = CreateMethodDeclWithParam("process", "Swift.Int", "value");
        second.WasEmitted = true;
        second.EmittedCSharpName = "ProcessValue"; // Disambiguated name from emission
        second.ParentDecl = classDecl;
        classDecl.Methods.Add(first);
        classDecl.Methods.Add(second);

        var module = CreateModuleDecl("TestModule");
        module.Types.Add(classDecl);

        var typeDatabase = new TypeDatabase();
        var moduleDatabase = new ModuleTypeDatabase("TestModule", "/fake/TestModule.dylib");
        moduleDatabase.RegisterType(classDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Worker"),
            SwiftTypeName = classDecl.SwiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(moduleDatabase);

        ClassHandler.PopulateEmittedClassMethods(module, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var record));
        Assert.NotNull(record!.EmittedClassMethods);
        Assert.Equal(2, record!.EmittedClassMethods!.Count);

        // First overload: no collision, stamped as "Process".
        var firstEntry = record.EmittedClassMethods!.Single(m => m.ParameterSwiftTypes.Count == 0);
        Assert.Equal("Process", firstEntry.CSharpName);

        // Second overload: disambiguated name preserved as "ProcessValue", NOT recomputed to "Process".
        var secondEntry = record.EmittedClassMethods!.Single(m => m.ParameterSwiftTypes.Count == 1);
        Assert.Equal("ProcessValue", secondEntry.CSharpName);
    }

    [Fact]
    public void Populator_FallsBackToComputed_WhenEmittedCSharpNameIsNull()
    {
        // Synthesized methods (e.g., ConcreteProtocolSpecializationEmitter outputs) bypass the
        // IHandler conductor that stamps EmittedCSharpName. They do NOT participate in
        // projected-signature collision tracking, so collisionIndex would be 0 for them anyway —
        // recomputing via NameProvider is safe and matches the actual emitted name. The
        // populator must accept the null-stamp case and produce a sensible CSharpName.
        var classDecl = CreateClassDecl("Worker", moduleName: "TestModule");
        var method = CreateVoidMethodDecl("describe");
        method.WasEmitted = true;
        method.EmittedCSharpName = null; // Synthesized path didn't stamp
        method.ParentDecl = classDecl;
        classDecl.Methods.Add(method);

        var module = CreateModuleDecl("TestModule");
        module.Types.Add(classDecl);

        var typeDatabase = new TypeDatabase();
        var moduleDatabase = new ModuleTypeDatabase("TestModule", "/fake/TestModule.dylib");
        moduleDatabase.RegisterType(classDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Worker"),
            SwiftTypeName = classDecl.SwiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(moduleDatabase);

        ClassHandler.PopulateEmittedClassMethods(module, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var record));
        Assert.NotNull(record!.EmittedClassMethods);
        var entry = Assert.Single(record!.EmittedClassMethods!);
        // Recomputed via NameProvider: void no-arg "describe" → "Describe".
        Assert.Equal("Describe", entry.CSharpName);
    }

    #endregion

    #region Protocol Conformance Inheritance (Session I5)

    [Fact]
    public void DerivedInheritsConformanceDictionaryEntriesFromBase()
    {
        // Base has Equatable conformance with a symbol. Derived has no own conformances.
        // The derived class's conformance dictionary should include the base's Equatable entry.
        var baseClass = CreateClassDecl("Base");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Base"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$s10TestModule4BaseVSQAAMc"));

        var derived = CreateClassDecl("Derived");
        derived.Conformances = new List<TypeConformance>();

        var output = EmitClassHierarchy(baseClass, derived);
        var derivedBody = GetClassBody(output, "Derived");

        // Derived should have Equatable in its conformance dictionary (inherited from Base)
        Assert.Contains("$s10TestModule4BaseVSQAAMc", derivedBody);
    }

    [Fact]
    public void OwnConformanceWithEmptySymbol_ResolvesFromBaseSymbol()
    {
        // Derived has Equatable conformance with empty symbol (TBD lookup failed).
        // Base has the same conformance with a valid symbol.
        // Derived's dictionary entry should use the base's symbol.
        var baseClass = CreateClassDecl("Base");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Base"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$s10TestModule4BaseVSQAAMc"));

        var derived = CreateClassDecl("Derived");
        derived.Conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Derived"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "") // Empty symbol — should be resolved from base
        };

        var output = EmitClassHierarchy(baseClass, derived);
        var derivedBody = GetClassBody(output, "Derived");

        // Should contain the base's symbol, not empty
        Assert.Contains("$s10TestModule4BaseVSQAAMc", derivedBody);
        Assert.DoesNotContain("\"\"", derivedBody);
    }

    [Fact]
    public void OwnAndInheritedConformancesMergedCorrectly()
    {
        // Base: Equatable. Derived: Hashable. Both should appear in derived's dictionary.
        var baseClass = CreateClassDecl("Base");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Base"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$s10TestModule4BaseVSQAAMc"));

        var derived = CreateClassDecl("Derived");
        derived.Conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Derived"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule7DerivedVSHAAMc")
        };

        var output = EmitClassHierarchy(baseClass, derived);
        var derivedBody = GetClassBody(output, "Derived");

        Assert.Contains("$s10TestModule7DerivedVSHAAMc", derivedBody); // Own Hashable
        Assert.Contains("$s10TestModule4BaseVSQAAMc", derivedBody);    // Inherited Equatable
    }

    [Fact]
    public void EmptySymbolWithNoAncestorResolution_OmittedFromDictionary()
    {
        // Derived has conformance with empty symbol, and base has no matching conformance.
        // The empty symbol should be filtered out (Step 1 safety net).
        var baseClass = CreateClassDecl("Base");

        var derived = CreateClassDecl("Derived");
        derived.Conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Derived"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "") // Empty, no ancestor to resolve from
        };

        var output = EmitClassHierarchy(baseClass, derived);
        var derivedBody = GetClassBody(output, "Derived");

        // Should NOT contain an empty string entry
        Assert.DoesNotContain("IEquatable", derivedBody);
    }

    [Fact]
    public void OwnNonEmptySymbol_TakesPriorityOverBase()
    {
        // Both base and derived have Equatable with valid symbols. Derived's own symbol wins.
        var baseClass = CreateClassDecl("Base");
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Base"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sBase_EQ_Symbol"));

        var derived = CreateClassDecl("Derived");
        derived.Conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Derived"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sDerived_EQ_Symbol")
        };

        var output = EmitClassHierarchy(baseClass, derived);
        var derivedBody = GetClassBody(output, "Derived");

        Assert.Contains("$sDerived_EQ_Symbol", derivedBody);
        // Base's symbol should NOT appear (dedup by protocol name)
        Assert.DoesNotContain("$sBase_EQ_Symbol", derivedBody);
    }

    [Fact]
    public void SkippedBase_NoAncestorConformancesInDictionary()
    {
        // Base class has unsupported generic constraints → effectively not derived.
        // Derived's dictionary should NOT include base's conformances.
        var baseClass = CreateClassDecl("GenericBase");
        baseClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        Path: new[] { "T" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        baseClass.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.GenericBase"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sGenericBase_EQ"));

        var derived = CreateClassDecl("Derived");
        derived.ResolvedSuperclass = baseClass;
        derived.Conformances = new List<TypeConformance>();

        // Emit just the derived class (base is skipped)
        var output = EmitSingleClass(derived);
        var derivedBody = GetClassBody(output, "Derived");

        // Base is non-emittable, so _isDerived is false — no ancestor conformances
        Assert.DoesNotContain("$sGenericBase_EQ", derivedBody);
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
            // Set ParentDecl and ModuleDecl on all child methods and properties
            foreach (var method in cls.Methods)
            {
                method.ParentDecl = cls;
                method.ModuleDecl = moduleDecl;
            }
            foreach (var prop in cls.Properties)
            {
                prop.ParentDecl = cls;
                prop.ModuleDecl = moduleDecl;
                foreach (var accessor in prop.Accessors)
                {
                    accessor.Method.ParentDecl = cls;
                    accessor.Method.ModuleDecl = moduleDecl;
                }
            }
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
        // Set ParentDecl and ModuleDecl on all child methods and properties
        foreach (var method in classDecl.Methods)
        {
            method.ParentDecl = classDecl;
            method.ModuleDecl = moduleDecl;
        }
        foreach (var prop in classDecl.Properties)
        {
            prop.ParentDecl = classDecl;
            prop.ModuleDecl = moduleDecl;
            foreach (var accessor in prop.Accessors)
            {
                accessor.Method.ParentDecl = classDecl;
                accessor.Method.ModuleDecl = moduleDecl;
            }
        }
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

    /// <summary>
    /// Creates a void-returning, no-parameter method on a class and emits it via MethodHandler.
    /// Returns the C# and Swift output.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitMethodOnClass(
        string name,
        bool classIsFinal = false,
        bool methodIsOverride = false,
        bool methodIsFinal = false,
        bool isStatic = false,
        bool isAccessor = false,
        bool hasResolvedBase = false)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("TestClass");
        classDecl.IsFinal = classIsFinal;
        classDecl.ParentDecl = moduleDecl;
        classDecl.ModuleDecl = moduleDecl;

        if (hasResolvedBase)
        {
            var baseClass = CreateClassDecl("BaseClass");
            baseClass.ParentDecl = moduleDecl;
            baseClass.ModuleDecl = moduleDecl;
            // Add the matching emitted method to the base class so override resolution finds it
            var baseMethod = CreateVoidMethodDecl(name);
            baseMethod.ParentDecl = baseClass;
            baseMethod.ModuleDecl = moduleDecl;
            baseMethod.WasEmitted = true;
            baseClass.Methods.Add(baseMethod);
            classDecl.ResolvedSuperclass = baseClass;
        }

        var method = CreateVoidMethodDecl(name,
            isOverride: methodIsOverride, isFinal: methodIsFinal,
            isStatic: isStatic, isAccessor: isAccessor);
        method.ParentDecl = classDecl;
        method.ModuleDecl = moduleDecl;
        classDecl.Methods.Add(method);

        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
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
        typeDatabase.AddModuleDatabase(testModule);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(NullLogger<MethodHandler>.Instance);
        var env = new MethodEnvironment(method, typeDatabase);
        var conductor = new Conductor(NullLoggerFactory.Instance);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    /// <summary>
    /// Emits a property on a class via PropertyHandler and returns the C# output.
    /// </summary>
    private static string EmitPropertyOnClass(PropertyDecl propertyDecl, ClassDecl classDecl)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        classDecl.ParentDecl = moduleDecl;
        classDecl.ModuleDecl = moduleDecl;
        propertyDecl.ModuleDecl = moduleDecl;
        foreach (var accessor in propertyDecl.Accessors)
        {
            accessor.Method.ParentDecl = classDecl;
            accessor.Method.ModuleDecl = moduleDecl;
        }

        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "String"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Kind = TypeRecordKind.Struct,
                Flags = TypeRecordFlags.Frozen
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
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
        typeDatabase.AddModuleDatabase(testModule);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new PropertyHandler(NullLogger<PropertyHandler>.Instance);
        var env = handler.Marshal(propertyDecl, typeDatabase);
        var conductor = new Conductor(NullLoggerFactory.Instance);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csOutput.ToString();
    }

    /// <summary>
    /// Creates a void-returning, no-parameter MethodDecl for testing.
    /// </summary>
    private static MethodDecl CreateVoidMethodDecl(
        string name,
        bool isOverride = false,
        bool isFinal = false,
        bool isStatic = false,
        bool isAccessor = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            IsAccessor = isAccessor,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
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
            IsSynthesizedAccessor = false,
            IsOverride = isOverride,
            IsFinal = isFinal,
        };
    }

    /// <summary>
    /// Creates a void-returning MethodDecl with one parameter of the given Swift type.
    /// Used for testing overload-aware override resolution.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithParam(string name, string swiftParamType, string paramName)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec(swiftParamType),
                    Name = paramName,
                    PrivateName = paramName,
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
            IsSynthesizedAccessor = false,
        };
    }

    /// <summary>
    /// Creates a simple string-returning, no-parameter property for testing dispatch modifiers.
    /// </summary>
    private static PropertyDecl CreateSimplePropertyDecl(
        string name,
        bool isOverride = false,
        bool isFinal = false,
        bool isStatic = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = isStatic,
            HasStorage = false,
            IsOverride = isOverride,
            IsFinal = isFinal,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s10TestModule{name.Length}{name}Ssvg",
                        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
                        IsConstructor = false,
                        IsAccessor = true,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new ArgumentDecl
                            {
                                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                                Name = "",
                                PrivateName = "",
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
                        IsSynthesizedAccessor = true
                    }
                }
            },
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
