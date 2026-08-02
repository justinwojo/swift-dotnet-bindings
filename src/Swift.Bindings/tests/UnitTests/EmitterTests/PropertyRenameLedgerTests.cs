// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The write side of the module database's property rename ledger.
///
/// <para>These assert what <see cref="PropertyRenameLedger.Populate"/> actually records off
/// emission stamps — distinct from the XML round-trip tests, which seed a
/// <see cref="TypeRecord"/> by hand and only prove serialization is lossless. The distinction
/// matters because the ledger's contract gives empty and null different meanings: empty asserts
/// "this type was processed and renamed nothing", which a downstream conformer trusts.</para>
/// </summary>
public class PropertyRenameLedgerTests
{
    [Fact]
    public void Populate_CaseOnlyRename_IsRecordedUnderThatScheme()
    {
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Settings", "url", "URL");
        typeDecl.Properties[1].MarkCaseDisambiguated("Url2");
        MarkEmitted(typeDecl.Properties[0], "Url");
        MarkEmitted(typeDecl.Properties[1], "Url2");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        var entry = Assert.Single(GetRenames(typeDatabase, swiftName));
        Assert.Equal(RenamedMemberKind.Property, entry.Kind);
        Assert.Equal("URL", entry.SwiftName);
        Assert.Equal("Url2", entry.CSharpName);
        Assert.Equal(nameof(NameCollisionScheme.CaseOnlyMemberCollision), entry.Scheme);
        Assert.False(entry.IsStatic);
    }

    [Fact]
    public void Populate_ValueSuffixRename_IsRecordedUnderTheOtherScheme()
    {
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Camera", "position");
        MarkEmitted(typeDecl.Properties[0], "PositionValue");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        var entry = Assert.Single(GetRenames(typeDatabase, swiftName));
        Assert.Equal("PositionValue", entry.CSharpName);
        Assert.Equal(nameof(NameCollisionScheme.PropertyValueSuffix), entry.Scheme);
    }

    [Fact]
    public void Populate_StaticnessIsPartOfTheRecordedIdentity()
    {
        // Swift permits a static and an instance member sharing one identifier, so the Swift name
        // alone does not name a member.
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Camera", "position");
        typeDecl.Properties[0].IsStatic = true;
        MarkEmitted(typeDecl.Properties[0], "PositionValue");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.True(Assert.Single(GetRenames(typeDatabase, swiftName)).IsStatic);
    }

    [Fact]
    public void Populate_PropertyEmittedUnderItsNaturalName_IsNotRecorded()
    {
        // Only a DEPARTURE from the predictable projection is worth persisting; recording every
        // property would make the ledger a second, staler copy of the type's member list.
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Settings", "host");
        MarkEmitted(typeDecl.Properties[0], "Host");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.Empty(GetRenames(typeDatabase, swiftName));
    }

    [Fact]
    public void Populate_PropertyThatNeverEmitted_IsNotRecorded()
    {
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Settings", "url", "URL");
        typeDecl.Properties[1].MarkCaseDisambiguated("Url2");
        // Only the first was emitted; the second was gate-skipped, so the pre-pass decision about
        // it is not something a consumer can act on.
        MarkEmitted(typeDecl.Properties[0], "Url");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.Empty(GetRenames(typeDatabase, swiftName));
    }

    [Fact]
    public void Populate_ConcretePropertyStampedButNeverEmitted_IsNotRecorded()
    {
        // The concrete property handler settles the C# name early and can still bail out of a
        // later accessor preflight (and a recovery rollback restores `WasEmitted` without
        // clearing the name stamp), so on that path the stamp alone does not prove emission.
        var (moduleDecl, typeDatabase, typeDecl, swiftName) = BuildType("Settings", "url", "URL");
        typeDecl.Properties[1].MarkCaseDisambiguated("Url2");
        typeDecl.Properties[1].MarkEmittedCSharpName("Url2");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.Empty(GetRenames(typeDatabase, swiftName));
    }

    [Fact]
    public void Populate_ProtocolRequirementStampedWithoutTheEmissionFlag_IsStillRecorded()
    {
        // A protocol requirement never sets `WasEmitted` — that flag is a concrete-type ancestry
        // signal — so requiring it here would write an empty list for every protocol, which this
        // ledger's contract reads as "processed, renamed nothing".
        var typeDatabase = new TypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var protocolName = SwiftTypeName.FromModuleQualifiedName("TestModule.Endpoint");
        var protocolDecl = CreateProtocolDecl("Endpoint", protocolName, moduleDecl);
        protocolDecl.Properties.Add(CreateProperty("URL"));
        protocolDecl.Properties[0].MarkCaseDisambiguated("Url2");
        protocolDecl.Properties[0].MarkEmittedCSharpName("Url2");
        moduleDecl.Protocols.Add(protocolDecl);
        RegisterType(typeDatabase, protocolName, "IEndpoint");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.False(protocolDecl.Properties[0].WasEmitted);
        Assert.Equal("Url2", Assert.Single(GetRenames(typeDatabase, protocolName)).CSharpName);
    }

