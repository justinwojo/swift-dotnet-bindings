// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericTypeEmitter.
/// </summary>
public class GenericTypeEmitterTests
{
    [Fact]
    public void GetGenericParameterList_ReturnsEmpty_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetGenericParameterList_ReturnsSingleParam_ForSingleGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal("<T>", result);
    }

    [Fact]
    public void GetGenericParameterList_ReturnsMultipleParams_ForMultipleGenericType()
    {
        var typeDecl = CreateGenericStruct("Pair", 2);

        var result = GenericTypeEmitter.GetGenericParameterList(typeDecl);

        Assert.Equal("<T, U>", result);
    }

    [Fact]
    public void GetTypeNameWithGenerics_ReturnsNameOnly_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl);

        Assert.Equal("SimpleStruct", result);
    }

    [Fact]
    public void GetTypeNameWithGenerics_ReturnsNameWithParams_ForGenericType()
    {
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl);

        Assert.Equal("Box<T>", result);
    }

    [Fact]
    public void GetWhereClause_ReturnsEmpty_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetWhereClause_ReturnsEmpty_ForGenericTypeWithNoProtocolConformances()
    {
        // Box<T> with no protocol conformance: ISwiftObject seed is dropped so blittable
        // instantiations like Box<Vector3> / Box<float> compile at the call site.
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetWhereClause_ReturnsEmpty_ForMultipleGenericParamsWithNoConformances()
    {
        // Pair<T, U> with no protocol conformance: no where clause for either param.
        var typeDecl = CreateGenericStruct("Pair", 2);

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetWhereClause_IncludesProtocolConstraints()
    {
        var typeDecl = CreateGenericStructWithConstraints("Container", new List<string> { "Swift.Equatable" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Contains("ISwiftObject", result);
        // Swift.Equatable maps to IEquatable<> in C# (with empty type name when called from GetWhereClause)
        Assert.Contains("IEquatable<>", result);
    }

    [Fact]
    public void GetWhereClause_SeedsISwiftObject_OnlyWhenProtocolConformanceSurvives()
    {
        // When at least one protocol conformance survives filtering, ISwiftObject is
        // seeded first because PWT lookups (ProtocolWitnessTable.GetOrThrowAuto<T, IFoo>)
        // require T : ISwiftObject.
        var typeDecl = CreateGenericStructWithConstraints("Container", new List<string> { "Swift.Equatable" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        // Order matters: ISwiftObject must come first so callers can read it as the
        // primary constraint.
        var iswiftIndex = result.IndexOf("ISwiftObject", StringComparison.Ordinal);
        var iequatableIndex = result.IndexOf("IEquatable", StringComparison.Ordinal);
        Assert.True(iswiftIndex >= 0);
        Assert.True(iequatableIndex >= 0);
        Assert.True(iswiftIndex < iequatableIndex);
    }

    [Fact]
    public void GetWhereClause_SkipsSendableConstraint()
    {
        // Sendable is the only conformance and gets filtered out -> the surviving list
        // is empty so no where clause is emitted.
        var typeDecl = CreateGenericStructWithConstraints("AsyncBox", new List<string> { "Swift.Sendable" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("Swift.Copyable")]
    [InlineData("Swift.Escapable")]
    [InlineData("Swift.SendableMetatype")]
    [InlineData("Swift.BitwiseCopyable")]
    public void GetWhereClause_DropsISwiftObjectSeed_ForMarkerOnlyConstraint(string markerProtocol)
    {
        // Stdlib marker protocols carry no runtime witness table — the Swift compiler
        // does not pass them as PWT args. So a generic param constrained only by a
        // marker has no descriptor-symbol PWT lookup and the ISwiftObject seed must
        // NOT be retained; otherwise blittable instantiations like MeshBuffer<Vector3>
        // would re-trigger CS0315 even though no witness table is needed.
        var typeDecl = CreateGenericStructWithConstraints("MarkerBox", new List<string> { markerProtocol });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("MyApp.Copyable")]
    [InlineData("MyApp.Sendable")]
    [InlineData("MyApp.Escapable")]
    [InlineData("MyApp.BitwiseCopyable")]
    public void GetWhereClause_KeepsISwiftObjectSeed_ForSameNameNonStdlibProtocol(string nonStdlibProtocol)
    {
        // A real app/framework protocol that happens to share a simple name with a
        // stdlib marker (Swift.Copyable etc.) is NOT a marker — it has a real witness
        // table and the descriptor-symbol PWT path will still emit a lookup. The
        // ISwiftObject seed must be retained so that lookup compiles. Module-qualified
        // marker detection is the guard against false-positive stripping.
        var typeDecl = CreateGenericStructWithConstraints("ConstrainedBox", new List<string> { nonStdlibProtocol });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_KeepsISwiftObjectSeed_WhenAllProtocolConformancesAreFiltered()
    {
        // SwiftUI.View is filtered out as an unsupported framework constraint, so it
        // doesn't surface in the C# constraint list. The Swift param still declares a
        // non-Sendable conformance though, and the descriptor-symbol PWT lookup path
        // can still emit `ProtocolWitnessTable.GetOrThrowAuto<T, …>` calls — so the
        // ISwiftObject seed must remain even though no projected interface survives.
        // (The type itself is gated separately via TryGetUnsupportedConstraint.)
        var typeDecl = CreateGenericStructWithConstraints("UIBox", new List<string> { "SwiftUI.View" });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        Assert.Contains("ISwiftObject", result);
        Assert.DoesNotContain("ISwiftView", result);
    }

    [Fact]
    public void TryGetUnsupportedConstraint_ReturnsTrue_ForSwiftUIProtocol()
    {
        var typeDecl = CreateGenericStructWithConstraints("UIBox", new List<string> { "SwiftUI.View" });

        var found = GenericTypeEmitter.TryGetUnsupportedConstraint(typeDecl, out var unsupportedConstraint);

        Assert.True(found);
        Assert.NotNull(unsupportedConstraint);
        Assert.Equal("View", unsupportedConstraint.Name);
        Assert.Equal("SwiftUI", unsupportedConstraint.Module);
    }

    [Fact]
    public void GetFullTypeSignature_ReturnsNameOnly_ForNonGenericType()
    {
        var typeDecl = CreateNonGenericStruct();

        var result = GenericTypeEmitter.GetFullTypeSignature(typeDecl);

        Assert.Equal("SimpleStruct", result);
    }

    [Fact]
    public void GetFullTypeSignature_ReturnsNameWithoutWhereClause_ForGenericTypeWithNoConformances()
    {
        // Generic params without protocol conformances no longer get a defensive
        // ISwiftObject seed; the type signature is just the bare name + params.
        var typeDecl = CreateGenericStruct("Box", 1);

        var result = GenericTypeEmitter.GetFullTypeSignature(typeDecl);

        Assert.Equal("Box<T>", result);
    }

    [Fact]
    public void GetWhereClause_MixedParams_OnlyConstrainedParamGetsClause()
    {
        // Pair<T, U> with T : Equatable and U with no conformance — only T should get
        // an emitted where clause. U's clause is dropped so call sites can pass blittable
        // values (Vector3, float, …) for U while still satisfying T's constraint.
        var typeConformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                ConformanceKind.Protocol),
        };
        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", typeConformances, new List<GenericParameterConformance>()),
            new GenericArgumentDecl("τ_0_1", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
        };
        var moduleDecl = CreateModuleDecl("TestModule");
        var typeDecl = new StructDecl
        {
            Name = "MixedPair",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MixedPair"),
            MangledName = "$s10TestModule9MixedPairV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule9MixedPairVMa",
            GenericParameters = genericParams,
        };

        var result = GenericTypeEmitter.GetWhereClause(typeDecl);

        // T survives → emitted with ISwiftObject + IEquatable<>. U dropped.
        Assert.Contains("where T : ISwiftObject", result);
        Assert.Contains("IEquatable", result);
        Assert.DoesNotContain("where U", result);
        Assert.DoesNotContain("U : ISwiftObject", result);
    }

    [Fact]
    public void GetTypeNameWithGenerics_UsesRenamedCSharpTypeName_WhenTypeDatabaseProvided()
    {
        // Nested type "Configuration" was renamed to "ConfigurationType" in TypeDatabase.
        // GetTypeNameWithGenerics should use the CSharpTypeName leaf name, not TypeDecl.Name.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var configSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Configuration");
        module.RegisterType(configSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.ConfigurationType"),
            SwiftTypeName = configSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        var typeDecl = new StructDecl
        {
            Name = "Configuration", // TypeDecl.Name unchanged (used for Swift symbols)
            SwiftTypeName = configSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa",
        };

        // Without TypeDatabase: uses TypeDecl.Name
        Assert.Equal("Configuration", GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl));

        // With TypeDatabase: uses renamed CSharpTypeName leaf
        Assert.Equal("ConfigurationType", GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl, typeDatabase));
    }

    #region Cross-Module Constraint Stripping Tests

    [Fact]
    public void GetWhereClause_StdlibDecodableConstraint_IsStrippedButISwiftObjectRemains()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("RequestInterceptor", "Alamofire",
            new List<string> { "Swift.Decodable" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // The cross-module unregistered protocol is filtered out of the projected
        // C# constraint list, but the Swift param still carries a non-Sendable
        // conformance. The ISwiftObject seed must stay because PWT lookups for
        // filtered conformances still emit through the descriptor-symbol path —
        // dropping the seed would break call sites with CS0314.
        Assert.DoesNotContain("Decodable", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_StdlibErrorConstraint_IsStrippedButISwiftObjectRemains()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("ErrorWrapper", "Alamofire",
            new List<string> { "Swift.Error" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Same as the Decodable case above: the unregistered cross-module protocol
        // is filtered, but the underlying Swift conformance keeps the ISwiftObject
        // seed alive so descriptor-symbol PWT lookups continue to compile.
        Assert.DoesNotContain("IError", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_SameModuleProtocol_IsKept()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Container", "Alamofire",
            new List<string> { "Alamofire.RequestInterceptor" });
        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Same-module constraint is kept even without TypeDB registration
        Assert.Contains("IRequestInterceptor", result);
    }

    [Fact]
    public void GetWhereClause_CrossModuleRegisteredProtocol_IsKept()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Wrapper", "Alamofire",
            new List<string> { "Foundation.NSCoding" });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Foundation.NSCoding"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "INSCoding"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSCoding"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.Contains("INSCoding", result);
    }

    [Fact]
    public void GetWhereClause_MultipleMixedConstraints_OnlyKnownKept()
    {
        // T has both Decodable (cross-module, unregistered) and ISwiftObject (baseline)
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Swift.Decodable"),
                ConformanceKind.Protocol),
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Alamofire.RequestInterceptor"),
                ConformanceKind.Protocol)
        };

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", conformances, new List<GenericParameterConformance>())
        };

        var moduleDecl = CreateModuleDecl("Alamofire");
        var typeDecl = new StructDecl
        {
            Name = "MixedBox",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.MixedBox"),
            MangledName = "$s9Alamofire8MixedBoxV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s9Alamofire8MixedBoxVMa",
            GenericParameters = genericParams
        };

        var typeDatabase = new TypeDatabase();

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.DoesNotContain("Decodable", result);
        Assert.Contains("IRequestInterceptor", result);
        Assert.Contains("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_NoTypeDatabase_EmitsAll()
    {
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Box", "Alamofire",
            new List<string> { "Swift.Decodable" });

        // null typeDatabase → preserves existing behavior (no filtering)
        var result = GenericTypeEmitter.GetWhereClause(typeDecl, null);

        Assert.Contains("IDecodable", result);
    }

    #endregion

    #region ObjC-Bridged Class-As-Constraint Tests (some-protocol over-broad)

    [Fact]
    public void GetWhereClause_ObjCBridgedClassConformanceTarget_EmitsBaseClassConstraint()
    {
        // Reproduces the StoreKit `purchase(confirmIn: some UIScene)` shape: the
        // `some UIScene` opaque parameter lowers to a synthetic generic with a
        // protocol conformance pointing at `UIKit.UIScene`. The TypeDatabase
        // resolves `UIKit.UIScene` to a synthetic ObjC-bridged Class record (via
        // CreateObjCBridgedTypeRecord) because UIKit is an autoBridge module in
        // apple-frameworks.json. The where-clause emitter must recognize this
        // and emit `where T : UIKit.UIScene` (a base-class constraint), NOT the
        // over-broad `where T : ISwiftObject` fallback.
        var typeDecl = CreateGenericStructWithConstraintsAndModule("PurchaseHelper", "StoreKit",
            new List<string> { "UIKit.UIScene" });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("UIKit.UIScene"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIScene"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIScene"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Class constraint must be present and ISwiftObject must NOT be seeded —
        // ObjC-bridged classes do not implement ISwiftObject, so seeding it
        // would produce an unsatisfiable constraint (no type can be both an
        // ObjC class deriving from UIKit.UIScene AND a Swift-class implementing
        // ISwiftObject).
        Assert.Contains("UIKit.UIScene", result);
        Assert.DoesNotContain("ISwiftObject", result);
        Assert.DoesNotContain("IUIScene", result);
    }

    [Fact]
    public void GetWhereClause_ObjCBridgedClassConstraint_PrecedesAdditionalInterfaces()
    {
        // C# `where` syntax requires class constraints to appear ahead of any
        // interface constraints. When a generic param has both an ObjC-bridged
        // class conformance AND a registered interface conformance, the class
        // must come first. This is the corner case that motivates the explicit
        // ordering in the where-clause emitter.
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("UIKit.UIScene"),
                ConformanceKind.Protocol),
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("StoreKit.Confirmable"),
                ConformanceKind.Protocol),
        };
        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", conformances, new List<GenericParameterConformance>()),
        };
        var moduleDecl = CreateModuleDecl("StoreKit");
        var typeDecl = new StructDecl
        {
            Name = "MixedConstraint",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("StoreKit.MixedConstraint"),
            MangledName = "$s8StoreKit15MixedConstraintV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s8StoreKit15MixedConstraintVMa",
            GenericParameters = genericParams,
        };
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("UIKit.UIScene"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIScene"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIScene"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            }),
            (SwiftTypeName.FromModuleQualifiedName("StoreKit.Confirmable"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("StoreKit", "IConfirmable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("StoreKit.Confirmable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        var classIndex = result.IndexOf("UIKit.UIScene", StringComparison.Ordinal);
        var interfaceIndex = result.IndexOf("IConfirmable", StringComparison.Ordinal);
        Assert.True(classIndex >= 0, "Class constraint must be present.");
        Assert.True(interfaceIndex >= 0, "Surviving interface constraint must be present.");
        Assert.True(classIndex < interfaceIndex,
            "Class constraint must appear before interface constraints to satisfy C# where-clause syntax.");
        Assert.DoesNotContain("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_NonObjCBridgedCrossModuleClass_DoesNotPromoteToBaseClassConstraint()
    {
        // Scoping guard: only ObjC-bridged Class records (the synthetic shape
        // from CreateObjCBridgedTypeRecord) should be promoted to base-class
        // constraints. A regular Swift cross-module class registered without
        // the ObjCBridged flag must continue through the historical permissive
        // path that emits an "I"-prefixed interface name regardless of declared
        // Kind. This prevents an accidental class-constraint regression from
        // spilling out to hand-registered Swift class records.
        var typeDecl = CreateGenericStructWithConstraintsAndModule("Container", "Alamofire",
            new List<string> { "OtherModule.PlainClass" });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("OtherModule.PlainClass"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OtherModule", "PlainClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.PlainClass"),
                MetadataAccessor = "$s11OtherModule10PlainClassCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Without the ObjCBridged flag, the record is NOT promoted to a base-class
        // constraint — the historical permissive path emits the "I"-prefixed
        // interface name and keeps ISwiftObject seeded.
        Assert.DoesNotContain("where T : OtherModule.PlainClass", result);
        Assert.DoesNotContain("where T : PlainClass", result);
        Assert.Contains("ISwiftObject", result);
        Assert.Contains("IPlainClass", result);
    }

    #endregion

    #region Class-Bound Generic Constraint Tests

    [Fact]
    public void GetWhereClause_ClassBoundConstraint_EmitsClassNameNotInterfaceName()
    {
        // Regression coverage for `bug-0.10.0-foundation-dimension-constraint-not-projected.md`
        // (Bundle 04 #5). The parser tags every `:` clause as ConformanceKind.Protocol because
        // it has no type-database access; the emitter must consult the resolved record to
        // recognise class targets. Class-bound generics emit the projected C# class name as
        // the constraint, not an `I{Name}` form, AND skip the ISwiftObject seed because the
        // class constraint must come first in the C# clause (CS0405/CS0406) and already
        // implies ISwiftObject.
        var typeDecl = CreateGenericStructWithConstraintsAndModule("UnitBox", "TestModule",
            new List<string> { "Foundation.Dimension" });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDimension"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
                MetadataAccessor = "",
                // ObjCBridged matches the production FoundationDatabase.xml entry
                // (`objcBridged="true"`) and gates the class-bound promotion path.
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        Assert.Contains("Foundation.NSDimension", result);
        // No IDimension synthesis — that would emit a non-existent interface.
        Assert.DoesNotContain("IDimension", result);
        // Class constraint already implies ISwiftObject; re-seeding would put an
        // interface ahead of a class constraint and break compilation.
        Assert.DoesNotContain("ISwiftObject", result);
    }

    [Fact]
    public void GetWhereClause_ClassPlusProtocolConstraint_EmitsClassFirstThenProtocol()
    {
        // When a generic param is bound by both a class and a protocol, C# requires the
        // class constraint to come first (CS0405). The emitter visits conformances in
        // declaration order and inserts ISwiftObject only when no class bound is present;
        // in this case the class bound is present, so no seeding happens, and the
        // surviving paramConstraints list is `[ClassName, IProtoName]`.
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
                ConformanceKind.Protocol),
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("TestModule.Tracker"),
                ConformanceKind.Protocol),
        };
        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", conformances, new List<GenericParameterConformance>())
        };
        var moduleDecl = CreateModuleDecl("TestModule");
        var typeDecl = new StructDecl
        {
            Name = "TrackedUnitBox",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TrackedUnitBox"),
            MangledName = "$s10TestModule14TrackedUnitBoxV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule14TrackedUnitBoxVMa",
            GenericParameters = genericParams,
        };
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDimension"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Dimension"),
                MetadataAccessor = "",
                // ObjCBridged matches the production FoundationDatabase.xml entry
                // (`objcBridged="true"`) and gates the class-bound promotion path.
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            })
        });

        var result = GenericTypeEmitter.GetWhereClause(typeDecl, typeDatabase);

        // Class name appears before the protocol interface name; ISwiftObject is not
        // injected. Same-module Tracker protocol is kept (matches IsKept-same-module test).
        Assert.Contains("Foundation.NSDimension", result);
        Assert.Contains("ITracker", result);
        var classIdx = result.IndexOf("Foundation.NSDimension", StringComparison.Ordinal);
        var protoIdx = result.IndexOf("ITracker", StringComparison.Ordinal);
        Assert.True(classIdx >= 0 && protoIdx >= 0);
        Assert.True(classIdx < protoIdx,
            "Class constraint must precede protocol constraint per CS0405.");
        Assert.DoesNotContain("ISwiftObject", result);
    }

    #endregion

    private static StructDecl CreateNonGenericStruct()
    {
        return new StructDecl
        {
            Name = "SimpleStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleStruct"),
            MangledName = "$s10TestModule12SimpleStructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule12SimpleStructVMa",
        };
    }

    // Sugared names used in tests — mirrors real Swift generic signatures
    private static readonly string[] TestSugaredNames = { "T", "U", "V", "W" };

    private static StructDecl CreateGenericStruct(string name, int typeParamCount)
    {
        var genericParams = new List<GenericArgumentDecl>();
        for (int i = 0; i < typeParamCount; i++)
        {
            var sugared = i < TestSugaredNames.Length ? TestSugaredNames[i] : $"T{i}";
            genericParams.Add(new GenericArgumentDecl(
                $"τ_0_{i}",
                sugared,
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()
            ));
        }

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            GenericParameters = genericParams
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
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateGenericStructWithConstraintsAndModule(string name, string moduleName, List<string> protocols)
    {
        var conformances = protocols.Select(p => new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(p),
            ConformanceKind.Protocol
        )).ToList();

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "τ_0_0",
                "T",
                conformances,
                new List<GenericParameterConformance>()
            )
        };

        var moduleDecl = CreateModuleDecl(moduleName);

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VMa",
            GenericParameters = genericParams
        };
    }

    private static StructDecl CreateGenericStructWithConstraints(string name, List<string> protocols)
    {
        var conformances = protocols.Select(p => new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(p),
            ConformanceKind.Protocol
        )).ToList();

        var genericParams = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "τ_0_0",
                "T",
                conformances,
                new List<GenericParameterConformance>()
            )
        };

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            GenericParameters = genericParams
        };
    }
}
