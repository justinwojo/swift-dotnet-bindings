// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// P1-26 A5: a <c>Foundation.Date</c> element inside a returned tuple must be surfaced as
/// <c>System.DateTimeOffset</c> (matching the scalar <see cref="DateProjection"/>), not as the
/// bare <c>double</c> that Date's raw P/Invoke type would otherwise leak. The double divergence
/// was: the scalar return path applied the 2001-epoch conversion while the tuple-element path
/// emitted a raw <c>double</c>, so the same Swift <c>Date</c> surfaced as two different C# types
/// depending on whether it was returned alone or as a tuple member.
///
/// Exercises the two A5-touched seams in <c>WrapperEmitter.Return.cs</c> directly:
///   * <c>GetCSharpTypeForTupleElement</c> — the element's public C# type.
///   * <c>GetTupleElementMarshalCode</c> — the per-element marshalling statement.
/// </summary>
public class TupleDateElementEmitterTests
{
    [Fact]
    public void TupleElementType_FoundationDate_IsDateTimeOffset()
    {
        var emitter = CreateWrapperEmitter();
        var date = new NamedTypeSpec("Foundation.Date");

        Assert.Equal("System.DateTimeOffset", emitter.GetCSharpTypeForTupleElement(date));
    }

    [Fact]
    public void TupleElementType_FoundationDate_InsideGenerics_NotIdiomatic()
    {
        // The Date→DateTimeOffset rewrite is gated on top-level (applyIdiomaticConversion).
        // Inside a generic argument it must NOT apply (mirrors the bare-SwiftString/Data gating),
        // so the result is whatever the type record resolves to — never "System.DateTimeOffset".
        var emitter = CreateWrapperEmitter();
        var date = new NamedTypeSpec("Foundation.Date");

        Assert.NotEqual("System.DateTimeOffset",
            emitter.GetCSharpTypeForTupleElement(date, applyIdiomaticConversion: false));
    }

    [Fact]
    public void TupleElementMarshal_FoundationDate_AppliesEpochConversion()
    {
        var emitter = CreateWrapperEmitter();
        var date = new NamedTypeSpec("Foundation.Date");

        var code = emitter.GetTupleElementMarshalCode(date, "item0", "elem0", "System.DateTimeOffset");

        Assert.NotNull(code);
        // Reads the raw 2001-epoch Double and applies the same epoch offset as the scalar path.
        Assert.Contains("SwiftMarshal.MarshalFromSwift<double>(item0)", code);
        Assert.Contains(DateProjection.SwiftEpoch, code);
        Assert.Contains(".AddSeconds(", code);
        // Must NOT surface the bare double — that was the divergence.
        Assert.DoesNotContain("MarshalFromSwift<System.DateTimeOffset>", code);
    }

    [Fact]
    public void TupleElementType_NonDateScalar_Unaffected()
    {
        // Guard: the special-case is Date-specific. A plain Swift.Int element resolves through
        // the normal type-record path to its mapped C# type, not DateTimeOffset.
        var emitter = CreateWrapperEmitter();
        var intSpec = new NamedTypeSpec("Swift.Int");

        var type = emitter.GetCSharpTypeForTupleElement(intSpec);
        Assert.NotEqual("System.DateTimeOffset", type);
        Assert.Equal("long", type);
    }

    /// <summary>
    /// Builds a minimal <see cref="WrapperEmitter"/> over a free function returning Swift.Int.
    /// The tuple-element helpers under test operate on a supplied <see cref="TypeSpec"/>, so the
    /// host method's own return shape is irrelevant — only the type database (Swift.Int) matters.
    /// </summary>
    private static WrapperEmitter CreateWrapperEmitter()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "now",
            MangledName = "$s10TestModule3nowSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);
        var signatureHandler = new SignatureHandler(env);
        return new WrapperEmitter(env, signatureHandler);
    }
}
