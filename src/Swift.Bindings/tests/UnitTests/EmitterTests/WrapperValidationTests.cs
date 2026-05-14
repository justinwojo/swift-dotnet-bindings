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
