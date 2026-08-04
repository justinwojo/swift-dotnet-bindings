// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="ObjCSwiftImportNameRewriter"/> — the pre-emission pass that declares an ObjC
/// type under the name Swift imports it as, while keeping its ObjC runtime identity on the raw name.
/// The invariant every test here defends is declaration/reference agreement: a renamed declaration
/// with a reference left on the raw spelling is CS0246, and a rename that lands on an occupied name
/// is CS0101.
/// </summary>
public class ObjCSwiftImportNameRewriterTests
{
    static (ObjCModule Module, IReadOnlyList<ObjCSwiftImportRename> Renames) Rewrite(
        ObjCModule module, Dictionary<string, string>? map) =>
        ObjCSwiftImportNameRewriter.Rewrite(module, map, Logger);

    [Fact]
    public void Rewrite_RenamesDeclaration_AndKeepsRawNameForRuntimeRegistration()
    {
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAccessToken")
            .Build();

        var (rewritten, renames) = Rewrite(module, new() { ["FBSDKAccessToken"] = "AccessToken" });

        var cls = Assert.Single(rewritten.Classes);
        Assert.Equal("AccessToken", cls.Name);
        Assert.Equal("FBSDKAccessToken", cls.RawObjCName);

        var rename = Assert.Single(renames);
        Assert.Equal("FBSDKAccessToken", rename.RawObjCName);
        Assert.Equal("AccessToken", rename.SwiftImportName);
        Assert.Equal("class", rename.Kind);
    }

    [Fact]
    public void Rewrite_RenamesProtocolsAndEnums_Too()
    {
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithProtocol("FBSDKSharing")
            .WithEnum("FBSDKLoginBehavior", e => e.Case("FBSDKLoginBehaviorBrowser"))
            .Build();

        var (rewritten, renames) = Rewrite(module, new()
        {
            ["FBSDKSharing"] = "Sharing",
            ["FBSDKLoginBehavior"] = "LoginBehavior",
        });

        Assert.Equal("Sharing", Assert.Single(rewritten.Protocols).Name);
        Assert.Equal("FBSDKSharing", Assert.Single(rewritten.Protocols).RawObjCName);
        Assert.Equal("LoginBehavior", Assert.Single(rewritten.Enums).Name);
        Assert.Equal("FBSDKLoginBehavior", Assert.Single(rewritten.Enums).RawObjCName);
        Assert.Equal(2, renames.Count);
    }

    [Fact]
    public void Rewrite_UpdatesEverySiteThatNamesTheRenamedType()
    {
        // Each of these is a distinct reference channel; missing any one leaves a member typed by a
        // name no longer declared.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAccessToken")
            .WithProtocol("FBSDKSharing")
            .WithClass("FBSDKProfile", superclass: "FBSDKAccessToken", configure: c =>
            {
                c.Protocol("FBSDKSharing");
                c.Method("tokenFor:", "FBSDKAccessToken", parameters: [("token", "FBSDKAccessToken")]);
                c.Property("current", "FBSDKAccessToken", isPointer: true);
            })
            .WithCategory("Extras", "FBSDKAccessToken")
            .WithFunction("FBSDKCurrentToken", "FBSDKAccessToken")
            .Build();

        var (rewritten, _) = Rewrite(module, new()
        {
            ["FBSDKAccessToken"] = "AccessToken",
            ["FBSDKSharing"] = "Sharing",
        });

        var profile = rewritten.Classes.Single(c => c.Name == "FBSDKProfile");
        Assert.Equal("AccessToken", profile.SuperclassName);
        Assert.Equal("Sharing", Assert.Single(profile.ProtocolNames));
        Assert.Equal("AccessToken", Assert.Single(profile.Methods).ReturnType.Name);
        Assert.Equal("AccessToken", Assert.Single(profile.Methods).Parameters[0].Type.Name);
        Assert.Equal("AccessToken", Assert.Single(profile.Properties).Type.Name);
        Assert.Equal("AccessToken", Assert.Single(rewritten.Categories).ClassName);
        Assert.Equal("AccessToken", Assert.Single(rewritten.Functions).ReturnType.Name);
    }

