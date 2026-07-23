// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that <c>ModuleProcessor.RegisterClassType</c> re-anchors a locally-declared pure-ObjC
/// superclass whose ABI <c>superclassNames</c> segment mis-attributes it to an unrelated imported
/// module (the CombineCocoa <c>ObjcDelegateProxy</c> / <c>Runtime.ObjcDelegateProxy</c> shape), so
/// the reference persisted into the module database resolves against this module's own ObjC-bridge
/// record instead of a nonexistent <c>{wrongModule}.{Name}</c>.
/// </summary>
public class ObjCSuperclassReanchorTests
{
    private const string CombineCocoaModule = "CombineCocoa";

    [Fact]
    public void RegisterClassType_LocalObjCHelper_MisattributedModule_ReanchorsToCurrentModule()
    {
        // `DelegateProxy : Runtime.ObjcDelegateProxy` where ObjcDelegateProxy is declared in
        // CombineCocoa's own bridging header (bare c:objc(cs) USR) and appears in the module's
        // ObjC-bridge class set. The persisted superclass must be re-anchored to CombineCocoa.
        var cls = CreateClassDecl("DelegateProxy", CombineCocoaModule,
            superclassUsr: "c:objc(cs)ObjcDelegateProxy",
            superclassNames: new[] { "Runtime.ObjcDelegateProxy", "ObjectiveC.NSObject" });

        var record = RegisterAndGet(cls, localObjCClassNames: new[] { "ObjcDelegateProxy" });

        Assert.NotNull(record.SuperclassTypeName);
        Assert.Equal("CombineCocoa.ObjcDelegateProxy", record.SuperclassTypeName!.ModuleQualifiedName);
    }

    [Fact]
    public async System.Threading.Tasks.Task RegisterClassType_ReanchoredSuperclass_PersistsCorrectedNameToXml()
    {
        // The corrected name must survive serialization into the module database XML that downstream
        // consumers load — a round-trip through ModuleDatabaseEmitter reads back the anchored name.
        var cls = CreateClassDecl("DelegateProxy", CombineCocoaModule,
            superclassUsr: "c:objc(cs)ObjcDelegateProxy",
            superclassNames: new[] { "Runtime.ObjcDelegateProxy", "ObjectiveC.NSObject" });

        var moduleDb = Finalize(cls, localObjCClassNames: new[] { "ObjcDelegateProxy" });

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"objc_reanchor_{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = ModuleDatabaseEmitter.Emit(moduleDb, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var reloaded = new TypeDatabase();
            await reloaded.LoadModuleDatabaseFromFile(path!);

            var swiftName = SwiftTypeName.FromModuleQualifiedName("CombineCocoa.DelegateProxy");
            Assert.True(reloaded.TryGetTypeRecord(swiftName, out var loaded));
            Assert.Equal("CombineCocoa.ObjcDelegateProxy",
                loaded!.SuperclassTypeName!.ModuleQualifiedName);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void RegisterClassType_NoLocalObjCRecords_LeavesSuperclassUnchanged()
    {
        // Swift-only module (no ObjC-bridge records): the ABI superclass name is persisted verbatim,
        // so re-anchoring never fires when there is nothing to anchor against.
        var cls = CreateClassDecl("DelegateProxy", CombineCocoaModule,
            superclassUsr: "c:objc(cs)ObjcDelegateProxy",
            superclassNames: new[] { "Runtime.ObjcDelegateProxy", "ObjectiveC.NSObject" });

        var record = RegisterAndGet(cls, localObjCClassNames: null);

        Assert.Equal("Runtime.ObjcDelegateProxy", record.SuperclassTypeName!.ModuleQualifiedName);
    }

    [Fact]
    public void RegisterClassType_ExternalObjCBase_NotInLocalSet_LeavesSuperclassUnchanged()
    {
        // A genuinely external pure-ObjC base (bound in another module, not in this module's bridge
        // set) must be left as the ABI reports it — no false re-anchoring of a real cross-module base.
        var cls = CreateClassDecl("FBLoginButton", "FBSDKLoginKit",
            superclassUsr: "c:objc(cs)FBSDKButton",
            superclassNames: new[] { "FBSDKCoreKit.FBButton" });

        var record = RegisterAndGet(cls, localObjCClassNames: new[] { "FBSDKSomethingElse" });

        Assert.Equal("FBSDKCoreKit.FBButton", record.SuperclassTypeName!.ModuleQualifiedName);
    }

    private static TypeRecord RegisterAndGet(ClassDecl cls, string[]? localObjCClassNames)
    {
        var moduleDb = Finalize(cls, localObjCClassNames);
        Assert.True(moduleDb.TryGetTypeRecord(cls.SwiftTypeName, out var record));
        return record!;
    }

    private static ModuleTypeDatabase Finalize(ClassDecl cls, string[]? localObjCClassNames)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec(cls.SwiftTypeName.ModuleQualifiedName)] = cls,
        };

        var set = localObjCClassNames == null
            ? null
            : (IReadOnlySet<string>)localObjCClassNames.ToHashSet(System.StringComparer.Ordinal);

        var processor = new ModuleProcessor(
            cls.SwiftTypeName.Module,
            "/tmp/dummy.dylib",
            cls.SwiftTypeName.Module,
            typeDecls,
            new TypeDatabase(),
            NullLogger.Instance,
            namespacePatternResolver: null,
            localObjCClassNames: set);

        return processor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
    }

    private static ClassDecl CreateClassDecl(
        string name,
        string moduleName,
        string? superclassUsr = null,
        string[]? superclassNames = null)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            SuperclassUsr = superclassUsr,
            SuperclassNames = superclassNames?.ToList() ?? new List<string>(),
        };
    }
}
