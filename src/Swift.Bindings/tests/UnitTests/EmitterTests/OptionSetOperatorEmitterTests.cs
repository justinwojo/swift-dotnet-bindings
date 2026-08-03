// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Gates and output shape for the synthesized <c>OptionSet</c> bitwise surface.
/// </summary>
/// <remarks>
/// The synthesis has no Swift symbol behind it — it writes C# over the type's own emitted
/// <c>RawValue</c> and <c>init(rawValue:)</c>. So every gate here is guarding against emitting a
/// member that would not compile: a raw value that never bound, an initializer that was skipped,
/// a raw type with no bitwise meaning, or a name another member already claimed.
/// </remarks>
public class OptionSetOperatorEmitterTests
{
    private const string Module = "TestModule";

    private static async Task<TypeDatabase> CreateDatabaseAsync()
    {
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml"));
        return typeDatabase;
    }

    private static string Emit(StructDecl structDecl, ITypeDatabase typeDatabase, bool isReferenceType = false,
        IReadOnlySet<string>? emittedOperatorSymbols = null, IReadOnlySet<string>? reservedPropertyNames = null)
    {
        var stringWriter = new StringWriter();
        OptionSetOperatorEmitter.EmitIfOptionSet(
            new CSharpWriter(stringWriter),
            structDecl,
            structDecl.Name,
            typeDatabase,
            isReferenceType,
            emittedOperatorSymbols ?? new HashSet<string>(StringComparer.Ordinal),
            reservedPropertyNames ?? new HashSet<string>(StringComparer.Ordinal),
            NullLogger.Instance);
        return stringWriter.ToString();
    }

    // ---- Positive cases --------------------------------------------------------------------

    [Fact]
    public async Task EmitIfOptionSet_Int32RawValue_EmitsFullBitwiseSurface()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");

        var output = Emit(decl, db);

