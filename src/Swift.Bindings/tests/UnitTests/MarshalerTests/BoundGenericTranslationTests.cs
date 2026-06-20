// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="BoundGenericTranslation"/> — the single home for the bound-generic
/// <c>NamedTypeSpec</c> → C# type-name mapping that the closure and tuple element translators now
/// delegate to, plus the SIMD alias-collapse short-circuit shared by every C# translator.
///
/// These assert the service's contract directly so the two caller-supplied policy axes — the
/// closure path's empty-tuple→<c>SwiftVoid</c> mapping and bare-generic safety net (both ON), versus
/// the tuple path (both OFF) — can no longer silently drift, and the recursion stays the caller's.
/// </summary>
public class BoundGenericTranslationTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly ExistentialHandler _existentialHandler;

    public BoundGenericTranslationTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _existentialHandler = new ExistentialHandler(_typeDatabase);
    }

    // A recurse delegate that tags each argument it is asked to translate, so a test can assert
    // exactly which arguments flowed through the caller's recursion versus a service short-circuit.
    private static string Recurse(TypeSpec spec) =>
        $"RECURSE:{(spec as NamedTypeSpec)?.Name ?? spec.GetType().Name}";

    private static NamedTypeSpec BoundGeneric(string name, params TypeSpec[] args)
    {
        var spec = new NamedTypeSpec(name);
        foreach (var arg in args)
            spec.GenericParameters.Add(arg);
        return spec;
    }

    private string Translate(NamedTypeSpec spec, bool mapEmptyTupleArgumentToSwiftVoid, bool bareGenericSafetyNet) =>
        BoundGenericTranslation.TranslateBoundGenericToCSharp(
            _typeDatabase,
            _existentialHandler,
            spec,
            Recurse,
            mapEmptyTupleArgumentToSwiftVoid,
            bareGenericSafetyNet);

    #region TryResolveSimdAliasCSharp

    [Fact]
    public void TryResolveSimdAliasCSharp_KnownSimdAlias_ResolvesToNonGenericRecord()
    {
        var spec = BoundGeneric("Swift.SIMD3", new NamedTypeSpec("Swift.Float"));

        var resolved = BoundGenericTranslation.TryResolveSimdAliasCSharp(_typeDatabase, spec, out var csharp);

        Assert.True(resolved);
        Assert.Equal("System.Numerics.Vector3", csharp);
        Assert.DoesNotContain("<", csharp); // the alias is non-generic — never re-append the arg
    }

    [Fact]
    public void TryResolveSimdAliasCSharp_NonAliasBoundGeneric_ReturnsFalseAndNull()
    {
        var spec = BoundGeneric("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var resolved = BoundGenericTranslation.TryResolveSimdAliasCSharp(_typeDatabase, spec, out var csharp);

        Assert.False(resolved);
        Assert.Null(csharp);
    }

    [Fact]
    public void TryResolveSimdAliasCSharp_SimdBaseButUnmappedElement_ReturnsFalse()
    {
        // Swift.SIMD3<Swift.Int> is not in the alias table (only the Float variants are).
        var spec = BoundGeneric("Swift.SIMD3", new NamedTypeSpec("Swift.Int"));

        var resolved = BoundGenericTranslation.TryResolveSimdAliasCSharp(_typeDatabase, spec, out var csharp);

        Assert.False(resolved);
        Assert.Null(csharp);
    }

    #endregion

    #region TranslateBoundGenericToCSharp — shared body

    [Theory]
    [InlineData(true, true)]   // closure mode
    [InlineData(false, false)] // tuple mode
    public void TranslateBoundGenericToCSharp_SimdAlias_ShortCircuitsWithoutGenericArgs(
        bool mapEmptyTupleToSwiftVoid, bool safetyNet)
    {
        var spec = BoundGeneric("Swift.SIMD3", new NamedTypeSpec("Swift.Float"));

        var result = Translate(spec, mapEmptyTupleToSwiftVoid, safetyNet);

        // Alias short-circuit precedes the <...> wrap regardless of the policy flags.
        Assert.Equal("System.Numerics.Vector3", result);
        Assert.DoesNotContain("RECURSE:", result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_UnknownBase_ReturnsAnyType()
    {
        var spec = BoundGeneric("Unknown.Missing", new NamedTypeSpec("Swift.Int"));

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: true, bareGenericSafetyNet: true);

        Assert.Equal(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_PointerBase_ShortCircuitsToIntPtrWithoutGenericArgs()
    {
        var spec = BoundGeneric("Swift.UnsafeMutablePointer", new NamedTypeSpec("Swift.Int"));

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: true, bareGenericSafetyNet: true);

        Assert.Equal("System.IntPtr", result);
        Assert.DoesNotContain("RECURSE:", result); // IntPtr has no generics; the arg is never translated
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_NamedArgs_RecurseThroughSuppliedDelegateAndWrap()
    {
        var spec = BoundGeneric("Test.Box", new NamedTypeSpec("Swift.Int"));

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: false, bareGenericSafetyNet: false);

        // The non-existential arg flows through the caller's recursion delegate, then is wrapped.
        Assert.Equal("Test.Box<RECURSE:Swift.Int>", result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_ExistentialArg_BypassesRecurseDelegate()
    {
        // Box<Int, any> — the bare-Any existential arg must be handled by the existential branch,
        // NOT routed through the caller's recurse delegate.
        var spec = BoundGeneric("Test.Box", new NamedTypeSpec("Swift.Int"), new ProtocolListTypeSpec());

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: false, bareGenericSafetyNet: false);

        Assert.Contains("RECURSE:Swift.Int", result);                  // the named arg used the delegate
        Assert.Equal(1, result.Split("RECURSE:").Length - 1);          // the existential arg did NOT
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_EmptyTupleArg_ClosureMode_MapsToSwiftVoid()
    {
        var spec = BoundGeneric("Test.Box", TupleTypeSpec.Empty);

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: true, bareGenericSafetyNet: true);

        // Closure path: empty tuple → Swift.SwiftVoid (it never reaches the recurse delegate).
        Assert.Equal("Test.Box<Swift.SwiftVoid>", result);
        Assert.DoesNotContain("RECURSE:", result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_EmptyTupleArg_TupleMode_FlowsThroughDelegate()
    {
        var spec = BoundGeneric("Test.Box", TupleTypeSpec.Empty);

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: false, bareGenericSafetyNet: false);

        // Tuple path: no empty-tuple special case — the empty tuple flows through the recurse delegate.
        Assert.Contains("RECURSE:", result);
        Assert.DoesNotContain("Swift.SwiftVoid", result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_BareGenericNoArgs_SafetyNetOn_ReturnsAnyType()
    {
        // A bare-generic base (SwiftDictionary) with zero translated args would otherwise emit an
        // argument-less generic name (CS0305). The closure path's safety net rewrites it to AnyType.
        var spec = new NamedTypeSpec("Swift.Dictionary");

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: true, bareGenericSafetyNet: true);

        Assert.Equal(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, result);
    }

    [Fact]
    public void TranslateBoundGenericToCSharp_BareGenericNoArgs_SafetyNetOff_ReturnsBareName()
    {
        // The tuple path has no safety net — it returns the base record name as-is.
        var spec = new NamedTypeSpec("Swift.Dictionary");

        var result = Translate(spec, mapEmptyTupleArgumentToSwiftVoid: false, bareGenericSafetyNet: false);

        Assert.Equal("Swift.SwiftDictionary", result);
    }

    #endregion

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // Bare-generic base: FullyQualifiedName "Swift.SwiftDictionary" is recognized by
                // TypeDatabaseExtensions.IsBareGenericTypeName, exercising the safety-net flag.
                ["Swift.Dictionary"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // Ordinary generic base for the recurse + wrap path.
                ["Test.Box"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "Box"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.Box"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // SIMD alias target — the non-generic record Swift.SIMD3<Swift.Float> collapses to.
                ["simd.simd_float3"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                // Pointer type — must be the exact IntPtrType instance (reference-equality short-circuit).
                ["Swift.UnsafeMutablePointer"] = TypeDatabaseExtensions.IntPtrType
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
