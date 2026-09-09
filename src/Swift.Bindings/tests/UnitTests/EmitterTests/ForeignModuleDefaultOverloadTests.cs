// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A defaulted parameter whose written expression names a member of a module the binding does not
/// own — a static factory call or a static property on a platform-vended type — cannot become a C#
/// compile-time constant, so the value has to be produced on the Swift side. The only entry point
/// the generator may name for that is one it mints itself: a symbol for a foreign member is a name
/// the generator would be guessing at, and a guessed mangled name links cleanly and then throws
/// <c>EntryPointNotFoundException</c> at the first call.
///
/// These tests pin both halves — the mapper declines the expression rather than inventing a C#
/// spelling of it, and the reduced overload the generator emits in its place calls through the
/// generator's own wrapper with one fewer argument, naming no Swift-mangled entry point at all.
/// </summary>
public class ForeignModuleDefaultOverloadTests
{
    private const string ForeignModule = "Dispatch";
    private const string ForeignType = "DispatchQueue";
    private const string OwningType = "DefaultBox";

    /// <summary>
    /// Expressions of the shape a platform default takes: a static factory call (with and without
    /// arguments of its own), a bare static property reference, and both spelled out with their type.
    /// None is a value C# can write down, so each must be declined.
    /// </summary>
    [Theory]
    [InlineData(".global()")]
    [InlineData(".global(qos: .background)")]
    [InlineData(".main")]
    [InlineData("DispatchQueue.global()")]
    [InlineData("DispatchQueue.main")]
    [InlineData("Dispatch.DispatchQueue.main")]
    public void ForeignMemberDefault_IsDeclinedRatherThanTranslated(string swiftDefaultExpression)
    {
        var typeDb = CreateTypeDatabase();

        var mapped = SwiftDefaultValueMapper.TryMapToCSharpDefault(
            swiftDefaultExpression,
            new NamedTypeSpec($"{ForeignModule}.{ForeignType}"),
            typeDb);

        Assert.Null(mapped);
    }

    /// <summary>
    /// The negative above is only meaningful if the mapper still says yes to something. A case of an
    /// enum it can see keeps "declined" a statement about the expression rather than about the
    /// fixture being too impoverished for anything to map. The expected value is pinned rather than
    /// merely non-null, so a mapper that started returning some other constant would not pass as a
    /// working control.
    /// </summary>
    [Fact]
    public void MapperStillAcceptsAnExpressionItCanRepresent()
    {
        var typeDb = CreateTypeDatabase();

        var mapped = SwiftDefaultValueMapper.TryMapToCSharpDefault(
            ".background",
            new NamedTypeSpec($"{ForeignModule}.QosClass"),
            typeDb);

        Assert.Equal($"{ForeignModule}.QosClass.Background", mapped);
    }

    /// <summary>
    /// A method whose only default is such an expression is not C#-mappable end to end, so the
    /// trailing-trim gate has to decline it too — that decline is what routes the member to a reduced
    /// overload backed by a Swift shim instead of an inline <c>= value</c> in the primary signature.
    /// </summary>
    [Fact]
    public void ForeignMemberDefault_IsNotTreatedAsAnInlineCSharpDefault()
    {
        var typeDb = CreateTypeDatabase();
        var method = CreateMethodWithForeignDefault(".global()");

        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    /// <summary>
    /// The reduced overload drops exactly the defaulted parameter — arity one less, the required
    /// parameter kept — and is re-pointed at the generator's own wrapper library rather than at
    /// whatever symbol the declaration itself was bound to.
    /// </summary>
    [Fact]
    public void ReducedOverload_DropsTheDefaultedParameterAndRetargetsTheWrapper()
    {
        var method = CreateMethodWithForeignDefault(".global()");
        var primaryArity = method.CSSignature.Count - 1;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method.MangledName, method, 1);

        Assert.Equal(primaryArity - 1, overload.CSSignature.Count - 1);
        Assert.Equal(new[] { "tag" }, overload.CSSignature.Skip(1).Select(a => a.Name).ToArray());
        Assert.True(overload.UsesWrapperLibrary);
        Assert.NotEqual(method.MangledName, overload.MangledName);
    }

