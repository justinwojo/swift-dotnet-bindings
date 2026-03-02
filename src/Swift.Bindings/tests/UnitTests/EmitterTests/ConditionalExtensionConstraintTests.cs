// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for conditional extension constraint handling (Foundation B3).
///
/// Conditional extensions in Swift (e.g., <c>extension Table&lt;T&gt; where T: FetchableRecord</c>)
/// add constraints to the method's genericSig beyond what the parent type declares.
/// These tests verify that:
/// 1. Extra constraints on simple protocols (no associated types, no Self) are allowed through
/// 2. Extra constraints on protocols with associated types are still blocked
/// 3. Parent-baseline constraints are not re-checked at the method level
/// 4. BoundGenericsHandler accepts conditional extension constraints for bound generic satisfaction
/// </summary>
public class ConditionalExtensionConstraintTests
{
    #region MethodValidationGates Tests

    [Fact]
    public void HasUnsupportedProtocolConstraints_ConditionalExtension_SimpleProtocol_ReturnsFalse()
    {
        // Scenario: extension Table<T> where T: FetchableRecord { func fetchCursor() }
        // FetchableRecord has no associated types → should emit
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.FetchableRecord", TypeRecordKind.Protocol, TypeRecordFlags.None));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");

        var method = CreateMethodDecl("fetchCursor", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),       // parent baseline
                    ("GRDB.FetchableRecord", ConformanceKind.Protocol))  // conditional extension extra
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.False(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_ConditionalExtension_ProtocolWithAssociatedTypes_ReturnsTrue()
    {
        // Scenario: extension Table<T> where T: Cursor { ... }
        // Cursor has associated types → should still skip
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.Cursor", TypeRecordKind.Protocol, TypeRecordFlags.HasAssociatedTypes));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");

        var method = CreateMethodDecl("process", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),
                    ("GRDB.Cursor", ConformanceKind.Protocol))
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.True(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_ConditionalExtension_ProtocolWithSelfRequirement_ReturnsTrue()
    {
        // Protocol with HasSelfRequirement → should still skip (aligned with PInvokeEmitter)
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.SelfRefProtocol", TypeRecordKind.Protocol, TypeRecordFlags.HasSelfRequirement));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");

        var method = CreateMethodDecl("compare", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),
                    ("GRDB.SelfRefProtocol", ConformanceKind.Protocol))
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.True(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_ParentBaseline_ProtocolWithAssociatedTypes_StillBlocked()
    {
        // Parent type already declares T: Cursor (with associated types).
        // Even though it's a parent-baseline constraint, PAT protocols must still be
        // blocked because the type-level where clause also skips them (GenericTypeEmitter
        // line 85), so the constraint is never enforced and P/Invoke would lack the
        // required witness table parameter.
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.Cursor", TypeRecordKind.Protocol, TypeRecordFlags.HasAssociatedTypes));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "GRDB.Cursor");

        var method = CreateMethodDecl("process", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("GRDB.Cursor", ConformanceKind.Protocol))  // same as parent — PAT, still blocked
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.True(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_ParentBaseline_SupportedProtocol_Skipped()
    {
        // Parent type declares T: FetchableRecord (no associated types, no Self).
        // This is a supported parent-baseline constraint → correctly skipped
        // (handled by type-level where clause).
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.FetchableRecord", TypeRecordKind.Protocol, TypeRecordFlags.None));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "GRDB.FetchableRecord");

        var method = CreateMethodDecl("fetch", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("GRDB.FetchableRecord", ConformanceKind.Protocol))  // same as parent — supported, skipped
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.False(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_MethodOwnParam_ProtocolWithAssociatedTypes_ReturnsTrue()
    {
        // Method-own type parameter (τ_1_0) constrained on protocol with associated types
        // → should be blocked (it's not a parent-baseline constraint)
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.Cursor", TypeRecordKind.Protocol, TypeRecordFlags.HasAssociatedTypes));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");

        var method = CreateMethodDecl("process", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T", ("Swift.Equatable", ConformanceKind.Protocol)),
                CreateGenericParam("τ_1_0", "U", ("GRDB.Cursor", ConformanceKind.Protocol))
            });

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.True(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    [Fact]
    public void HasUnsupportedProtocolConstraints_NonGenericMethod_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");

        var method = CreateMethodDecl("simpleMethod", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>());

        var methodEnv = new MethodEnvironment(method, typeDatabase);
        Assert.False(MethodValidationGates.HasUnsupportedProtocolConstraints(methodEnv));
    }

    #endregion

    #region BoundGenericsHandler Constraint Tests

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ConditionalExtension_SimpleProtocol_ReturnsFalse()
    {
        // Scenario: Method in conditional extension `where T: FetchableRecord`
        // returns RecordCursor<T> where RecordCursor requires T: FetchableRecord.
        // Parent type (Table<T>) does NOT declare FetchableRecord, but method does.
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.FetchableRecord", TypeRecordKind.Protocol, TypeRecordFlags.None));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("RecordCursor", moduleDecl, "T", "GRDB.FetchableRecord");

        var boundGeneric = new NamedTypeSpec("GRDB.RecordCursor", new NamedTypeSpec("τ_0_0"));

        var method = CreateMethodDecl("fetchCursor", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),
                    ("GRDB.FetchableRecord", ConformanceKind.Protocol))
            });

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        Assert.False(found);
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ConditionalExtension_ProtocolWithAssociatedTypes_SkippedByBoundGeneric()
    {
        // Conditional extension constraint on protocol WITH associated types.
        // ShouldSkipConstraint correctly skips checking this constraint entirely
        // (protocols with associated types can't be expressed as C# constraints).
        // The method is blocked at HasUnsupportedProtocolConstraints, not here.
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.Cursor", TypeRecordKind.Protocol, TypeRecordFlags.HasAssociatedTypes));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("CursorWrapper", moduleDecl, "T", "GRDB.Cursor");

        var boundGeneric = new NamedTypeSpec("GRDB.CursorWrapper", new NamedTypeSpec("τ_0_0"));

        var method = CreateMethodDecl("wrapCursor", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),
                    ("GRDB.Cursor", ConformanceKind.Protocol))
            });

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        // ShouldSkipConstraint skips Cursor (has associated types) → no violation reported
        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_ConditionalExtension_ProtocolWithSelfRequirement_SkippedByBoundGeneric()
    {
        // Conditional extension constraint on protocol with Self requirement.
        // ShouldSkipConstraint correctly skips this (Self requirement protocols
        // can't be used as C# constraints). Blocked at HasUnsupportedProtocolConstraints.
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.Comparable", TypeRecordKind.Protocol, TypeRecordFlags.HasSelfRequirement));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("SortedCollection", moduleDecl, "T", "GRDB.Comparable");

        var boundGeneric = new NamedTypeSpec("GRDB.SortedCollection", new NamedTypeSpec("τ_0_0"));

        var method = CreateMethodDecl("sorted", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol),
                    ("GRDB.Comparable", ConformanceKind.Protocol))
            });

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        // ShouldSkipConstraint skips Comparable (Self requirement) → no violation reported
        Assert.False(found);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_NoConditionalExtension_StillFails()
    {
        // No conditional extension — parent type doesn't satisfy, method doesn't help
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.FetchableRecord", TypeRecordKind.Protocol, TypeRecordFlags.None));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("RecordCursor", moduleDecl, "T", "GRDB.FetchableRecord");

        var boundGeneric = new NamedTypeSpec("GRDB.RecordCursor", new NamedTypeSpec("τ_0_0"));

        // Method does NOT include FetchableRecord in its constraints
        var method = CreateMethodDecl("fetchCursor", parentType, moduleDecl,
            genericParams: new List<GenericArgumentDecl>
            {
                CreateGenericParam("τ_0_0", "T",
                    ("Swift.Equatable", ConformanceKind.Protocol))
            });

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, method, out var details);

        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    [Fact]
    public void TryGetFirstUnsatisfiedConstraint_PropertyContext_NoMethodFallback()
    {
        // When contextDecl is a PropertyDecl (not MethodDecl), no method fallback occurs
        var typeDatabase = CreateTypeDatabase(
            ("GRDB.FetchableRecord", TypeRecordKind.Protocol, TypeRecordFlags.None));

        var moduleDecl = CreateModuleDecl("GRDB");
        var parentType = CreateGenericStructDecl("Table", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("RecordCursor", moduleDecl, "T", "GRDB.FetchableRecord");

        var boundGeneric = new NamedTypeSpec("GRDB.RecordCursor", new NamedTypeSpec("τ_0_0"));

        var propertyContext = new PropertyDecl
        {
            Name = "cursor",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, propertyContext, out var details);

        // Should still fail — PropertyDecl doesn't provide method-level constraint fallback
        Assert.True(found);
        Assert.Contains("does not satisfy constraint", details);
    }

    #endregion

    #region ShouldSkipConstraint Alignment Tests

    [Fact]
    public void ShouldSkipConstraint_HasSelfRequirement_SkipsConstraint()
    {
        // Step 6: ShouldSkipConstraint should now also skip protocols with HasSelfRequirement,
        // aligned with PInvokeEmitter.IsProtocolAvailableForConstraint
        var typeDatabase = CreateTypeDatabase(
            ("Swift.Comparable", TypeRecordKind.Protocol, TypeRecordFlags.HasSelfRequirement));

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentType = CreateGenericStructDecl("Container", moduleDecl, "T", "Swift.Equatable");
        CreateGenericStructDecl("Box", moduleDecl, "T", "Swift.Comparable");

        // Box<T> where T: Comparable (has Self requirement)
        // When Comparable is skipped by ShouldSkipConstraint, the bound generic check passes
        var boundGeneric = new NamedTypeSpec("TestModule.Box", new NamedTypeSpec("τ_0_0"));

        var propertyContext = new PropertyDecl
        {
            Name = "box",
            SwiftTypeSpec = boundGeneric,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>()
        };

        var handler = new BoundGenericsHandler(typeDatabase);
        var found = handler.TryGetFirstUnsatisfiedConstraint(boundGeneric, propertyContext, out var details);

        // Comparable (Self requirement) should be skipped by ShouldSkipConstraint → no violation
        Assert.False(found);
    }

    #endregion

    #region IsConditionalExtensionConstraint Helper Tests

    [Fact]
    public void IsConditionalExtensionConstraint_ExtraConstraint_ReturnsTrue()
    {
        var parentParams = new List<GenericArgumentDecl>
        {
            CreateGenericParam("τ_0_0", "T", ("Swift.Equatable", ConformanceKind.Protocol))
        };

        var methodParam = CreateGenericParam("τ_0_0", "T",
            ("Swift.Equatable", ConformanceKind.Protocol),
            ("GRDB.FetchableRecord", ConformanceKind.Protocol));

        var extraConformance = methodParam.GenericConformances[1]; // FetchableRecord
        Assert.True(MethodValidationGates.IsConditionalExtensionConstraint(methodParam, extraConformance, parentParams));
    }

    [Fact]
    public void IsConditionalExtensionConstraint_BaselineConstraint_ReturnsFalse()
    {
        var parentParams = new List<GenericArgumentDecl>
        {
            CreateGenericParam("τ_0_0", "T", ("Swift.Equatable", ConformanceKind.Protocol))
        };

        var methodParam = CreateGenericParam("τ_0_0", "T",
            ("Swift.Equatable", ConformanceKind.Protocol),
            ("GRDB.FetchableRecord", ConformanceKind.Protocol));

        var baselineConformance = methodParam.GenericConformances[0]; // Equatable
        Assert.False(MethodValidationGates.IsConditionalExtensionConstraint(methodParam, baselineConformance, parentParams));
    }

    [Fact]
    public void IsConditionalExtensionConstraint_NoParentParams_ReturnsTrue()
    {
        var methodParam = CreateGenericParam("τ_0_0", "T",
            ("GRDB.FetchableRecord", ConformanceKind.Protocol));

        var conformance = methodParam.GenericConformances[0];
        Assert.True(MethodValidationGates.IsConditionalExtensionConstraint(methodParam, conformance, null));
    }

    [Fact]
    public void IsConditionalExtensionConstraint_UnmatchedParam_ReturnsTrue()
    {
        // Method param τ_1_0 doesn't match any parent param → all are "extra"
        var parentParams = new List<GenericArgumentDecl>
        {
            CreateGenericParam("τ_0_0", "T", ("Swift.Equatable", ConformanceKind.Protocol))
        };

        var methodParam = CreateGenericParam("τ_1_0", "U",
            ("GRDB.FetchableRecord", ConformanceKind.Protocol));

        var conformance = methodParam.GenericConformances[0];
        Assert.True(MethodValidationGates.IsConditionalExtensionConstraint(methodParam, conformance, parentParams));
    }

    #endregion

    #region Helper Methods

    private static MockTypeDatabase CreateTypeDatabase(
        params (string Name, TypeRecordKind Kind, TypeRecordFlags Flags)[] protocols)
    {
        return new MockTypeDatabase(protocols);
    }

    private static ModuleDecl CreateModuleDecl(string moduleName)
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

    private static StructDecl CreateGenericStructDecl(
        string structName, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
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
            GenericParameters = new List<GenericArgumentDecl>
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
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static GenericArgumentDecl CreateGenericParam(
        string typeName, string sugaredName,
        params (string Protocol, ConformanceKind Kind)[] conformances)
    {
        return new GenericArgumentDecl(
            TypeName: typeName,
            SugaredTypeName: sugaredName,
            GenericConformances: conformances.Select(c => new GenericParameterConformance(
                Path: new[] { typeName },
                ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(c.Protocol),
                Kind: c.Kind)).ToList(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
    }

    private static MethodDecl CreateMethodDecl(
        string name, TypeDecl parentType, ModuleDecl moduleDecl,
        List<GenericArgumentDecl> genericParams)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s_{name}",
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
                    ModuleDecl = null
                }
            },
            GenericParameters = genericParams,
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase(params (string Name, TypeRecordKind Kind, TypeRecordFlags Flags)[] protocols)
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };

            foreach (var (name, kind, flags) in protocols)
            {
                _types[name] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", name.Split('.').Last()),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(name),
                    MetadataAccessor = "",
                    Flags = flags,
                    Kind = kind
                };
            }
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
