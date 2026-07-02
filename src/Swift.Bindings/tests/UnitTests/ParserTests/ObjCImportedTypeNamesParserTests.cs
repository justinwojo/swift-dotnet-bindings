// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <c>SwiftABIParser.ObjCImportedTypeNames</c> — the authoritative rawObjCName → Swift-import-name
/// map harvested while parsing type references. Each ABI reference to an ObjC-imported nominal carries the
/// Swift-import name in <c>printedName</c> (e.g. <c>M.Greeter</c>) and the raw ObjC identity in its Clang
/// <c>usr</c> (e.g. <c>c:objc(cs)MGreeter</c>). This map is the only place the generator can recover an
/// <c>NS_SWIFT_NAME</c> / prefix-stripped rename, because Clang's JSON AST omits the <c>SwiftNameAttr</c>
/// argument; <c>ObjCBridgeRecordRekeyer</c> consumes it to key the mixed-bridge records correctly.
/// </summary>
public class ObjCImportedTypeNamesParserTests
{
    // The minimal parser's module is "TestModule" (see CreateMinimalParser), so this module's own
    // ObjC imports carry that module prefix in printedName (e.g. TestModule.Greeter) — the harvest
    // only records renames whose leading component names THIS module.

    [Fact]
    public void Captures_PureObjCClass_MapsRawNameToSwiftImportName()
    {
        // NS_SWIFT_NAME rename: raw ObjC class MGreeter imported into Swift as TestModule.Greeter.
        var parser = CreateMinimalParser();
        var node = Nominal(name: "Greeter", printedName: "TestModule.Greeter", usr: "c:objc(cs)MGreeter");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("MGreeter", out var swiftName));
        Assert.Equal("Greeter", swiftName);
    }

    [Fact]
    public void Captures_ObjCEnum_FromArrayEnumUsr()
    {
        // NS_ENUM identity is c:@EA@<Name> (or c:@E@<Name>); here raw name == Swift name (no rename).
        var parser = CreateMinimalParser();
        var node = Nominal(name: "MLevel", printedName: "TestModule.MLevel", usr: "c:@EA@MLevel");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("MLevel", out var swiftName));
        Assert.Equal("MLevel", swiftName);
    }

    [Fact]
    public void Captures_ObjCEnum_FromPlainEnumUsr()
    {
        var parser = CreateMinimalParser();
        var node = Nominal(name: "Behavior", printedName: "TestModule.Behavior", usr: "c:@E@MBehavior");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("MBehavior", out var swiftName));
        Assert.Equal("Behavior", swiftName);
    }

    [Fact]
    public void Captures_ObjCTypedef_FromFileScopedTypedefUsr()
    {
        // NS_TYPED_ENUM (typedef NSString *Foo) imports as an 8-byte newtype struct. Its Clang identity
        // is a file-scoped typedef USR c:<file>@T@<RawName>; the NS_SWIFT_NAME rename lands only in
        // printedName. This is the exact real-world FBSDKLoginAuthType shape: raw FBSDKLoginAuthType
        // imported into Swift as LoginAuthType. The harvest must decode the @T@ segment and record the
        // rename so ObjCBridgeRecordRekeyer can re-key the mixed-bridge record off the raw name.
        var parser = CreateMinimalParser();
        var node = Nominal(
            name: "LoginAuthType",
            printedName: "TestModule.LoginAuthType",
            usr: "c:FBSDKLoginAuthType.h@T@FBSDKLoginAuthType");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("FBSDKLoginAuthType", out var swiftName));
        Assert.Equal("LoginAuthType", swiftName);
    }

    [Fact]
    public void Captures_ObjCTypedef_FromBuiltinTypedefUsr()
    {
        // A typedef declared without an owning file (builtin/compiler-provided) drops the file segment:
        // c:@T@<RawName>. The @T@ decode must still recover the raw name from the file-less form.
        var parser = CreateMinimalParser();
        var node = Nominal(name: "Behavior", printedName: "TestModule.Behavior", usr: "c:@T@MBehavior");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("MBehavior", out var swiftName));
        Assert.Equal("Behavior", swiftName);
    }

    [Fact]
    public void Captures_ObjCTypedef_AnchorsOnTrailingIdentifier_WhenEarlierMarkerPresent()
    {
        // A compound/multi-segment USR can carry the "@T@" marker more than once (e.g. a file component
        // that itself contains it). The typedef name is the TRAILING C identifier, so the decode anchors
        // on the LAST "@T@": anchoring on the first would fold the intervening segment into the key
        // ("Path.h@T@LoginAuthType"), and the rekeyer would then never find the record under the true
        // raw name and the Swift member would degrade to AnyType.
        var parser = CreateMinimalParser();
        var node = Nominal(
            name: "LoginAuthType",
            printedName: "TestModule.LoginAuthType",
            usr: "c:Weird@T@Path.h@T@LoginAuthType");

        parser.CreateTypeSpec(node);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("LoginAuthType", out var swiftName));
        Assert.Equal("LoginAuthType", swiftName);
    }

    [Fact]
    public void Ignores_ObjCTypedef_WhenTrailingNameIsNotPlainIdentifier()
    {
        // After anchoring on the last "@T@", the remainder must be a pure C identifier. A residual
        // segment marker or other punctuation means a malformed/compound USR — skip rather than seed a
        // decorated key that can never be matched (mirrors the own-name identifier guard the class/enum
        // capture path applies to the Swift-side name).
        var parser = CreateMinimalParser();
        var node = Nominal(
            name: "Bad",
            printedName: "TestModule.Bad",
            usr: "c:foo.h@T@Bad@Suffix");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Ignores_DependencyModuleTypedef()
    {
        // A typedef imported from a DEPENDENCY (printedName leads with the dependency module) is filtered
        // by the same this-module guard the class/enum paths use — cross-module bridging is out of scope,
        // and recording it could mis-key this module's own raw name (e.g. UIKit's CGPathRef / launch keys).
        var parser = CreateMinimalParser();
        var node = Nominal(name: "OtherType", printedName: "Dep.OtherType", usr: "c:Dep.h@T@DepType");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Ignores_SwiftObjCClass_NotPureObjC()
    {
        // An @objc SWIFT class has usr c:@M@<module>@objc(cs)<Name> — it is Swift-owned, not an imported
        // ObjC type, and must NOT be captured (a Swift-wins record already exists for it).
        var parser = CreateMinimalParser();
        var node = Nominal(name: "SwiftClass", printedName: "TestModule.SwiftClass", usr: "c:@M@M@objc(cs)MSwiftClass");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Ignores_DependencyModuleImport()
    {
        // An ObjC type imported by a DEPENDENCY (e.g. Dep.OtherThing) carries a different module
        // prefix. Harvesting it would seed a rename under raw name DepThing that could collide with
        // THIS module's own raw name and mis-key its bridge record — cross-module bridging is out of
        // scope, so only THIS module's own imports are recorded.
        var parser = CreateMinimalParser();
        var node = Nominal(name: "OtherThing", printedName: "Dep.OtherThing", usr: "c:objc(cs)DepThing");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Ignores_NestedRename_NotFlatIdentifier()
    {
        // A nested NS_SWIFT_NAME(Parent.Mode) imports the ObjC type as TestModule.Parent.Mode. The
        // companion does not emit a nested type in Phase 1, so recording a truncated "Mode" key would
        // point at nothing (and could collide with a real flat Mode) — the remaining dot means it is
        // skipped rather than seeding a bogus key.
        var parser = CreateMinimalParser();
        var node = Nominal(name: "Mode", printedName: "TestModule.Parent.Mode", usr: "c:@E@Mode");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Ignores_NativeSwiftType()
    {
        // A native Swift nominal has an s: mangled usr — not an ObjC import.
        var parser = CreateMinimalParser();
        var node = Nominal(name: "String", printedName: "Swift.String", usr: "s:SS");

        parser.CreateTypeSpec(node);

        Assert.Empty(parser.ObjCImportedTypeNames);
    }

    [Fact]
    public void Captures_NestedGenericArgument()
    {
        // The mapping must reach ObjC types in generic-argument position, e.g. Optional<TestModule.Greeter>,
        // since a Swift member can reference the renamed type only inside a container.
        var parser = CreateMinimalParser();
        var inner = Nominal(name: "Greeter", printedName: "TestModule.Greeter", usr: "c:objc(cs)MGreeter");
        var optional = Nominal(name: "Optional", printedName: "TestModule.Greeter?", usr: "s:Sq", children: new[] { inner });

        parser.CreateTypeSpec(optional);

        Assert.True(parser.ObjCImportedTypeNames.TryGetValue("MGreeter", out var swiftName));
        Assert.Equal("Greeter", swiftName);
    }

    #region Helpers

    private static Node Nominal(string name, string printedName, string usr, IEnumerable<Node>? children = null)
    {
        var node = NodeBase(kind: "TypeNominal", name: name, printedName: printedName, children ?? Array.Empty<Node>());
        node.usr = usr;
        return node;
    }

    private static Node NodeBase(string kind, string name, string printedName, IEnumerable<Node> children)
        => new()
        {
            Kind = kind,
            DeclKind = "",
            Name = name,
            MangledName = "",
            PrintedName = printedName,
            ModuleName = "",
            DeclAttributes = Array.Empty<string>(),
            @static = null,
            IsInternal = null,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = null,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = children,
            Conformances = Enumerable.Empty<Node>(),
            Accessors = Enumerable.Empty<Node>()
        };

    private static SwiftABIParser CreateMinimalParser()
    {
        var abiJson = JsonConvert.SerializeObject(new
        {
            ABIRoot = new
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new object[]
                {
                    new
                    {
                        Kind = "TypeDecl",
                        DeclKind = "Module",
                        Name = "TestModule",
                        MangledName = "",
                        PrintedName = "TestModule",
                        ModuleName = "TestModule",
                        DeclAttributes = new string[0],
                        @static = false,
                        IsInternal = false,
                        GenericSig = "",
                        sugared_genericSig = "",
                        throwing = false,
                        AccessorKind = "",
                        EnumRawTypeName = "",
                        paramValueOwnership = "",
                        hasDefaultArg = false,
                        Children = new object[0],
                        Conformances = new object[0],
                        Accessors = new object[0]
                    }
                }
            }
        });

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, abiJson);
        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);
        File.Delete(filePath);
        return parser;
    }

    private static BindingsGeneration.Demangling.DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(BindingsGeneration.Demangling.DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(BindingsGeneration.Demangling.IReduction[]), typeof(HashSet<string>) },
            modifiers: null);
        if (ctor == null)
            throw new InvalidOperationException("Could not find DemanglingResults constructor");
        return (BindingsGeneration.Demangling.DemanglingResults)ctor.Invoke(
            new object[] { Array.Empty<BindingsGeneration.Demangling.IReduction>(), new HashSet<string>() });
    }

    #endregion
}
