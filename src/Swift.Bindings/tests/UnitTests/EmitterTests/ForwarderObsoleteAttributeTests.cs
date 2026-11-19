// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A convenience forwarder (the <c>int</c> overload of an <c>nint</c> member, the <c>string</c>
/// overload of a URL member, the <c>int</c> indexer) calls its primary. When the primary carries an
/// <c>[Obsolete]</c> — a Swift deprecation, or the SB0008 consume-degrade marker — an unmarked
/// forwarder warns at that call (CS0618, or SB0008), and a binding built with warnings as errors
/// fails. Each forwarder therefore carries the primary's own marker, the same line. The primary and
/// its forwarders are emitted through the real handler, post-processors included.
/// </summary>
public class ForwarderObsoleteAttributeTests
{
    public enum MemberKind { Instance, Static, FreeFunction, StructMethod, Constructor }
    public enum MarkerKind { Deprecation, ConsumeDegraded }

    [Theory]
    [InlineData(MemberKind.Instance, MarkerKind.Deprecation)]
    [InlineData(MemberKind.Instance, MarkerKind.ConsumeDegraded)]
    [InlineData(MemberKind.Static, MarkerKind.Deprecation)]
    [InlineData(MemberKind.Static, MarkerKind.ConsumeDegraded)]
    [InlineData(MemberKind.FreeFunction, MarkerKind.Deprecation)]
    [InlineData(MemberKind.FreeFunction, MarkerKind.ConsumeDegraded)]
    [InlineData(MemberKind.StructMethod, MarkerKind.Deprecation)]
    [InlineData(MemberKind.StructMethod, MarkerKind.ConsumeDegraded)]
    [InlineData(MemberKind.Constructor, MarkerKind.Deprecation)]
    [InlineData(MemberKind.Constructor, MarkerKind.ConsumeDegraded)]
    public void IntForwarder_CarriesThePrimarysObsolete(MemberKind kind, MarkerKind marker)
    {
        var (module, member) = CreateMember(kind, ("n", new NamedTypeSpec("Swift.Int")));
        var context = ApplyMarker(member, marker);

        var cs = Emit(member, context);

        var primary = ObsoleteLineOf(cs, "(nint n)");
        var forwarder = ObsoleteLineOf(cs, "(int n)");
        Assert.True(primary != null, $"the primary carries its marker:\n{cs}");
        Assert.Contains(marker == MarkerKind.Deprecation ? "Deprecated: use the new one" : "SB0008", primary);
        Assert.True(forwarder != null, $"the int forwarder carries a marker:\n{cs}");
        Assert.Equal(primary, forwarder);
        if (kind == MemberKind.Constructor)
            Assert.Contains(": this((nint)n)", cs);
    }

    [Theory]
    [InlineData(MarkerKind.Deprecation)]
    [InlineData(MarkerKind.ConsumeDegraded)]
    public void UrlStringForwarder_CarriesThePrimarysObsolete(MarkerKind marker)
    {
        var (_, member) = CreateMember(MemberKind.Instance, ("url", new NamedTypeSpec("Foundation.URL")));
        var context = ApplyMarker(member, marker);

        var cs = Emit(member, context);

        var primary = ObsoleteLineOf(cs, "(Foundation.NSUrl url)");
        var forwarder = ObsoleteLineOf(cs, "(string url)");
        Assert.True(primary != null, $"the primary carries its marker:\n{cs}");
        Assert.True(forwarder != null, $"the string forwarder carries a marker:\n{cs}");
        Assert.Equal(primary, forwarder);
    }

    [Fact]
    public void IntForwarder_OfAnUnmarkedPrimary_CarriesNone()
    {
        var (_, member) = CreateMember(MemberKind.Instance, ("n", new NamedTypeSpec("Swift.Int")));

        var cs = Emit(member, new ModuleEmissionContext());

        Assert.Contains("(int n)", cs);
        Assert.DoesNotContain("Obsolete", cs);
    }