    /// <summary>
    /// The claim this file exists for: nothing the generator emits for a foreign-module default names
    /// a Swift-mangled entry point. Every native symbol on the emitted overload path is one the
    /// generator minted for its own wrapper — a symbol it also emits the Swift definition of, and so
    /// one that is present in the library the binding links. A mangled name here would be the
    /// generator asserting the existence of a symbol it never produced.
    /// </summary>
    [Fact]
    public void EmittedOverload_NamesNoSwiftMangledEntryPoint()
    {
        var typeDb = CreateTypeDatabase();
        var method = CreateMethodWithForeignDefault(".global()");

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        var entryPoints = ExtractEntryPoints(csOutput);
        Assert.NotEmpty(entryPoints);
        Assert.All(entryPoints, entryPoint =>
            Assert.False(
                entryPoint.StartsWith("$s", StringComparison.Ordinal) ||
                entryPoint.StartsWith("$S", StringComparison.Ordinal),
                $"Overload path named the Swift-mangled entry point '{entryPoint}'; only generator-minted " +
                "wrapper symbols may appear here."));

        // Each of those symbols has to be one this emission actually defines, not merely one it names.
        foreach (var entryPoint in entryPoints)
            Assert.Contains(entryPoint, swiftOutput, StringComparison.Ordinal);
    }

    #region Helpers

    private static List<string> ExtractEntryPoints(string csOutput)
    {
        var entryPoints = new List<string>();
        const string marker = "EntryPoint = \"";
        var index = csOutput.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var start = index + marker.Length;
            var end = csOutput.IndexOf('"', start);
            if (end < 0)
                break;
            entryPoints.Add(csOutput.Substring(start, end - start));
            index = csOutput.IndexOf(marker, end, StringComparison.Ordinal);
        }
        return entryPoints;
    }

    /// <summary>
    /// A type database that knows the foreign module's class (so the mapper can look it up and still
    /// decline it) alongside a simple enum in the same module (the mapper's positive control) and the
    /// primitives the fixture method's other parameter uses.
    /// </summary>
    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDb = new TypeDatabase();

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
        typeDb.AddModuleDatabase(swiftModule);

        var foreignModule = new ModuleTypeDatabase(
            ForeignModule, $"/usr/lib/swift/libswift{ForeignModule}.dylib");
        foreignModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{ForeignModule}.{ForeignType}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ForeignModule, ForeignType),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ForeignModule}.{ForeignType}"),
                MetadataAccessor = $"$s8{ForeignModule}0A5QueueCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        foreignModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{ForeignModule}.QosClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ForeignModule, "QosClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ForeignModule}.QosClass"),
                MetadataAccessor = $"$s8{ForeignModule}8QosClassOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        typeDb.AddModuleDatabase(foreignModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{OwningType}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", OwningType),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{OwningType}"),
                MetadataAccessor = $"$s10TestModule{OwningType.Length}{OwningType}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        return typeDb;
    }

    /// <summary>
    /// <c>testMethod(tag: Int, queue: DispatchQueue = &lt;expr&gt;)</c> — one required parameter, one
    /// trailing default whose expression names a member of another module.
    /// </summary>
    private static MethodDecl CreateMethodWithForeignDefault(string swiftDefaultExpression)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var returnArg = new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var tag = new ArgumentDecl
        {
            Name = "tag",
            PrivateName = "tag",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        var queue = new ArgumentDecl
        {
            Name = "queue",
            PrivateName = "queue",
            SwiftTypeSpec = new NamedTypeSpec($"{ForeignModule}.{ForeignType}"),
            HasDefaultArg = true,
            SwiftDefaultExpression = swiftDefaultExpression,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new StructDecl
        {
            Name = OwningType,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{OwningType}"),
            MangledName = $"$s10TestModule{OwningType.Length}{OwningType}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{OwningType.Length}{OwningType}VMa"
        };

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule10DefaultBoxV10testMethodyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { returnArg, tag, queue },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static (string csOutput, string swiftOutput) EmitOverloads(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var env = new MethodEnvironment(methodDecl, typeDatabase);

        DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, env, NullLogger.Instance);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
