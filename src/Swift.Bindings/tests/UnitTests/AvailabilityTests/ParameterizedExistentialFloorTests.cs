// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A member that moves a parameterized protocol existential (<c>any Requestable&lt;Value, Failure&gt;</c>)
/// through its wrapper instantiates that existential's metadata, which Swift only provides from
/// iOS 16 / macOS 13 / tvOS 16 / watchOS 9. These tests cover which signatures carry such a type and
/// how the floor lands on the member so the Swift wrapper, the C# platform attributes and the protocol
/// witness guard all read the same answer.
/// </summary>
public class ParameterizedExistentialFloorTests
{
    private const string Module = "TestModule";

    private static TypeDatabase Db()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase(Module, $"/tmp/{Module}.dylib");
        void Register(string name, TypeRecordKind kind, TypeRecordFlags flags = TypeRecordFlags.None)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}");
            module.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = kind,
            });
        }
        Register("Requestable", TypeRecordKind.Protocol, TypeRecordFlags.HasAssociatedTypes);
        Register("Box", TypeRecordKind.Struct);
        db.AddModuleDatabase(module);
        return db;
    }

    private static TypeSpec Spec(string text) => TypeSpecParser.Parse(text)!;

    private static AvailabilityAnnotation Ann(string platform, string? introduced, bool unavailable = false) =>
        new(platform, introduced, null, null, false, unavailable, null, null);

    private static ModuleDecl ModuleWith(params BaseDecl[] members)
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
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDecl method: module.Methods.Add(method); break;
                case PropertyDecl property: module.Properties.Add(property); break;
                case ProtocolDecl protocol: module.Protocols.Add(protocol); break;
                case TypeDecl type: module.Types.Add(type); break;
            }
        }
        return module;
    }

    private static string? Introduced(BaseDecl decl, string platform) =>
        decl.AvailabilityAnnotations?.FirstOrDefault(a => a.Platform == platform)?.IntroducedVersion;

    // --- Which signatures carry the type ---

    [Theory]
    [InlineData("any TestModule.Requestable<Swift.Int, Swift.String>")]
    [InlineData("TestModule.Requestable<Swift.Int, Swift.String>")]
    [InlineData("Swift.Optional<any TestModule.Requestable<Swift.Int, Swift.String>>")]
    [InlineData("Swift.Array<any TestModule.Requestable<Swift.Int, Swift.String>>")]
    [InlineData("(Swift.Int, any TestModule.Requestable<Swift.Int, Swift.String>)")]
    [InlineData("(any TestModule.Requestable<Swift.Int, Swift.String>) -> Swift.Void")]
    [InlineData("() -> any TestModule.Requestable<Swift.Int, Swift.String>")]
    public void SignatureNamingParameterizedExistential_IsDetected(string type)
    {
        Assert.True(ParameterizedExistentialFloor.ContainsParameterizedExistential(Spec(type), Db()));
    }

    [Theory]
    [InlineData("any TestModule.Requestable")]
    [InlineData("TestModule.Box<Swift.Int>")]
    [InlineData("Swift.Array<any Swift.Error>")]
    [InlineData("Swift.Dictionary<Swift.String, Swift.Int>")]
    [InlineData("Swift.Array<T>")]
    public void SignatureWithoutParameterizedExistential_IsNotDetected(string type)
    {
        Assert.False(ParameterizedExistentialFloor.ContainsParameterizedExistential(Spec(type), Db()));
    }

    [Fact]
    public void OpaqueResultOfParameterizedProtocol_IsNotDetected()
    {
        var opaque = new ProtocolListTypeSpec(
            [(NamedTypeSpec)Spec("TestModule.Requestable<Swift.Int, Swift.String>")]) { IsOpaque = true };

        Assert.False(ParameterizedExistentialFloor.ContainsParameterizedExistential(opaque, Db()));
    }

    // --- How the floor lands ---

    [Fact]
    public void Method_ReturningParameterizedExistential_IsRaisedOnEveryPlatform()
    {
        var method = TestDecls.Method("fetch", returnType: Spec("any TestModule.Requestable<Swift.Int, Swift.String>"));
        var untouched = TestDecls.Method("count", returnType: Spec("Swift.Int"));

        var raised = ParameterizedExistentialFloor.Apply(ModuleWith(method, untouched), Db());

        Assert.Equal(1, raised);
        Assert.Equal("16.0", Introduced(method, "iOS"));
        Assert.Equal("13.0", Introduced(method, "macOS"));
        Assert.Equal("16.0", Introduced(method, "tvOS"));
        Assert.Equal("9.0", Introduced(method, "watchOS"));
        Assert.All(method.AvailabilityAnnotations!, a => Assert.True(a.IsRuntimeSupportFloor));
        Assert.Null(untouched.AvailabilityAnnotations);
    }

    [Fact]
    public void Method_TakingParameterizedExistential_IsRaised()
    {
        var method = TestDecls.Method("run",
            parameters: [TestDecls.Param("request", Spec("any TestModule.Requestable<Swift.Int, Swift.String>"))]);

        ParameterizedExistentialFloor.Apply(ModuleWith(method), Db());

        Assert.Equal("16.0", Introduced(method, "iOS"));
    }

    [Fact]
    public void DeclaredFloorAtOrAboveRuntimeSupport_IsKept()
    {
        var method = TestDecls.Method("fetch", returnType: Spec("any TestModule.Requestable<Swift.Int, Swift.String>"));
        method.AvailabilityAnnotations = [Ann("iOS", "17.0")];

        ParameterizedExistentialFloor.Apply(ModuleWith(method), Db());

        var ios = Assert.Single(method.AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("17.0", ios.IntroducedVersion);
        Assert.False(ios.IsRuntimeSupportFloor);
        Assert.Equal("13.0", Introduced(method, "macOS"));
    }

    [Fact]
    public void DeclaredFloorBelowRuntimeSupport_GainsTheRuntimeFloor()
    {
        var method = TestDecls.Method("fetch", returnType: Spec("any TestModule.Requestable<Swift.Int, Swift.String>"));
        method.AvailabilityAnnotations = [Ann("iOS", "15.0")];

        ParameterizedExistentialFloor.Apply(ModuleWith(method), Db());

        var effective = AvailabilityHelpers.MergeAvailabilityFromAncestors(method.AvailabilityAnnotations, null);
        var ios = effective!.Where(a => a.Platform == "iOS" && a.IntroducedVersion is not null)
            .Max(a => new Version(a.IntroducedVersion!));
        Assert.Equal(new Version(16, 0), ios);
    }

    [Fact]
    public void PlatformTheMemberIsUnavailableOn_IsLeftAlone()
    {
        var method = TestDecls.Method("fetch", returnType: Spec("any TestModule.Requestable<Swift.Int, Swift.String>"));
        method.AvailabilityAnnotations = [Ann("watchOS", null, unavailable: true)];

        ParameterizedExistentialFloor.Apply(ModuleWith(method), Db());

        Assert.Null(Introduced(method, "watchOS"));
        Assert.Equal("16.0", Introduced(method, "iOS"));
    }

    [Fact]
    public void EnclosingTypeFloorAboveRuntimeSupport_AddsNothing()
    {
        var method = TestDecls.Method("fetch", returnType: Spec("any TestModule.Requestable<Swift.Int, Swift.String>"));
        var protocol = TestDecls.Protocol("Client", method);
        method.ParentDecl = protocol;
        protocol.AvailabilityAnnotations =
            [Ann("iOS", "16.0"), Ann("macOS", "13.0"), Ann("tvOS", "16.0"), Ann("watchOS", "9.0")];

        var raised = ParameterizedExistentialFloor.Apply(ModuleWith(protocol), Db());

        Assert.Equal(0, raised);
        Assert.Null(method.AvailabilityAnnotations);
    }

    [Fact]
    public void Property_RaisesItselfAndItsAccessors()
    {
        var property = TestDecls.Property("pending",
            Spec("any TestModule.Requestable<Swift.Int, Swift.String>"), hasSetter: true);
        var protocol = TestDecls.Protocol("Client", property);
        property.ParentDecl = protocol;

        var raised = ParameterizedExistentialFloor.Apply(ModuleWith(protocol), Db());

        Assert.Equal(1, raised);
        Assert.Equal("16.0", Introduced(property, "iOS"));
        Assert.All(property.Accessors, accessor => Assert.Equal("16.0", Introduced(accessor.Method, "iOS")));
    }

    [Fact]
    public void SetterWithItsOwnAvailability_AlsoGainsTheFloor()
    {
        var property = TestDecls.Property("pending",
            Spec("any TestModule.Requestable<Swift.Int, Swift.String>"), hasSetter: true);
        property.SetterAvailabilityAnnotations = [Ann("iOS", "15.4")];

        ParameterizedExistentialFloor.Apply(ModuleWith(property), Db());

        Assert.Contains(property.SetterAvailabilityAnnotations!,
            a => a.Platform == "iOS" && a.IntroducedVersion == "16.0" && a.IsRuntimeSupportFloor);
    }

    [Fact]
    public void SubscriptIndexedByParameterizedExistential_IsRaised()
    {
        var getter = TestDecls.Method("subscript_get", returnType: Spec("Swift.Int"));
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s10TestModule6ClientPyS2icig",
            ParentDecl = null,
            ModuleDecl = null,
            ReturnTypeSpec = Spec("Swift.Int"),
            IndexParameters = [TestDecls.Param("request", Spec("any TestModule.Requestable<Swift.Int, Swift.String>"))],
            Accessors = [new GetAccessorDecl { Method = getter }],
            IsStatic = false,
        };
        var protocol = TestDecls.Protocol("Client", subscript);
        subscript.ParentDecl = protocol;

        ParameterizedExistentialFloor.Apply(ModuleWith(protocol), Db());

        Assert.Equal("16.0", Introduced(subscript, "iOS"));
        Assert.Equal("16.0", Introduced(getter, "iOS"));
    }
}