    [Fact]
    public void Rewrite_DeclinesRename_WhenTheSwiftNameIsAlreadyTakenInTheModule()
    {
        // Two C# types of one name is a hard compile failure and the raw name is always a correct
        // fallback, so a collision is declined rather than disambiguated.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAccessToken")
            .WithClass("AccessToken")
            .Build();

        var (rewritten, renames) = Rewrite(module, new() { ["FBSDKAccessToken"] = "AccessToken" });

        Assert.Empty(renames);
        Assert.Equal(["FBSDKAccessToken", "AccessToken"], rewritten.Classes.Select(c => c.Name));
        Assert.All(rewritten.Classes, c => Assert.Null(c.RawObjCName));
    }

    [Fact]
    public void Rewrite_DeclinesRename_WhenTheSwiftNameCollidesWithANonRenamableKind()
    {
        // Structs and typedefs are never renamed by this pass but they DO occupy the module's C#
        // type namespace, so a rename must not land on one.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKRange")
            .WithStruct("Range", ("location", "NSInteger"))
            .Build();

        var (rewritten, renames) = Rewrite(module, new() { ["FBSDKRange"] = "Range" });

        Assert.Empty(renames);
        Assert.Equal("FBSDKRange", Assert.Single(rewritten.Classes).Name);
    }

    [Fact]
    public void Rewrite_IgnoresMapEntriesForTypesThisModuleDoesNotDeclare()
    {
        // The ABI map spans every ObjC type the Swift half references, including ones imported from
        // sibling frameworks. Renaming those here would rewrite references to a declaration that
        // lives in another binding.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAccessToken", configure: c => c.Property("session", "NSURLSession", isPointer: true))
            .Build();

        var (rewritten, renames) = Rewrite(module, new() { ["NSURLSession"] = "URLSession" });

        Assert.Empty(renames);
        Assert.Equal("NSURLSession", Assert.Single(Assert.Single(rewritten.Classes).Properties).Type.Name);
    }

    [Fact]
    public void Rewrite_IsIdentity_WhenTheMapIsEmptyOrNull()
    {
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit").WithClass("FBSDKAccessToken").Build();

        foreach (var map in new Dictionary<string, string>?[] { null, new() })
        {
            var (rewritten, renames) = Rewrite(module, map);
            Assert.Same(module, rewritten);
            Assert.Empty(renames);
        }
    }

    [Fact]
    public void Rewrite_IsIdentity_WhenTheSwiftNameEqualsTheRawName()
    {
        // A type Swift imports unchanged must not be stamped with a RawObjCName — that field is the
        // signal "this declaration was renamed", and a spurious one would put a redundant
        // `Name = "..."` on every registration attribute.
        var module = ObjCModuleBuilder.Create("MapLib").WithClass("MLNMapView").Build();

        var (rewritten, renames) = Rewrite(module, new() { ["MLNMapView"] = "MLNMapView" });

        Assert.Same(module, rewritten);
        Assert.Empty(renames);
        Assert.Null(Assert.Single(rewritten.Classes).RawObjCName);
    }

    [Fact]
    public void AcceptRenames_DeclinesTheNamesTheEmittersSynthesizePerModule()
    {
        // `{Module}Constants` and `{Module}Functions` are emitted from the module itself, not from a
        // declaration, so they never appear in the declaration lists a collision check walks — but a
        // rename landing on either still produces two C# types of one name.
        var module = ObjCModuleBuilder.Create("Kit")
            .WithClass("XYZKitConstants")
            .WithClass("XYZKitFunctions")
            .Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(module, new Dictionary<string, string>
        {
            ["XYZKitConstants"] = "KitConstants",
            ["XYZKitFunctions"] = "KitFunctions",
        }, reservedName: null, Logger);

        Assert.Empty(accepted);
    }

    [Fact]
    public void AcceptRenames_DeclinesARenameOntoTheModuleNamespace()
    {
        // A type whose name equals its own namespace makes every qualified reference ambiguous
        // (CS0426). The pipeline detects that collision on the parsed names and suffixes the
        // namespace; a rename applied afterwards can recreate it, so the namespace is reserved.
        var module = ObjCModuleBuilder.Create("Kit").WithClass("XYZMapper").Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(
            module, new Dictionary<string, string> { ["XYZMapper"] = "Mapper" }, "Mapper", Logger);

        Assert.Empty(accepted);
    }

    [Fact]
    public void AcceptRenames_DeclinesARenameThatOnlyCollidesAfterTheAcronymConvention()
    {
        // The .NET acronym convention folds NSURL* and NSUrl* onto one emitted name, so two names
        // that differ as declared can still be one C# type. Comparing raw spellings alone clears the
        // rename and the companion then declares `NSUrlBox` twice.
        var module = ObjCModuleBuilder.Create("Kit")
            .WithClass("XYZBox")
            .WithClass("NSUrlBox")
            .Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(
            module, new Dictionary<string, string> { ["XYZBox"] = "NSURLBox" }, reservedName: null, Logger);

        Assert.Empty(accepted);
    }

