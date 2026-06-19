// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The flag-matrix invariant (Finding 30 #5 / Finding 31): across the full grid of the four "fiction"
/// flags a protocol member can carry — <c>IsStatic × IsObjCOptional × IsProtocolRequirement ×
/// IsFromExtension</c> — the membership oracle <see cref="ProtocolVtableMembers"/> must agree with the
/// actual <c>{P}_vtable</c> struct that <see cref="EveryProtocolEmitter.EmitProtocolVtableStruct"/>
/// emits. "Agree" = the predicate says <i>include</i> iff the emitted struct actually carries the
/// member's function-pointer field. If they ever diverge, the C# reverse-dispatch mirrors compute slot
/// positions from a different membership set than the Swift struct lays out — the positional slot
/// corruption (Defect F / Bug #21) that only SIGSEGVs on the NativeAOT device leg.
///
/// <para>This is meaningful ONLY because members are built through <see cref="TestDecls"/>, whose
/// <c>Protocol(...)</c> promotes requirements to <c>IsProtocolRequirement=true</c> — the parser-real
/// combination. A member built with the old <c>false</c> default is excluded by the predicate AND
/// absent from the struct, so they agree <i>trivially on a combination production never emits</i>; the
/// invariant only has teeth on the <c>true</c> rows, which the matrix covers explicitly.</para>
/// </summary>
public class ProtocolVtableMembersInvariantTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly EveryProtocolEmitter _emitter;
    private readonly ClosureHandler _closureHandler;

    public ProtocolVtableMembersInvariantTests()
    {
        _typeDatabase = new TypeDatabase();
        _typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
        _emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        _closureHandler = new ClosureHandler(_typeDatabase);
    }

    // The 16 cells of IsStatic × IsObjCOptional × IsProtocolRequirement × IsFromExtension.
    public static TheoryData<bool, bool, bool, bool> FlagMatrix()
    {
        var data = new TheoryData<bool, bool, bool, bool>();
        foreach (var isStatic in new[] { false, true })
            foreach (var isObjCOptional in new[] { false, true })
                foreach (var isRequirement in new[] { false, true })
                    foreach (var isFromExtension in new[] { false, true })
                        data.Add(isStatic, isObjCOptional, isRequirement, isFromExtension);
        return data;
    }

    [Theory]
    [MemberData(nameof(FlagMatrix))]
    public void Property_PredicateAgreesWithEmittedStruct(
        bool isStatic, bool isObjCOptional, bool isRequirement, bool isFromExtension)
    {
        // Build a single-member protocol, then stamp the exact flag combination this cell tests.
        var property = TestDecls.Property("value", hasGetter: true, hasSetter: false);
        property.IsStatic = isStatic;
        property.IsObjCOptional = isObjCOptional;
        property.IsProtocolRequirement = isRequirement;
        property.IsFromExtension = isFromExtension;
        var protocol = ProtocolHolding(property);

        bool predicateIncludes = ProtocolVtableMembers.IncludesProperty(property, protocol, _closureHandler);
        bool structHasSlot = EmittedStructHasPropertyField(protocol, property.Name);

        Assert.Equal(predicateIncludes, structHasSlot);
    }

    [Theory]
    [MemberData(nameof(FlagMatrix))]
    public void Method_PredicateAgreesWithEmittedStruct(
        bool isStatic, bool isObjCOptional, bool isRequirement, bool isFromExtension)
    {
        var method = TestDecls.Method("doSomething");
        method.MethodType = isStatic ? MethodType.Static : MethodType.Instance;
        method.IsObjCOptional = isObjCOptional;
        method.IsProtocolRequirement = isRequirement;
        method.IsProtocolExtensionMethod = isFromExtension;
        var protocol = ProtocolHolding(method);

        bool predicateIncludes = ProtocolVtableMembers.IncludesMethod(method, protocol, _closureHandler);
        bool structHasSlot = EmittedStructHasMethodField(protocol, method.Name);

        Assert.Equal(predicateIncludes, structHasSlot);
    }

    // ===================================================================
    //  Factory-faithfulness guard — pins the TestDecls defaults so a future
    //  edit cannot silently re-introduce the IsProtocolRequirement fiction.
    // ===================================================================

    [Fact]
    public void Protocol_PromotesMethodRequirementToIsProtocolRequirement()
    {
        var method = TestDecls.Method("doSomething");
        Assert.False(method.IsProtocolRequirement); // bare factory default = parser-real "not a requirement"

        var protocol = TestDecls.Protocol("P", method);

        Assert.True(protocol.Methods[0].IsProtocolRequirement); // promoted on attachment
    }

    [Fact]
    public void Protocol_PromotesPropertyRequirementToIsProtocolRequirement()
    {
        var property = TestDecls.Property("value");
        Assert.False(property.IsProtocolRequirement);

        var protocol = TestDecls.Protocol("P", property);

        Assert.True(protocol.Properties[0].IsProtocolRequirement);
    }

    [Fact]
    public void Protocol_LeavesExtensionDefaultMethodAsNonRequirement()
    {
        var method = TestDecls.ExtensionDefault("withDefault");

        var protocol = TestDecls.Protocol("P", method);

        Assert.False(protocol.Methods[0].IsProtocolRequirement);
        Assert.True(protocol.Methods[0].IsProtocolExtensionMethod);
    }

    [Fact]
    public void Protocol_LeavesExtensionDefaultPropertyAsNonRequirement()
    {
        var property = TestDecls.ExtensionDefaultProperty("provided");

        var protocol = TestDecls.Protocol("P", property);

        Assert.False(protocol.Properties[0].IsProtocolRequirement);
        Assert.True(protocol.Properties[0].IsFromExtension);
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

    private static ProtocolDecl ProtocolHolding(BaseDecl member)
        // Use the raw Protocol factory but DO NOT let it promote IsProtocolRequirement — the matrix
        // sets that flag explicitly per cell. Build an empty protocol and attach the member directly.
    {
        var protocol = TestDecls.Protocol("TestProtocol");
        switch (member)
        {
            case PropertyDecl property:
                protocol.Properties.Add(property);
                break;
            case MethodDecl method:
                protocol.Methods.Add(method);
                break;
        }
        return protocol;
    }

    private string EmitVtableStruct(ProtocolDecl protocol)
    {
        using var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolVtableStruct(writer, protocol);
        return stringWriter.ToString();
    }

    // A property occupies a slot iff the struct carries `var func_{name}_get` (or `_set`).
    private bool EmittedStructHasPropertyField(ProtocolDecl protocol, string propertyName)
    {
        var output = EmitVtableStruct(protocol);
        return output.Contains($"func_{propertyName}_get") || output.Contains($"func_{propertyName}_set");
    }

    // A method occupies a slot iff the struct carries `var func_{name}_{index}`.
    private bool EmittedStructHasMethodField(ProtocolDecl protocol, string methodName)
    {
        var output = EmitVtableStruct(protocol);
        return output.Contains($"func_{methodName}_");
    }
}
