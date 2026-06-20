// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the Path-3 concrete-class fallback in
/// <see cref="WrapperValidation.IsOptionalWithReferenceInner"/>.
///
/// The first two paths in the helper already cover (1) types with a TypeRecord
/// of the right Kind, and (2) the broad Apple ObjC fallback gated on
/// <see cref="MarshallingHelpers.IsOptionalObjCBridged"/> + an ObjC class
/// prefix. The gap exposed by RealityFoundation / RealityKit is the third
/// case: cross-module Swift classes that ship without an XML database AND
/// whose names do not start with an ObjC class prefix (e.g.
/// <c>RealityFoundation.Entity</c>). Both existing paths fall through and the
/// <c>@_cdecl</c> wrapper renders the parameter bare as
/// <c>Optional&lt;Entity&gt;</c> rather than <c>UnsafeMutableRawPointer?</c>,
/// which swiftc rejects with "type is not representable in Objective-C".
///
/// The fix routes these modules through a new
/// <c>concreteClassFallback</c> flag declared on the module entry in
/// <c>apple-frameworks.json</c>. The tests below pin the public contract of
/// the helper through <see cref="CdeclParamMapper.IsOptionalWithReferenceInner"/>
/// (the re-export the mapper exposes to callers).
/// </summary>
public class WrapperValidationTests
{
    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_NoTypeRecord_NoObjCPrefix_ReturnsTrue()
    {
        // RealityFoundation.Entity: no XML/TypeRecord, name has no ObjC prefix.
        // Both Path 1 (TypeRecord lookup) and Path 2 (ObjC-prefix fallback) miss.
        // Path 3 (concrete-class fallback for known concrete-class modules)
        // must catch it so the @_cdecl wrapper renders UnsafeMutableRawPointer?.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityFoundation.Entity"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "RealityFoundation.Entity must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_RealityKit_ReturnsTrue()
    {
        // RealityKit ships concrete Swift classes (ARKitSession, AnchorEntity, ...)
        // some of which do not match the "RE" objcPrefix. Path 3 must still fire.
        // Use a name that doesn't match the RE prefix so we exercise Path 3, not Path 2.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityKit.AnchorEntity"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "RealityKit.AnchorEntity must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_SceneKit_ReturnsTrue()
    {
        // SceneKit ships concrete Swift classes that don't always match the "SC" prefix
        // (the framework hosts both SCN-prefixed ObjC classes and concrete Swift classes).
        // Use a name with no objcPrefix match so Path 2 doesn't fire — Path 3 must.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("SceneKit.ProgramNode"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "SceneKit.ProgramNode must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_KnownValueType_ReturnsFalse()
    {
        // SCNVector3 is in apple-frameworks.json's valueTypes list for SceneKit.
        // Path 3 must respect that exclusion — value types stay value-shaped.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("SceneKit.SCNVector3"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Path 3 must defer to AppleFrameworkRegistry's known-value-type list");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_NestedType_ReturnsFalse()
    {
        // Nested type names (two dots) are conservatively excluded — they're usually
        // value-type enums/structs scoped under a class. Matches the Path 2 guard
        // and TypeProjectionFactory.IsOptionalObjCBridged behavior.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityFoundation.Entity.HierarchyOptions"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Nested types must not fall into Path 3 — they may be value-type enums");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_GenericContainer_ReturnsFalse()
    {
        // Generic specializations like RealityKit.Entity<Foo> aren't simple class
        // references — they're typically generic value types or generic specializations
        // that need their own marshalling. Path 3 must defer to the generic-container
        // handling and not over-claim them.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        var innerGeneric = new NamedTypeSpec("RealityFoundation.Entity");
        innerGeneric.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        optionalSpec.GenericParameters.Add(innerGeneric);

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Generic specializations of concrete-class-fallback modules must not fall into Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NonConcreteClassFallbackModule_NoObjCPrefix_ReturnsFalse()
    {
        // A module that is NOT in the concrete-class-fallback list and whose
        // type name doesn't match an ObjC prefix must stay rejected — Path 3
        // is opt-in per-module so we don't over-classify third-party Swift
        // modules as Apple-class shapes.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("ThirdParty.RandomThing"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Path 3 must not fire for arbitrary unrecognized modules");
    }
}

/// <summary>
/// Tests for the <c>parent_module_internal</c> guard arm (2b) of
/// <see cref="WrapperValidation.GetMemberRejectionReason"/> and the shared
/// <see cref="WrapperValidation.IsParentTypeModuleInternal"/> predicate it
/// delegates to.
///
/// A <c>public</c> member whose PARENT type is <c>@usableFromInline internal</c>
/// (<see cref="TypeDecl.IsModuleInternal"/>) slips the member-keyed
/// <c>module_internal</c> arm (the member's own flag is false), but its @_cdecl
/// wrapper body would name the parent by its module-qualified name to
/// reconstruct <c>self</c> — an internal type the separate wrapper-compilation
/// module cannot reference, so swiftc rejects the wrapper and it is stripped.
/// Arm 2b rejects the wrapper at emission instead, so the member falls back to a
/// direct CallConvSwift P/Invoke (no CS0535) and the wrapper-strip count stays 0.
///
/// Scope is sync Method / Constructor / Property / Subscript — a subscript is an
/// accessor pair like a property and shares the same clean CallConvSwift fallback,
/// so it is gated identically. The async, closure, and operator promotion sites are
/// intentionally NOT gated (no clean fallback), so those <see cref="MemberKind"/>s
/// must continue to NOT return this reason.
/// </summary>
public class ParentModuleInternalGateTests
{
    private const string WrapperLib = "TestModuleSwiftBindings";

    [Fact]
    public void GetMemberRejectionReason_PublicMethodOnInternalParentClass_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicConstructorOnInternalParentStruct_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalStruct("InternalValue", module);
        var ctor = SyncMethod("init", parent, module);
        ctor.IsConstructor = true;
        var env = Env(ctor, typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Constructor));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicPropertyOnInternalParentClass_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("get_tag", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Property));
    }

    [Fact]
    public void CanEmitMember_PublicMethodOnInternalParent_ReturnsFalse()
    {
        // The boolean shim must agree with the diagnostic twin (Finding 12).
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicMethodOnPublicParentClass_DoesNotRejectForParentInternal()
    {
        // A plain sync method on a PUBLIC parent must NOT be rejected by arm 2b —
        // it keeps its @_cdecl wrapper exactly as before. With no other gate
        // firing for this minimal shape the overall reason is null.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("PublicHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        var reason = WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method);

        Assert.NotEqual("parent_module_internal", reason);
        Assert.Null(reason);
    }

    [Fact]
    public void GetMemberRejectionReason_PublicSubscriptOnInternalParentClass_ReturnsParentModuleInternal()
    {
        // A subscript is an accessor pair like a property — its getter/setter
        // resolve to bare-silgen / Tj symbols the dylib already exports, so it has
        // the same clean CallConvSwift fallback and is gated by arm 2b. Without the
        // gate a public subscript on an internal parent emit-then-strips and leaves
        // the C# indexer bound to a stripped @_cdecl symbol.
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("subscript", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Subscript));
    }

    [Fact]
    public void GetMemberRejectionReason_OperatorOnInternalParent_NotRejectedForParentInternal()
    {
        // Operator is out of arm 2b's scope — it has no clean CallConvSwift
        // fallback, so the wrapper MUST stay (NativeAOT ILC segfaults on a direct
        // CallConvSwift operator P/Invoke). It must never surface this reason.
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalStruct("InternalValue", module);
        var env = Env(SyncMethod("member", parent, module), typeDb);

        Assert.NotEqual("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Operator));
    }

    [Fact]
    public void IsParentTypeModuleInternal_InternalParent_ReturnsTrue()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.True(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    [Fact]
    public void IsParentTypeModuleInternal_PublicParent_ReturnsFalse()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("PublicHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.False(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    [Fact]
    public void IsParentTypeModuleInternal_FreeFunctionModuleParent_ReturnsFalse()
    {
        // A free function's ParentDecl is the ModuleDecl (a BaseDecl, not a
        // TypeDecl), so the predicate must return false — the gate never fires
        // for module-level functions.
        var (module, typeDb) = XcframeworkEnv();
        var freeFunc = SyncMethod("freeFunc", module, module);
        var env = Env(freeFunc, typeDb);

        Assert.False(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    // --- minimal decl factories (local to keep the gate test self-contained) ---

    private static (ModuleDecl module, TypeDatabase typeDb) XcframeworkEnv()
    {
        // A non-empty AsyncLibraryName is what flips GenerationMode to XCFramework,
        // the prerequisite for any @_cdecl wrapper emission (gate 1).
        var typeDb = new TypeDatabase { AsyncLibraryName = WrapperLib };
        var module = new ModuleDecl
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
        return (module, typeDb);
    }

    private static MethodEnvironment Env(MethodDecl method, TypeDatabase typeDb)
        => new MethodEnvironment(method, typeDb);

    private static ClassDecl InternalClass(string name, ModuleDecl module)
    {
        var decl = PublicClass(name, module);
        decl.IsModuleInternal = true;
        return decl;
    }

    private static ClassDecl PublicClass(string name, ModuleDecl module)
        => new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module
        };

    private static StructDecl InternalStruct(string name, ModuleDecl module)
        => new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = true,
            IsModuleInternal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module
        };

    private static MethodDecl SyncMethod(string name, BaseDecl parent, ModuleDecl module)
        => new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = module
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
}
