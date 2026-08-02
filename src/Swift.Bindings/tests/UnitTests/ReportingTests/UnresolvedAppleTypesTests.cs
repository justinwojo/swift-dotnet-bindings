// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the skip-detail enrichment that names the dependency-module Apple type a member could
/// not be bound against, plus the signature walk it is built on.
/// </summary>
public class UnresolvedAppleTypesTests
{
    private static TypeDatabase CreateDatabase()
    {
        var database = new TypeDatabase();
        database.AddModuleDatabase(new ModuleTypeDatabase("MyKit", "/fake/path"));
        return database;
    }

    private static void Register(TypeDatabase database, string moduleQualifiedName)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        database.AddOutOfModuleTypes(new[]
        {
            (swiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(
                    "Ns", swiftTypeName.Name),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    [Fact]
    public void Find_UnregisteredAppleType_IsNamed()
    {
        var database = CreateDatabase();

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[] { new NamedTypeSpec("CoreGraphics.CGImage") }, database, "MyKit");

        Assert.Equal(new[] { "CoreGraphics.CGImage" }, unresolved);
    }

    [Fact]
    public void Find_RegisteredAppleType_IsNotNamed()
    {
        var database = CreateDatabase();
        Register(database, "CoreGraphics.CGImage");

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[] { new NamedTypeSpec("CoreGraphics.CGImage") }, database, "MyKit");

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Find_AutoBridgedAppleType_IsNotNamedEvenWithoutADatabaseEntry()
    {
        // An auto-bridged ObjC module type resolves through the bridging strategy rather than a
        // database entry, so it never degrades to AnyType and is never the cause of the skip.
        // Naming it would send the reader chasing a registration that would change nothing.
        var database = CreateDatabase();

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[] { new NamedTypeSpec("UIKit.UIView") }, database, "MyKit");

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Find_TypeFromTheModuleBeingBound_IsNotNamed()
    {
        // A gap in the module under generation is the generator's own business, not a missing
        // Apple registration — naming it here would point the reader at the wrong fix.
        var database = CreateDatabase();

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[] { new NamedTypeSpec("MyKit.Widget") }, database, "MyKit");

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Find_NonAppleThirdPartyModule_IsNotNamed()
    {
        var database = CreateDatabase();

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[] { new NamedTypeSpec("SomeVendorSDK.Session") }, database, "MyKit");

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Find_ReachesTypesNestedInsideGenericArguments()
    {
        var database = CreateDatabase();
        var array = new NamedTypeSpec("Swift.Array");
        array.GenericParameters.Add(new NamedTypeSpec("CoreGraphics.CGImage"));

        var unresolved = UnresolvedAppleTypes.Find(new TypeSpec?[] { array }, database, "MyKit");

        Assert.Contains("CoreGraphics.CGImage", unresolved);
    }

    [Fact]
    public void Find_DeDupesAndOrdersDeterministically()
    {
        var database = CreateDatabase();

        var unresolved = UnresolvedAppleTypes.Find(
            new TypeSpec?[]
            {
                new NamedTypeSpec("CoreMedia.CMSampleBuffer"),
                new NamedTypeSpec("CoreGraphics.CGImage"),
                new NamedTypeSpec("CoreMedia.CMSampleBuffer"),
            },
            database, "MyKit");

        Assert.Equal(new[] { "CoreGraphics.CGImage", "CoreMedia.CMSampleBuffer" }, unresolved);
    }

    [Fact]
    public void DescribeSuffix_NothingUnresolved_IsEmptySoTheDetailIsUnchanged()
    {
        var database = CreateDatabase();
        Register(database, "CoreGraphics.CGImage");

        var suffix = UnresolvedAppleTypes.DescribeSuffix(
            new TypeSpec?[] { new NamedTypeSpec("CoreGraphics.CGImage") }, database, "MyKit");

        Assert.Equal(string.Empty, suffix);
    }

    [Fact]
    public void DescribeSuffix_NamesEachUnresolvedType()
    {
        var database = CreateDatabase();

        var suffix = UnresolvedAppleTypes.DescribeSuffix(
            new TypeSpec?[]
            {
                new NamedTypeSpec("CoreGraphics.CGImage"),
                new NamedTypeSpec("CoreMedia.CMSampleBuffer"),
            },
            database, "MyKit");

        Assert.Contains("CoreGraphics.CGImage", suffix);
        Assert.Contains("CoreMedia.CMSampleBuffer", suffix);
    }

    [Fact]
    public void DescribeSuffix_OverAMethod_CoversTheReturnSlotAndEveryParameter()
    {
        var database = CreateDatabase();
        var methodDecl = new MethodDecl
        {
            Name = "render",
            MangledName = "$srender",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Index 0 is the return slot.
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("CoreGraphics.CGImage"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "into", PrivateName = "into",
                    SwiftTypeSpec = new NamedTypeSpec("CoreMedia.CMSampleBuffer"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        };

        var suffix = UnresolvedAppleTypes.DescribeSuffix(methodDecl, database, "MyKit");

        Assert.Contains("CoreGraphics.CGImage", suffix);
        Assert.Contains("CoreMedia.CMSampleBuffer", suffix);
    }

    [Fact]
    public void CollectNominalTypeNames_WalksClosureArgumentsAndReturn()
    {
        var names = new HashSet<string>();
        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("UIKit.UIView"), new NamedTypeSpec("Foundation.NSDate"));

        TypeSpecHelpers.CollectNominalTypeNames(closure, names);

        Assert.Contains("UIKit.UIView", names);
        Assert.Contains("Foundation.NSDate", names);
    }

    [Fact]
    public void CollectNominalTypeNames_WalksTupleElements()
    {
        var names = new HashSet<string>();
        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("UIKit.UIView"),
            new NamedTypeSpec("Foundation.NSDate"),
        });

        TypeSpecHelpers.CollectNominalTypeNames(tuple, names);

        Assert.Contains("UIKit.UIView", names);
        Assert.Contains("Foundation.NSDate", names);
    }

    [Fact]
    public void CollectNominalTypeNames_ComposesNestedTypeNamesModuleQualified()
    {
        // Must match how a type key is built elsewhere (Module.Outer.Inner), otherwise the
        // collected name never joins against the type database or the emitted-type set.
        var names = new HashSet<string>();
        var nested = new NamedTypeSpec("AVFoundation.AVCaptureSession")
        {
            InnerType = new NamedTypeSpec("Preset")
        };

        TypeSpecHelpers.CollectNominalTypeNames(nested, names);

        Assert.Contains("AVFoundation.AVCaptureSession.Preset", names);
    }

    [Fact]
    public void CollectNominalTypeNames_SkipsUnqualifiedNames()
    {
        var names = new HashSet<string>();

        TypeSpecHelpers.CollectNominalTypeNames(new NamedTypeSpec("T"), names);

        Assert.Empty(names);
    }
}
