// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Operators that call Swift directly with an Optional object result or operand. An Optional of an
/// ObjC class is one nullable pointer, the result at +1, which an ordinary method converts through
/// the type's projection. The operator body once wrote its own conversion and had none for these:
/// it returned the raw pointer as the projected class (CS0029) and passed an Optional operand's
/// <c>{name}Buffer</c> local without declaring it (CS0103). Either error fails the whole module's
/// compile, so the module shipped no binding at all.
/// </summary>
public class OperatorOptionalObjectTests
{
    private const string Module = "TestModule";
    private const string ObjCClass = "Foundation.NSNumber";
    private const string SwiftClass = "TestModule.Payload";

    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<TypeDatabase> CreateDatabaseAsync()
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));

        var foundation = new ModuleTypeDatabase("Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        foundation.RegisterType(SwiftTypeName.FromModuleQualifiedName(ObjCClass), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSNumber"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(ObjCClass),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged,
            Kind = TypeRecordKind.Class,
        });
        database.AddModuleDatabase(foundation);

        var module = new ModuleTypeDatabase(Module, $"/tmp/{Module}.dylib");
        foreach (var (name, kind) in new[] { ("Box", TypeRecordKind.Class), ("Payload", TypeRecordKind.Class), ("Value", TypeRecordKind.Struct) })
        {
            module.RegisterType(SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
                MetadataAccessor = $"$s10{Module}{name.Length}{name}{(kind == TypeRecordKind.Class ? "C" : "V")}Ma",
                Flags = kind == TypeRecordKind.Class ? TypeRecordFlags.RequiresMemoryManagement : TypeRecordFlags.None,
                Kind = kind,
            });
        }
        database.AddModuleDatabase(module);
        return database;
    }

    private static readonly ModuleDecl s_module = new()
    {
        Name = Module,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    /// <summary>The operator's parent: a class (<c>Box</c>) or a non-frozen struct (<c>Value</c>).</summary>
    private static TypeDecl Parent(bool isClass)
    {
        if (isClass)
        {
            return new ClassDecl
            {
                Name = "Box",
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Box"),
                MangledName = $"$s10{Module}3BoxCN",
                IsFinal = true,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                Conformances = new List<TypeConformance>(),
                ParentDecl = s_module,
                ModuleDecl = s_module,
            };
        }
        return new StructDecl
        {
            Name = "Value",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Value"),
            MangledName = $"$s10{Module}5ValueVN",
            IsFrozen = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = s_module,
            ModuleDecl = s_module,
            MetadataAccessor = $"$s10{Module}5ValueVMa",
        };
    }

    private static NamedTypeSpec Optional(string inner) => new("Swift.Optional", new NamedTypeSpec(inner));

    private static OperatorDecl Operator(string symbol, TypeDecl parent, TypeSpec result, TypeSpec? rhs = null, bool throws = false, bool rhsInOut = false)
    {
        var self = new NamedTypeSpec(parent.SwiftTypeName.ToString());
        ArgumentDecl Arg(TypeSpec spec, string name, bool inOut = false) => new()
        {
            SwiftTypeSpec = spec, Name = name, PrivateName = name, IsInOut = inOut, IsGeneric = false,
            ParentDecl = parent, ModuleDecl = s_module,
        };
        var signature = new List<ArgumentDecl> { Arg(result, string.Empty), Arg(self, "lhs") };
        if (rhs is not null)
            signature.Add(Arg(rhs, "rhs", rhsInOut));
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = $"$s10{Module}{parent.Name.Length}{parent.Name}_operator_{(symbol + rhs).GetHashCode():x}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = s_module,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        return new OperatorDecl
        {
            Name = symbol, OperatorSymbol = symbol,
            Kind = rhs is null ? OperatorKind.Unary : OperatorKind.Binary, IsPrefix = rhs is null,
            UnderlyingMethod = method, ParentDecl = parent, ModuleDecl = s_module,
        };
    }

    private static (bool Emitted, string CSharp) Emit(OperatorDecl op, TypeDatabase database)
    {
        var cs = new StringWriter();
        var logger = new CapturingLogger();
        var emitted = new OperatorHandler(logger).EmitOperator(
            new CSharpWriter(cs), op, database, pinvokeHelperContext: null,
            swiftWriter: new SwiftWriter(new StringWriter()), emissionContext: new ModuleEmissionContext());
        return (emitted, cs.ToString() + string.Join("\n", logger.Entries.Select(e => e.Message)));
    }

    /// <summary>The operator's own body, without the P/Invoke declaration that follows it.</summary>
    private static string Body(string cs)
    {
        var start = cs.IndexOf(" operator ", StringComparison.Ordinal);
        var end = Regex.Match(cs[start..], @"\n\s*\[(global::)?(System\.Runtime\.InteropServices\.)?(LibraryImport|UnmanagedCallConv)").Index;
        if (end > 0)
            end += start;
        return end < 0 ? cs[start..] : cs[start..end];
    }

    /// <summary>
    /// The result is read the way an ordinary method reads an Optional ObjC result: a null pointer
    /// is nil, and anything else is adopted with the +1 Swift returned it at.
    /// </summary>
    private static void AssertAdoptsOptionalObjCResult(string body, string resultType)
    {
        var adopt = Regex.Match(body,
            $@"(?<local>\w+) == (global::)?(System\.)?IntPtr\.Zero \? null : (global::)?ObjCRuntime\.Runtime\.GetINativeObject<(global::)?{Regex.Escape(resultType)}>\(\k<local>, true\)");
        Assert.True(adopt.Success, $"the result is null for nil and otherwise adopted at +1:\n{body}");
        Assert.Matches($@"var {adopt.Groups["local"].Value} = PInvoke_op_\w+\(", body);
        Assert.DoesNotMatch(@"return PInvoke_op_\w+\(", body);
    }

    [Theory]
    [InlineData(true, "+")]
    [InlineData(false, "+")]
    [InlineData(true, "~")]
    [InlineData(false, "~")]
    public async Task OptionalObjCResult_IsAdoptedOrNil(bool classParent, string symbol)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(classParent);
        var rhs = symbol == "~" ? null : new NamedTypeSpec(parent.SwiftTypeName.ToString());

        var (emitted, cs) = Emit(Operator(symbol, parent, Optional(ObjCClass), rhs), database);

        Assert.True(emitted, cs);
        Assert.True(cs.Contains(" operator " + symbol + "(", StringComparison.Ordinal), cs);
        AssertAdoptsOptionalObjCResult(Body(cs), "Foundation.NSNumber");
    }

    /// <summary>
    /// A throwing operator holds the raw result until the error check has run, so the Optional ObjC
    /// result is adopted only after it: a thrown error leaves the result register undefined.
    /// </summary>
    [Fact]
    public async Task OptionalObjCResult_OfThrowingOperator_IsAdoptedAfterTheErrorCheck()
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(isClass: true);
        var op = Operator("+", parent, Optional(ObjCClass), new NamedTypeSpec(parent.SwiftTypeName.ToString()), throws: true);

        var (emitted, cs) = Emit(op, database);

        Assert.True(emitted, cs);
        var body = Body(cs);
        AssertAdoptsOptionalObjCResult(body, "Foundation.NSNumber");
        var errorCheck = body.IndexOf("ThrowSwiftError(", StringComparison.Ordinal);
        Assert.True(errorCheck >= 0, $"a throwing operator checks the error slot:\n{body}");
        Assert.True(errorCheck < body.IndexOf("GetINativeObject", StringComparison.Ordinal),
            $"the error check runs before the result is adopted:\n{body}");
    }

    /// <summary>
    /// An Optional operand crosses as the <c>{name}Buffer</c> local the P/Invoke names, declared before
    /// the call from the operand's live handle, nil as a null pointer — as an ordinary method declares it.
    /// </summary>
    [Theory]
    [InlineData(true, ObjCClass, "GetLiveObjCHandleOrZero(rhs)")]
    [InlineData(false, ObjCClass, "GetLiveObjCHandleOrZero(rhs)")]
    [InlineData(true, SwiftClass, "rhs")]
    [InlineData(false, SwiftClass, "rhs")]
    // An Apple ObjC class with no record of its own, recognized by the projection's framework fallback.
    [InlineData(true, "Foundation.NSDecimalNumber", "GetLiveObjCHandleOrZero(rhs)")]
    public async Task OptionalObjectOperand_DeclaresTheBufferTheCallReads(bool classParent, string operandType, string handleSource)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(classParent);

        var (emitted, cs) = Emit(Operator("&", parent, new NamedTypeSpec("Swift.Int"), Optional(operandType)), database);

        Assert.True(emitted);
        var body = Body(cs);
        var declaration = Regex.Match(body, @"(global::System\.)?IntPtr rhsBuffer = (?<value>[^;]+);");
        Assert.True(declaration.Success, $"the operand's buffer is declared:\n{body}");
        Assert.Contains(handleSource, declaration.Groups["value"].Value);
        var call = Regex.Match(body, @"PInvoke_op_BitwiseAnd\([^;]*\brhsBuffer\b");
        Assert.True(call.Success, $"the call passes the declared buffer:\n{body}");
        Assert.True(declaration.Index < call.Index, "the buffer is declared before the call");
    }

    /// <summary>
    /// An equality operator's null guards answer for an operand C# null cannot reach Swift with. An
    /// Optional operand's null is nil, a value Swift compares, so it reaches the call: the guard is
    /// kept for the non-optional receiver only.
    /// </summary>
    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    public async Task EqualityOperator_OptionalOperandsNil_ReachesSwift(string symbol)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(isClass: true);

        var (emitted, cs) = Emit(Operator(symbol, parent, new NamedTypeSpec("Swift.Bool"), Optional(ObjCClass)), database);

        Assert.True(emitted, cs);
        var body = Body(cs);
        Assert.Matches(@"if \(\(?(object\?\))?lhs is null\) return", body);
        Assert.DoesNotMatch(@"if \(\(?(object\?\))?rhs is null\) return", body);
        Assert.Matches(@"PInvoke_op_\w+\([^;]*\brhsBuffer\b", body);
    }

    /// <summary>
    /// The partner C# requires for a declared <c>==</c> (or <c>!=</c>) is synthesized as its negation,
    /// and it keeps no null guard for an Optional operand either: the declared operator passes nil on.
    /// </summary>
    [Theory]
    [InlineData("==", "!=")]
    [InlineData("!=", "==")]
    public async Task SynthesizedEqualityPartner_OptionalOperandsNil_ReachesTheDeclaredOperator(string declared, string synthesized)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(isClass: true);
        var op = Operator(declared, parent, new NamedTypeSpec("Swift.Bool"), Optional(ObjCClass));
        var cs = new StringWriter();

        new OperatorHandler(NullLogger.Instance).EmitSynthesizedPairedOperator(new CSharpWriter(cs), op, synthesized, "Box", database, isReferenceType: true);

        var body = cs.ToString();
        Assert.Contains($"operator {synthesized}(", body);
        Assert.Matches(@"if \(\(?(object\?\))?left is null\) return", body);
        Assert.DoesNotMatch(@"if \(\(?(object\?\))?right is null\) return", body);
    }

    /// <summary>
    /// Only an Optional object operand is converted by the operator body: its object pointer is what
    /// Swift reads. An Optional collection of ObjC objects renders as an <c>NSArray</c> on the direct
    /// path, which is not Swift's array storage, so that operator is refused with a row rather than
    /// passed the wrong object.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalCollectionOperand_IsRefusedNotConverted(bool classParent)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(classParent);
        var operand = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Array", new NamedTypeSpec(ObjCClass)));

        var (emitted, cs) = Emit(Operator("&", parent, new NamedTypeSpec("Swift.Int"), operand), database);

        Assert.False(emitted, cs);
        Assert.DoesNotContain("rhsBuffer = ", cs);
        Assert.Contains("passes 'rhsBuffer', which no operand conversion declares", cs);
    }

    /// <summary>
    /// An <c>inout</c> Optional object operand is the address of a writable Optional slot, not the
    /// object pointer the conversion produces: the operator is refused with a row.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InOutOptionalObjectOperand_IsRefusedNotConverted(bool classParent)
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(classParent);

        var (emitted, cs) = Emit(Operator("&", parent, new NamedTypeSpec("Swift.Int"), Optional(ObjCClass), rhsInOut: true), database);

        Assert.False(emitted, cs);
        Assert.DoesNotContain("rhsBuffer = ", cs);
    }

    /// <summary>
    /// Positive control: an Optional of a Swift class keeps its own arm (one +1 reference adopted
    /// as the projected class), and a non-optional ObjC result keeps the borrowed-then-released read.
    /// </summary>
    [Fact]
    public async Task Controls_OptionalSwiftClassAndBareObjCResults_AreUnchanged()
    {
        var database = await CreateDatabaseAsync();
        var parent = Parent(isClass: false);
        var self = new NamedTypeSpec(parent.SwiftTypeName.ToString());

        var (optionalSwift, optionalSwiftCs) = Emit(Operator("*", parent, Optional(SwiftClass), self), database);
        var (bareObjC, bareObjCCs) = Emit(Operator("-", parent, new NamedTypeSpec(ObjCClass), self), database);

        Assert.True(optionalSwift);
        Assert.Contains("return null;", optionalSwiftCs);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.Payload>", optionalSwiftCs);
        Assert.True(bareObjC);
        Assert.Contains("GetNSObject<Foundation.NSNumber>", bareObjCCs);
        Assert.Contains("DangerousRelease()", bareObjCCs);
    }
}