    [Fact]
    public void AcceptRenames_AgreesWithWhatRewriteApplies()
    {
        // The vetted map is what BOTH the companion rewrite and the Swift-side bridge re-key consume.
        // If the two ever disagreed, one half of a mixed binding would rename and the other would not.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAccessToken")
            .WithClass("FBSDKRange")
            .WithStruct("Range", ("location", "NSInteger"))
            .Build();
        var map = new Dictionary<string, string>
        {
            ["FBSDKAccessToken"] = "AccessToken",
            ["FBSDKRange"] = "Range",
        };

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(module, map, reservedName: null, Logger);
        var (_, renames) = Rewrite(module, map);

        Assert.Equal(
            renames.Select(r => (r.RawObjCName, r.SwiftImportName)).OrderBy(r => r.RawObjCName),
            accepted.Select(kv => (kv.Key, kv.Value)).OrderBy(r => r.Key));
        Assert.Equal(["FBSDKAccessToken"], accepted.Keys);
    }

    [Fact]
    public void Rewrite_IsDeterministic_WhenTwoRawNamesTargetTheSameSwiftName()
    {
        // Enumeration order of the input map is not contractual. Ordering by raw name makes which
        // rename wins — and which is declined — a property of the source, not of dictionary internals.
        var module = ObjCModuleBuilder.Create("FBSDKCoreKit")
            .WithClass("FBSDKAToken")
            .WithClass("FBSDKBToken")
            .Build();

        var forward = Rewrite(module, new() { ["FBSDKAToken"] = "Token", ["FBSDKBToken"] = "Token" });
        var reverse = Rewrite(module, new() { ["FBSDKBToken"] = "Token", ["FBSDKAToken"] = "Token" });

        Assert.Equal("FBSDKAToken", Assert.Single(forward.Renames).RawObjCName);
        Assert.Equal("FBSDKAToken", Assert.Single(reverse.Renames).RawObjCName);
        Assert.Equal(["Token", "FBSDKBToken"], forward.Module.Classes.Select(c => c.Name));
        Assert.Equal(["Token", "FBSDKBToken"], reverse.Module.Classes.Select(c => c.Name));
    }

    [Fact]
    public void AcceptRenames_DeclinesARenameOntoAProtocolsForwardInterfaceName()
    {
        // Every protocol also emits a forward-declared `I{Name}` interface, which no declaration in
        // the module spells out. A class renamed onto that spelling declares it a second time.
        var module = ObjCModuleBuilder.Create("Kit")
            .WithProtocol("Drawable")
            .WithClass("XYZThing")
            .Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(
            module, new Dictionary<string, string> { ["XYZThing"] = "IDrawable" }, reservedName: null, Logger);

        Assert.Empty(accepted);
    }

    [Fact]
    public void AcceptRenames_DeclinesARenameOntoTheClassProtocolClashSpelling()
    {
        // A name declared as both a class and a protocol pushes the protocol's interface to
        // `{Name}Protocol`. That spelling belongs to the protocol even though nothing declares it.
        var module = ObjCModuleBuilder.Create("Kit")
            .WithClass("Foo")
            .WithProtocol("Foo")
            .WithClass("XYZThing")
            .Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(
            module, new Dictionary<string, string> { ["XYZThing"] = "FooProtocol" }, reservedName: null, Logger);

        Assert.Empty(accepted);
    }

    [Fact]
    public void AcceptRenames_KeepsTheClaimOfANamesakeWhenARenamedDeclarationReleasesIt()
    {
        // NSURLThing and NSUrlThing are two declarations projecting onto ONE C# name. Renaming the
        // second away must not release that name — the first still emits it, so a third rename
        // landing there would declare it twice.
        var module = ObjCModuleBuilder.Create("Kit")
            .WithClass("NSURLThing")
            .WithClass("NSUrlThing")
            .WithClass("ZZZThing")
            .Build();

        var accepted = ObjCSwiftImportNameRewriter.AcceptRenames(
            module,
            new Dictionary<string, string> { ["NSUrlThing"] = "Other", ["ZZZThing"] = "NSUrlThing" },
            reservedName: null,
            Logger);

        Assert.Equal(["NSUrlThing"], accepted.Keys);
        Assert.Equal("Other", accepted["NSUrlThing"]);
    }
}