    [Fact]
    public void IntIndexerForwarder_CarriesTheDeprecatedSubscriptsObsolete()
    {
        // The primary indexer's only [Obsolete] is its Swift deprecation.
        var module = CreateModuleDecl();
        var parent = CreateClassDecl("Loader", module);
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s10TestModule6LoaderCys5Int32VSicig",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IndexParameters = new List<ArgumentDecl> { CreateArg("i", new NamedTypeSpec("Swift.Int"), module) },
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = null! } },
            AvailabilityAnnotations = new List<AvailabilityAnnotation> { Deprecated("use the new one") },
            ParentDecl = parent,
            ModuleDecl = module,
        };
        var writer = new StringWriter();

        NativeIntOverloadEmitter.TryEmitIndexerOverload(
            new CSharpWriter(writer), subscript, "int", new() { ("nint", "i", null) });

        var cs = writer.ToString();
        Assert.Contains("this[int i] => this[(nint)i]", cs);
        Assert.Contains("[Obsolete(\"use the new one\")]", cs);
    }

    #region Helpers

    private static AvailabilityAnnotation Deprecated(string message) =>
        new(Platform: null, IntroducedVersion: null, DeprecatedVersion: null, ObsoletedVersion: null,
            IsUnconditionallyDeprecated: true, IsUnconditionallyUnavailable: false, Message: message, Renamed: null);

    private static ModuleEmissionContext ApplyMarker(MethodDecl member, MarkerKind marker)
    {
        var context = new ModuleEmissionContext();
        if (marker == MarkerKind.Deprecation)
            member.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Deprecated("use the new one") };
        else
            // What the marshalling emitters record when a parameter drops its C#-conformer fallback.
            context.RecordConsumeDegradedMember(member);
        return context;
    }

    /// <summary>
    /// The <c>[Obsolete]</c> line among the attributes directly above the member whose signature
    /// contains <paramref name="signatureFragment"/>, or null when it has none.
    /// </summary>
    private static string? ObsoleteLineOf(string cs, string signatureFragment)
    {
        var lines = cs.Split('\n').Select(l => l.Trim()).ToList();
        var index = lines.FindIndex(l => l.StartsWith("public ", System.StringComparison.Ordinal)
            && l.Replace("( ", "(").Replace(",  ", ", ").Contains(signatureFragment, System.StringComparison.Ordinal));
        Assert.True(index >= 0, $"a member with '{signatureFragment}' is emitted:\n{cs}");
        for (int i = index - 1; i >= 0 && (lines[i].StartsWith("[") || lines[i].StartsWith("//")); i--)
        {
            if (lines[i].StartsWith("[Obsolete", System.StringComparison.Ordinal)
                || lines[i].StartsWith("[global::System.Obsolete", System.StringComparison.Ordinal))
                return lines[i];
        }
        return null;
    }

    private static (ModuleDecl, MethodDecl) CreateMember(MemberKind kind, (string Name, TypeSpec Type) parameter)
    {
        var module = CreateModuleDecl();
        BaseDecl parent = kind switch
        {
            MemberKind.FreeFunction => module,
            MemberKind.StructMethod => CreateStructDecl("Cell", module),
            _ => CreateClassDecl("Loader", module),
        };
        var isConstructor = kind == MemberKind.Constructor;
        var parentName = parent is TypeDecl t ? t.Name : string.Empty;
        var returnSpec = isConstructor ? new NamedTypeSpec($"TestModule.{parentName}") : new NamedTypeSpec("Swift.Int");
        var method = new MethodDecl
        {
            Name = isConstructor ? "init" : "work",
            MangledName = isConstructor
                ? $"$s10TestModule{parentName.Length}{parentName}C1nACSi_tcfc"
                : kind == MemberKind.FreeFunction
                    ? "$s10TestModule4workyS2iF"
                    : $"$s10TestModule{parentName.Length}{parentName}C4workyS2iF",
            MethodType = kind == MemberKind.Static ? MethodType.Static : MethodType.Instance,
            IsFinal = kind == MemberKind.Static,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", returnSpec, module),
                CreateArg(parameter.Name, parameter.Type, module),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        // A struct method without a wrapper carries SB0001 of its own (its self crosses by
        // pointer), which would be the one marker; the wrapper leaves only the marker under test.
        if (kind == MemberKind.StructMethod)
            method.UsesCdeclMethodWrapper = true;
        if (parent is TypeDecl typeParent)
            typeParent.Methods.Add(method);
        else
            module.Methods.Add(method);
        module.ExportedSymbols = new HashSet<string> { method.MangledName };
        return (module, method);
    }

    private static string Emit(MethodDecl member, ModuleEmissionContext emissionContext)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());
        var env = new MethodEnvironment(member, CreateTypeDatabase());
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: emissionContext);
        if (member.IsConstructor)
            new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>()).Emit(csWriter, swiftWriter, env, conductor, context);
        else
            new MethodHandler(new NullLogger<MethodHandler>()).Emit(csWriter, swiftWriter, env, conductor, context);
        return csOutput.ToString();
    }

    private static ModuleDecl CreateModuleDecl() => new()
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ClassDecl CreateClassDecl(string name, ModuleDecl module)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
        module.Types.Add(decl);
        return decl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl module)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = false,
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
        module.Types.Add(decl);
        return decl;
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl module) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = typeSpec,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = module,
    };

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDb = new TypeDatabase();
        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swift.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.NIntType,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        typeDb.AddModuleDatabase(swift);

        var foundation = new ModuleTypeDatabase("Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        foundation.RegisterType(SwiftTypeName.FromModuleQualifiedName("Foundation.URL"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
            MetadataAccessor = "$s10Foundation3URLVMa",
            Flags = TypeRecordFlags.ObjCBridgeable,
            Kind = TypeRecordKind.Struct,
        });
        typeDb.AddModuleDatabase(foundation);

        var test = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        test.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            MetadataAccessor = "$s10TestModule6LoaderCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        test.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Cell"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Cell"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Cell"),
            MetadataAccessor = "$s10TestModule4CellVMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
        });
        typeDb.AddModuleDatabase(test);
        return typeDb;
    }

    #endregion
}
