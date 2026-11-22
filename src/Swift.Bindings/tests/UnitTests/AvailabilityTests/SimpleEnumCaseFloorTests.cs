// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A simple enum's case floored above the enum is refused in managed code before its value reaches
/// Swift, which has no such case on an older OS and traps (the case-by-tag switch's
/// <c>fatalError</c>, or a nil <c>init(rawValue:)</c> under the wrapper's precondition). The enum
/// gets a generated <c>__StatusCaseFloors.Require</c> beside it, its type record names that
/// method, and every site handing the enum's value to native code calls it; a value Swift returns
/// is a plain cast.
/// </summary>
public class SimpleEnumCaseFloorTests
{
    private const string Module = "TestModule";
    private const string Guard = "global::TestModule.__StatusCaseFloors.Require";

    [Fact]
    public void FlooredCase_GetsARequireThatRefusesItBelowItsFloor_AndPassesTheOtherCases()
    {
        var enumDecl = CreateEnum(("active", null), ("inactive", null), ("future", "99.0"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("weight", enumDecl));

        var cs = Emit(enumDecl);

        var require = EqualityFloorEmissionTests.Body(cs, "public static global::TestModule.Status Require(global::TestModule.Status value)");
        var future = require.IndexOf("case global::TestModule.Status.Future:", StringComparison.Ordinal);
        Assert.True(future > 0, require);
        var refusal = require.IndexOf("PlatformNotSupportedException", future, StringComparison.Ordinal);
        Assert.True(refusal > future, require);
        Assert.Contains("\"ios\", 99, 0", require[future..refusal]);
        Assert.DoesNotContain("Status.Active", require);
        Assert.DoesNotContain("Status.Inactive", require);
        Assert.Contains("return value;", require);
        Assert.Contains("public static class __StatusCaseFloors", cs);
        AssertParses(cs);
    }

    /// <summary>
    /// Each enum with a floored case gets its own guard class, named after it, so two enums of one
    /// namespace compile together, and two assemblies binding into one namespace declare no class
    /// twice (a consumer naming a class both declare gets CS0433). The guard names the enum fully
    /// qualified, so an enum named like the guard's method still resolves to the enum (CS0119).
    /// </summary>
    [Fact]
    public void TwoEnumsWithFlooredCases_GetTheirOwnGuardClasses_AndCompileTogether()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "99.0"));
        var trees = new[] { "Status", "Other", "Require" }.Select(name =>
        {
            var text = new StringWriter();
            var writer = new CSharpWriter(text);
            writer.WriteLine($"namespace {Module}");
            writer.WriteLine("{");
            writer.Indent++;
            writer.WriteLine($"public enum {name} : int {{ Active = 0, Future = 1 }}");
            SimpleEnumCaseFloors.EmitGuard(writer, enumDecl, CSharpTypeName.FromNamespaceAndName(Module, name), null);
            writer.Indent--;
            writer.WriteLine("}");
            writer.Flush();
            return CSharpSyntaxTree.ParseText(text.ToString(), path: $"{name}.cs");
        }).ToList();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));

        var errors = CSharpCompilation.Create("CaseFloorGuards", trees, references,
                new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.Empty(errors);
        Assert.All(trees, tree => Assert.Contains("[global::System.ComponentModel.EditorBrowsable", tree.ToString()));
    }

    [Fact]
    public void EnumWithoutAFlooredCase_GetsNoRequire_AndCastsItsValuesDirectly()
    {
        var enumDecl = CreateEnum(("active", null), ("inactive", null));
        enumDecl.Properties.Add(CreateInstanceIntProperty("weight", enumDecl));

        var cs = Emit(enumDecl);

        Assert.DoesNotContain("CaseFloors", cs);
        Assert.Contains("PInvoke_GetWeight((int)self)", cs);
    }

    /// <summary>A case at its enum's own floor is reachable wherever the enum is: nothing to refuse.</summary>
    [Fact]
    public void CaseFlooredOnlyAsHighAsItsEnum_IsNotRefused()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "99.0"));
        enumDecl.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann("99.0") };

        Assert.Empty(SimpleEnumCaseFloors.FlooredCases(enumDecl));
        Assert.False(SimpleEnumCaseFloors.NeedsGuard(enumDecl));
    }

    /// <summary>
    /// A static class beside an enum inside a generic type cannot be named from outside it (its
    /// type-record name carries no type arguments), so such an enum gets no guard.
    /// </summary>
    [Fact]
    public void EnumInsideAGenericType_GetsNoGuard()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "99.0"));
        Assert.True(SimpleEnumCaseFloors.NeedsGuard(enumDecl));
        var box = new StructDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Box"),
            MangledName = "$s10TestModule3BoxVMn",
            MetadataAccessor = "$s10TestModule3BoxVMa",
            IsFrozen = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { enumDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()) },
            Conformances = new List<TypeConformance>(),
            ParentDecl = enumDecl.ModuleDecl,
            ModuleDecl = enumDecl.ModuleDecl,
        };
        enumDecl.ParentDecl = box;

        Assert.False(SimpleEnumCaseFloors.NeedsGuard(enumDecl));
    }

    /// <summary>
    /// The guard is named from the record's current C# name, so renaming an enclosing type (which
    /// re-stamps the nested enum's record) moves the guard with the class emitted inside it.
    /// </summary>
    [Fact]
    public void CaseFloorGuard_FollowsARenameOfTheEnclosingType()
    {
        var record = SimpleEnumRecord(SwiftTypeName.FromModuleQualifiedName($"{Module}.Mode.Status"), "Mode.Status", Guard);
        Assert.Equal("global::TestModule.Mode.__StatusCaseFloors.Require", record.CaseFloorGuard);

        var renamed = record with { CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, "ModeType.Status") };

        Assert.Equal("global::TestModule.ModeType.__StatusCaseFloors.Require", renamed.CaseFloorGuard);
    }

    /// <summary>
    /// A case can carry a declared floor and an inferred one for the same platform. The Swift
    /// wrappers' case switches (self and enum arguments) name the platform once, at the higher
    /// floor: an <c>#available</c> naming a platform twice does not compile.
    /// </summary>
    [Fact]
    public void CaseWithTwoFloorsOnOnePlatform_IsCheckedOnceAtTheHigher()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "15.0"));
        enumDecl.Cases[1].AvailabilityAnnotations!.Add(Ann("17.0") with { IsInferredOsFloor = true });
        enumDecl.Properties.Add(CreateInstanceIntProperty("weight", enumDecl));
        enumDecl.Methods.Add(CreateStaticMethod("isValid", enumDecl,
            returns: "Swift.Bool", parameter: $"{Module}.Status"));

        var swift = EmitBoth(enumDecl).Swift;

        Assert.Contains("#available(iOS 17.0, *)", swift);
        Assert.DoesNotContain("iOS 15.0", swift);
    }

    [Fact]
    public void EnumsOwnMembers_PassSelfAndEnumArgumentsThroughRequire()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "99.0"));
        enumDecl.Properties.Add(CreateInstanceIntProperty("weight", enumDecl));
        enumDecl.Methods.Add(CreateStaticMethod("isValid", enumDecl,
            returns: "Swift.Bool", parameter: $"{Module}.Status"));

        var cs = Emit(enumDecl);

        Assert.Contains($"PInvoke_GetWeight((int){Guard}(self))", cs);
        Assert.Contains($"(int){Guard}(value)", cs);
        Assert.DoesNotContain("PInvoke_GetWeight((int)self)", cs);
    }

    /// <summary>
    /// A value Swift hands back is a case the running OS has (an OS without the case cannot produce
    /// it), so the return projection stays a plain cast and never throws.
    /// </summary>
    [Fact]
    public void EnumReturnedFromSwift_IsAPlainCast()
    {
        var enumDecl = CreateEnum(("active", null), ("future", "99.0"));
        enumDecl.Methods.Add(CreateStaticMethod("latest", enumDecl, returns: $"{Module}.Status", parameter: null));

        var cs = Emit(enumDecl);

        Assert.Contains("(Status)PInvoke_Latest()", cs);
        Assert.DoesNotContain($"{Guard}(PInvoke_Latest", cs);
    }

    /// <summary>
    /// A parameter of the enum on another type's member is spelled through the guard the enum's
    /// type record names, so a member of any type refuses the floored case the same way.
    /// </summary>
    [Fact]
    public void SimpleEnumParameterOfAnotherMember_IsSpelledThroughTheRecordsGuard()
    {
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, "Status"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Status"),
            MetadataAccessor = "$s10TestModule6StatusOMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            HasCaseFloorGuard = true,
        };

        Assert.Equal($"(int){Guard}(mode)", SimpleEnumCaseFloors.ToNative(record, "int", "mode"));
        Assert.Equal("(int)mode", SimpleEnumCaseFloors.ToNative(record with { HasCaseFloorGuard = false }, "int", "mode"));
        Assert.Equal($"{Guard}(mode)", SimpleEnumCaseFloors.RequireAvailable(record, "mode"));
    }

    /// <summary>
    /// A downstream binding reads the enum's record from the module database, so the guard must
    /// survive the round trip or the downstream refuses nothing.
    /// </summary>
    [Fact]
    public async Task CaseFloorGuard_RoundTripsThroughTheModuleDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mdb_casefloor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var module = new ModuleTypeDatabase(Module, "/fake/TestModule.dylib");
            var floored = SwiftTypeName.FromModuleQualifiedName($"{Module}.Status");
            var plain = SwiftTypeName.FromModuleQualifiedName($"{Module}.Plain");
            module.RegisterType(floored, SimpleEnumRecord(floored, "Status", Guard));
            module.RegisterType(plain, SimpleEnumRecord(plain, "Plain", null));

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);
            var typeDatabase = new TypeDatabase();
            await typeDatabase.LoadModuleDatabaseFromFile(path!);

            Assert.True(typeDatabase.TryGetTypeRecord(floored, out var loaded));
            Assert.Equal(Guard, loaded!.CaseFloorGuard);
            Assert.True(typeDatabase.TryGetTypeRecord(plain, out var loadedPlain));
            Assert.Null(loadedPlain!.CaseFloorGuard);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- helpers ---

    private static AvailabilityAnnotation Ann(string introduced) =>
        new("iOS", introduced, null, null, false, false, null, null);

    private static TypeRecord SimpleEnumRecord(SwiftTypeName name, string csName, string? guard) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, csName),
        SwiftTypeName = name,
        MetadataAccessor = $"$s10TestModule{csName.Length}{csName}OMa",
        Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
        Kind = TypeRecordKind.Enum,
        HasCaseFloorGuard = guard != null,
    };

    private static EnumDecl CreateEnum(params (string Name, string? Floor)[] cases)
    {
        var module = new ModuleDecl
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
        var enumDecl = new EnumDecl
        {
            Name = "Status",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Status"),
            MangledName = "$s10TestModule6StatusON",
            MetadataAccessor = "$s10TestModule6StatusOMa",
            IsFrozen = true,
            Cases = new List<EnumCaseDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        foreach (var (name, floor) in cases)
        {
            var caseDecl = new EnumCaseDecl
            {
                Name = name,
                MangledName = $"$s10TestModule6StatusO{name}yA2CmFWC",
                AssociatedValues = new List<TypeSpec>(),
                ParentDecl = enumDecl,
                ModuleDecl = module,
            };
            if (floor != null)
                caseDecl.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann(floor) };
            enumDecl.Cases.Add(caseDecl);
        }
        return enumDecl;
    }

    private static ArgumentDecl Arg(string type, string label, ModuleDecl? module) => new()
    {
        SwiftTypeSpec = new NamedTypeSpec(type), Name = label, PrivateName = label,
        IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = module,
    };

    private static PropertyDecl CreateInstanceIntProperty(string name, EnumDecl enumDecl) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
        IsStatic = false,
        HasStorage = false,
        Accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s10TestModule6StatusO{name}Sivg",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl> { Arg("Swift.Int", string.Empty, enumDecl.ModuleDecl) },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = enumDecl,
                    ModuleDecl = enumDecl.ModuleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false,
                },
            },
        },
        ParentDecl = enumDecl,
        ModuleDecl = enumDecl.ModuleDecl,
    };

    private static MethodDecl CreateStaticMethod(string name, EnumDecl enumDecl, string returns, string? parameter)
    {
        var signature = new List<ArgumentDecl> { Arg(returns, string.Empty, enumDecl.ModuleDecl) };
        if (parameter != null)
            signature.Add(Arg(parameter, "value", enumDecl.ModuleDecl));
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6StatusO{name}{(parameter != null ? "ySbAC" : "AC")}FZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = enumDecl.ModuleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
    }

    /// <summary>
    /// Emits the enum through <see cref="EnumHandler"/> against a database holding the enum's
    /// record as <see cref="ModuleProcessor"/> registers it (its guard from
    /// <see cref="SimpleEnumCaseFloors.NeedsGuard"/>).
    /// </summary>
    private static string Emit(EnumDecl enumDecl) => EmitBoth(enumDecl).CSharp;

    private static (string CSharp, string Swift) EmitBoth(EnumDecl enumDecl)
    {
        var typeDatabase = new TypeDatabase();
        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        foreach (var (name, cs, accessor) in new[]
        {
            ("Swift.Int", CSharpTypeName.NIntType, "$sSiMa"),
            ("Swift.Bool", CSharpTypeName.FromNamespaceAndName("System", "Boolean"), "$sSbMa"),
        })
        {
            swift.RegisterType(SwiftTypeName.FromModuleQualifiedName(name), new TypeRecord
            {
                CSharpTypeName = cs,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(name),
                MetadataAccessor = accessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        }
        typeDatabase.AddModuleDatabase(swift);
        var module = new ModuleTypeDatabase(Module, "/tmp/TestModule.dylib");
        module.RegisterType(enumDecl.SwiftTypeName, SimpleEnumRecord(enumDecl.SwiftTypeName, "Status",
            SimpleEnumCaseFloors.NeedsGuard(enumDecl) ? Guard : null));
        typeDatabase.AddModuleDatabase(module);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new EnumHandler(new NullLogger<EnumHandler>());
        var env = handler.Marshal(enumDecl, typeDatabase);
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env, new Conductor(new NullLoggerFactory()), context);
        return (csOutput.ToString(), SimpleEnumCrossing.AppendHelperDefinitions(swiftOutput.ToString()));
    }

    private static void AssertParses(string cs)
    {
        var errors = CSharpSyntaxTree.ParseText(cs).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.Empty(errors);
    }
}