    [Fact]
    public void Populate_TypeThatRenamedNothing_GetsAnEmptyListNotNull()
    {
        var (moduleDecl, typeDatabase, _, swiftName) = BuildType("Settings", "host");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var record));
        Assert.NotNull(record!.RenamedMembers);
        Assert.Empty(record.RenamedMembers!);
    }

    [Fact]
    public void Populate_NestedType_IsVisitedToo()
    {
        var (moduleDecl, typeDatabase, typeDecl, _) = BuildType("Settings", "host");
        var nestedName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings.Endpoint");
        var nested = CreateStructDecl("Endpoint", nestedName, moduleDecl);
        nested.ParentDecl = typeDecl;
        nested.Properties.Add(CreateProperty("url"));
        MarkEmitted(nested.Properties[0], "UrlValue");
        typeDecl.Types.Add(nested);
        RegisterType(typeDatabase, nestedName, "Settings.Endpoint");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        Assert.Equal("UrlValue", Assert.Single(GetRenames(typeDatabase, nestedName)).CSharpName);
    }

    [Fact]
    public void Populate_ProtocolRequirement_IsRecordedLikeAnyOtherType()
    {
        // A conforming type in a downstream module reads exactly this entry to bind the
        // requirement to the name the protocol was emitted under. If protocols wrote an empty
        // list, that reader would be told the protocol renamed nothing.
        var typeDatabase = new TypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var protocolName = SwiftTypeName.FromModuleQualifiedName("TestModule.Endpoint");
        var protocolDecl = CreateProtocolDecl("Endpoint", protocolName, moduleDecl);
        protocolDecl.Properties.Add(CreateProperty("url"));
        protocolDecl.Properties.Add(CreateProperty("URL"));
        protocolDecl.Properties[1].MarkCaseDisambiguated("Url2");
        MarkEmitted(protocolDecl.Properties[0], "Url");
        MarkEmitted(protocolDecl.Properties[1], "Url2");
        moduleDecl.Protocols.Add(protocolDecl);
        RegisterType(typeDatabase, protocolName, "IEndpoint");

        PropertyRenameLedger.Populate(moduleDecl, typeDatabase);

        var entry = Assert.Single(GetRenames(typeDatabase, protocolName));
        Assert.Equal("URL", entry.SwiftName);
        Assert.Equal("Url2", entry.CSharpName);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static IReadOnlyList<RenamedMember> GetRenames(TypeDatabase typeDatabase, SwiftTypeName swiftName)
    {
        Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var record));
        return record!.RenamedMembers ?? Array.Empty<RenamedMember>();
    }

    private static void MarkEmitted(PropertyDecl property, string csharpName)
    {
        property.MarkEmitted();
        property.MarkEmittedCSharpName(csharpName);
    }

    private static (ModuleDecl, TypeDatabase, StructDecl, SwiftTypeName) BuildType(
        string typeName, params string[] propertyNames)
    {
        var typeDatabase = new TypeDatabase();
        var moduleDecl = CreateModuleDecl();

        var swiftName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}");
        var typeDecl = CreateStructDecl(typeName, swiftName, moduleDecl);
        foreach (var propertyName in propertyNames)
            typeDecl.Properties.Add(CreateProperty(propertyName));

        moduleDecl.Types.Add(typeDecl);
        RegisterType(typeDatabase, swiftName, typeName);

        return (moduleDecl, typeDatabase, typeDecl, swiftName);
    }

    private static void RegisterType(TypeDatabase typeDatabase, SwiftTypeName swiftName, string csharpName)
    {
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", csharpName),
            SwiftTypeName = swiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        };

        // A module database is added exactly once. A second type in the same module — a nested
        // sibling, say — is registered into the database already in place.
        if (typeDatabase.IsModuleLoaded("TestModule"))
        {
            typeDatabase.RegisterCrossModuleType(swiftName, record);
            return;
        }

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(swiftName, record);
        typeDatabase.AddModuleDatabase(module);
    }

    private static PropertyDecl CreateProperty(string name) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
        IsStatic = false,
        HasStorage = true,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

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

    private static StructDecl CreateStructDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = swiftName,
        MangledName = "$sN",
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
        MetadataAccessor = "$sMa",
    };

    private static ProtocolDecl CreateProtocolDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = swiftName,
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };
}
