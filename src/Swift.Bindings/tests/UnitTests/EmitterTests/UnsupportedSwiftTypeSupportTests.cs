// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for UnsupportedSwiftTypeSupport — TryFindFallbackInfo recursive search, EscapeStringLiteral.
/// </summary>
public class UnsupportedSwiftTypeSupportTests
{
    [Fact]
    public void EmitAttribute_ExistentialFallback_RecordsDegradationOnContext()
    {
        // Defect E: a PAT existential `any P` that degrades to `object` carries the resolver's
        // "Existential type fallback" reason. EmitAttribute must record it on the emission context
        // so the loud SWIFTBIND023 diagnostic fires once per distinct type at report time, instead
        // of staying silent behind only the consumer-facing [UnsupportedSwiftType] attribute.
        var csWriter = new CSharpWriter(new StringWriter());
        var context = new ModuleEmissionContext();
        var info = new TypeDatabaseExtensions.AnyTypeFallbackInfo(
            TypeDatabaseExtensions.AnyTypeFallbackInfo.ExistentialFallbackReason, "any AttributeKind");

        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, info, context);

        Assert.Contains("any AttributeKind", context.DegradedExistentials);
    }

    [Fact]
    public void EmitAttribute_NonExistentialFallback_DoesNotRecordDegradation()
    {
        // Only the existential fallback degrades a protocol surface to `object`; other fallback
        // reasons (closures, unknown generics) are unrelated and must NOT raise SWIFTBIND023.
        var csWriter = new CSharpWriter(new StringWriter());
        var context = new ModuleEmissionContext();
        var info = new TypeDatabaseExtensions.AnyTypeFallbackInfo(
            "Unsupported closure fallback", "(Swift.Int) -> ()");

        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, info, context);

        Assert.Empty(context.DegradedExistentials);
    }

    [Fact]
    public void TryFindFallbackInfo_NamedTypeWithAnyTypeGenericParam_ReturnsTrue()
    {
        // Array<UnknownModule.Foo> — the generic parameter resolves to AnyType
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("UnknownModule.Foo"));

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, arrayType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_TupleWithAnyTypeElement_ReturnsTrue()
    {
        // (Swift.Int, UnknownModule.Bar) — second element resolves to AnyType
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var tupleType = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("UnknownModule.Bar")
        });

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, tupleType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_ClosureWithAnyTypeArg_ReturnsTrue()
    {
        // (UnknownModule.Foo) -> () — closure with unsupported arg type
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("UnknownModule.Foo") }),
            TupleTypeSpec.Empty);

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, closureType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_AllSupportedTypes_ReturnsFalse()
    {
        // Swift.Int — fully supported, no fallback needed
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var intType = new NamedTypeSpec("Swift.Int");

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, intType, out var fallbackInfo);

        Assert.False(result);
    }

    [Fact]
    public void RecordExistentialDegradations_TwoDistinctExistentialPositions_RecordsBoth()
    {
        // A member like `func f(_ a: any P, _ b: any Q)` degrades TWO distinct existentials.
        // The first-match attribute scan only ever names `any P`; the per-distinct-type SWIFTBIND023
        // diagnostic must still see BOTH. (Red against the old first-match-only recording.)
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var context = new ModuleEmissionContext();

        var positions = new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),                                  // return — supported
            new NamedTypeSpec("Unknown.PWithAssoc") { IsAny = true },        // param a — degrades
            new NamedTypeSpec("Unknown.QWithAssoc") { IsAny = true },        // param b — degrades
        };

        UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
            context, typeDatabase, closureHandler, positions);

        Assert.Contains(context.DegradedExistentials, s => s.Contains("PWithAssoc"));
        Assert.Contains(context.DegradedExistentials, s => s.Contains("QWithAssoc"));
    }

    [Fact]
    public void RecordExistentialDegradations_TupleWithTwoDistinctExistentials_RecordsBoth()
    {
        // A single position can itself nest two distinct degraded existentials (a tuple param
        // `(any P, any Q)`). The exhaustive walk records both; first-match would stop at `any P`.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var context = new ModuleEmissionContext();

        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Unknown.PWithAssoc") { IsAny = true },
            new NamedTypeSpec("Unknown.QWithAssoc") { IsAny = true },
        });

        UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
            context, typeDatabase, closureHandler, new TypeSpec[] { tuple });

        Assert.Contains(context.DegradedExistentials, s => s.Contains("PWithAssoc"));
        Assert.Contains(context.DegradedExistentials, s => s.Contains("QWithAssoc"));
    }

    [Fact]
    public void RecordExistentialDegradations_NonExistentialFallback_NotRecorded()
    {
        // An unsupported-closure fallback degrades to `object` too, but it is NOT an existential
        // degradation and must not raise SWIFTBIND023.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var context = new ModuleEmissionContext();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("UnknownModule.Foo") }),
            TupleTypeSpec.Empty);

        UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
            context, typeDatabase, closureHandler, new TypeSpec[] { closureType });

        Assert.Empty(context.DegradedExistentials);
    }

    [Fact]
    public void RecordExistentialDegradations_NullContext_DoesNotThrow()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
            null, typeDatabase, closureHandler,
            new TypeSpec[] { new NamedTypeSpec("Unknown.PWithAssoc") { IsAny = true } });
    }

    [Fact]
    public void TryFindFallbackInfo_TupleWithTwoExistentials_ReturnsFirstMatch()
    {
        // Refactor guard: TryFindFallbackInfo still short-circuits on the FIRST degraded position
        // (declaration order) — the ~18 callers that emit a single [UnsupportedSwiftType] flag rely
        // on this. The shared walker must not change that for the first-only mode.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Unknown.PWithAssoc") { IsAny = true },
            new NamedTypeSpec("Unknown.QWithAssoc") { IsAny = true },
        });

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, tuple, out var fallbackInfo);

        Assert.True(result);
        Assert.Contains("PWithAssoc", fallbackInfo.SwiftType);
        Assert.DoesNotContain("QWithAssoc", fallbackInfo.SwiftType);
    }

    [Fact]
    public void EscapeStringLiteral_EscapesQuotesAndBackslashes()
    {
        Assert.Equal("hello", UnsupportedSwiftTypeSupport.EscapeStringLiteral("hello"));
        Assert.Equal("say \\\"hi\\\"", UnsupportedSwiftTypeSupport.EscapeStringLiteral("say \"hi\""));
        Assert.Equal("path\\\\to\\\\file", UnsupportedSwiftTypeSupport.EscapeStringLiteral("path\\to\\file"));
        Assert.Equal("mixed\\\\\\\"test", UnsupportedSwiftTypeSupport.EscapeStringLiteral("mixed\\\"test"));
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }
}