        Assert.Contains("public static TextStyle operator |(TextStyle left, TextStyle right)", output);
        Assert.Contains("public static TextStyle operator &(TextStyle left, TextStyle right)", output);
        Assert.Contains("public static TextStyle operator ^(TextStyle left, TextStyle right)", output);
        Assert.Contains("public static TextStyle operator ~(TextStyle value)", output);
        Assert.Contains("public bool Contains(TextStyle other)", output);
        Assert.Contains("new TextStyle(unchecked((int)(left.RawValue | right.RawValue)))", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_NarrowRawValue_CastsBackToTheRawType()
    {
        // C# promotes byte operands to int, so the combined expression has to be cast back down —
        // and `unchecked` so a complement that overflows the narrow type wraps instead of throwing.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("AccessFlags", "Swift.UInt8");

        var output = Emit(decl, db);

        Assert.Contains("unchecked((byte)(left.RawValue | right.RawValue))", output);
        Assert.Contains("unchecked((byte)(~value.RawValue))", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_PlatformWidthRawValue_CastsToTheInitializerParameterType()
    {
        // Swift `Int` is the asymmetric case: the property narrows to `int` but the initializer
        // parameter stays `nint`. Casting to the property's narrowed type and relying on the
        // implicit widening makes `new T(int)` ambiguous with the projection's own
        // `T(SwiftHandle)` constructor, so the cast has to name the parameter's type instead.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("PermissionMask", "Swift.Int");

        var output = Emit(decl, db);

        Assert.Contains("new PermissionMask(unchecked((nint)(left.RawValue | right.RawValue)))", output);
        Assert.Contains("new PermissionMask(unchecked((nint)(~value.RawValue)))", output);
        Assert.DoesNotContain("unchecked((int)", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_ExtraRawValueInitializerOverload_UsesTheMatchingOne()
    {
        // A type may declare additional `init(rawValue:)` overloads on wider integers. Building the
        // option set through one of those would run a different initializer than the raw value the
        // operands were read from, so the overload whose parameter is the property's type wins
        // regardless of declaration order.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");
        var widerOverload = MakeConstructor("Swift.Int64");
        widerOverload.MarkEmitted();
        widerOverload.EmittedCSharpName = "TextStyle";
        decl.Methods.Insert(0, widerOverload);

        var output = Emit(decl, db);

        Assert.Contains("unchecked((int)(left.RawValue | right.RawValue))", output);
        Assert.DoesNotContain("(long)", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_ValueTypeProjection_EmitsNoNullGuards()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("AccessFlags", "Swift.UInt8");

        var output = Emit(decl, db, isReferenceType: false);

        Assert.DoesNotContain("ThrowIfNull", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_ClassProjection_GuardsEveryOperand()
    {
        // A non-frozen OptionSet projects as a C# class, so an operand can be null and the body
        // would dereference it for RawValue.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("OptionsInfo", "Swift.Int32");

        var output = Emit(decl, db, isReferenceType: true);

        Assert.Contains("global::System.ArgumentNullException.ThrowIfNull(left);", output);
        Assert.Contains("global::System.ArgumentNullException.ThrowIfNull(right);", output);
        Assert.Contains("global::System.ArgumentNullException.ThrowIfNull(value);", output);
        Assert.Contains("global::System.ArgumentNullException.ThrowIfNull(other);", output);
    }

    // ---- Gates -----------------------------------------------------------------------------

    [Fact]
    public async Task EmitIfOptionSet_NotAnOptionSet_EmitsNothing()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("Plain", "Swift.Int32", conformances: new[] { "Swift.Equatable" });

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_ForeignProtocolNamedOptionSet_EmitsNothing()
    {
        // A library is free to declare its own protocol called `OptionSet`. It promises none of
        // the bitwise semantics the standard library's does, so a bare name match would synthesize
        // operators the type never claimed.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("Impostor", "Swift.Int32", conformances: new[] { $"{Module}.OptionSet" });

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_GenericOptionSet_EmitsNothing()
    {
        // Swift permits a generic OptionSet; declining it is a deliberate surface omission until a
        // fixture exercises that shape end to end, not a claim that it cannot exist.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("Generic", "Swift.Int32");
        decl.GenericParameters.Add(new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "T",
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()));

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_RawValuePropertyNotEmitted_EmitsNothing()
    {
        // Without a bound RawValue there is nothing to combine — the bodies would not compile.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("Skipped", "Swift.Int32", rawValueEmitted: false);

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_RawValueInitializerNotEmitted_EmitsNothing()
    {
        // `new T(raw)` is the only way these bodies produce a value.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("NoInit", "Swift.Int32", initializerEmitted: false);

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_NonIntegralRawValue_EmitsNothing()
    {
        // A String-backed RawRepresentable conforming to OptionSet has no bitwise meaning.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("StringBacked", "Swift.String");

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_UnresolvableRawValue_EmitsNothing()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("Unresolved", $"{Module}.MysteryRaw");

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    [Fact]
    public async Task EmitIfOptionSet_OperatorAlreadyEmitted_SkipsOnlyThatOperator()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");

        var output = Emit(decl, db,
            emittedOperatorSymbols: new HashSet<string>(StringComparer.Ordinal) { "|" });

        Assert.DoesNotContain("operator |", output);
        Assert.Contains("operator &", output);
        Assert.Contains("operator ~", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_OperatorDeclaredInSwift_SkipsOnlyThatOperator()
    {
        // A Swift type may declare its own `|`; re-synthesizing it is a CS0111 duplicate.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");
        decl.Operators.Add(new OperatorDecl
        {
            Name = "|",
            ParentDecl = null,
            ModuleDecl = null,
            OperatorSymbol = "|",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = MakeConstructor("Swift.Int32"),
        });

        var output = Emit(decl, db);

        Assert.DoesNotContain("operator |", output);
        Assert.Contains("operator &", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_ContainsNameReserved_SkipsContainsButKeepsOperators()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");

        var output = Emit(decl, db,
            reservedPropertyNames: new HashSet<string>(StringComparer.Ordinal) { "Contains" });

        Assert.DoesNotContain("public bool Contains(", output);
        Assert.Contains("operator |", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_SwiftContainsMethodBound_SkipsSynthesizedContains()
    {
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("TextStyle", "Swift.Int32");
        var contains = MakeMethod("contains", "Swift.Bool");
        contains.MarkEmitted();
        contains.EmittedCSharpName = "Contains";
        decl.Methods.Add(contains);

        var output = Emit(decl, db);

        Assert.DoesNotContain("public bool Contains(", output);
        Assert.Contains("operator |", output);
    }

    [Fact]
    public async Task EmitIfOptionSet_StaticRawValue_EmitsNothing()
    {
        // A static `rawValue` is not the instance storage the bodies read.
        var db = await CreateDatabaseAsync();
        var decl = MakeOptionSet("StaticRaw", "Swift.Int32");
        decl.Properties[0].IsStatic = true;

        Assert.Equal(string.Empty, Emit(decl, db));
    }

    // ---- Model factories -------------------------------------------------------------------

    private static StructDecl MakeOptionSet(
        string name,
        string rawSwiftType,
        string[]? conformances = null,
        bool rawValueEmitted = true,
        bool initializerEmitted = true)
    {
        var rawValue = new PropertyDecl
        {
            Name = "rawValue",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec(rawSwiftType),
            HasStorage = true,
            IsStatic = false,
            Accessors = Array.Empty<AccessorDecl>(),
        };
        if (rawValueEmitted)
        {
            rawValue.MarkEmitted();
            rawValue.MarkEmittedCSharpName("RawValue");
        }

        var initializer = MakeConstructor(rawSwiftType);
        if (initializerEmitted)
        {
            initializer.MarkEmitted();
            initializer.EmittedCSharpName = name;
        }

        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = $"$s{name}",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl> { rawValue },
            Methods = new List<MethodDecl> { initializer },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = (conformances ?? new[] { "Swift.OptionSet" })
                .Select(p => new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
                    SwiftTypeName.FromModuleQualifiedName(p),
                    string.Empty))
                .ToList(),
            MetadataAccessor = string.Empty,
            AvailabilityAnnotations = null,
        };
    }

    private static MethodDecl MakeConstructor(string rawSwiftType)
    {
        var ctor = MakeMethod("init", "Swift.Void");
        ctor.IsConstructor = true;
        ctor.CSSignature.Add(MakeArg("rawValue", rawSwiftType));
        return ctor;
    }

    private static MethodDecl MakeMethod(string name, string returns) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        MangledName = $"$s{name}",
        MethodType = MethodType.Instance,
        IsConstructor = false,
        Throws = false,
        IsAsync = false,
        IsSynthesizedAccessor = false,
        GenericParameters = new List<GenericArgumentDecl>(),
        CSSignature = new List<ArgumentDecl> { MakeArg("__ret", returns) },
        AvailabilityAnnotations = null,
        RawGenericSig = null,
    };

    private static ArgumentDecl MakeArg(string name, string typeName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeSpec = new NamedTypeSpec(typeName),
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
    };
}
