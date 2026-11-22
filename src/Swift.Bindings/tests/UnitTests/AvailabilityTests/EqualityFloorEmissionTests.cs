// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The floor of a type's equality wrapper: the <c>==</c> Swift resolves for two operands of the type,
/// merged with the type's floor and the floor of its Equatable conformance. Struct, enum and class
/// equality all read it for the Swift wrapper's <c>@available</c>, the guard in the private method
/// that calls the wrapper (every public Equals, <c>==</c> and <c>!=</c> reaches the wrapper through it),
/// and the <c>[SupportedOSPlatform]</c> the public members advertise.
/// </summary>
public class EqualityFloorEmissionTests
{
    public enum Kind { Enum, RefStruct, ValueStruct, Class }

    private const string Module = "TestModule";

    [Theory]
    [InlineData(Kind.Enum)]
    [InlineData(Kind.RefStruct)]
    [InlineData(Kind.ValueStruct)]
    [InlineData(Kind.Class)]
    public void EqualityHolder_RefusesBelowTheSelfEqualityFloor_NotAnotherOverloads(Kind kind)
    {
        var type = Create(kind);
        // Declared first, so a "first binary ==" selection would pick it: it compares the type with an Int.
        AddOperator(type, rhs: "Swift.Int", Ann("iOS", "50.0"));
        AddOperator(type, rhs: $"{Module}.Shape", Ann("iOS", "99.0"));

        var (swift, cs) = Emit(kind, type);

        Assert.Contains("@available(iOS 99.0, *)", swift);
        Assert.DoesNotContain("iOS 50.0", swift);
        AssertRefusesBefore(cs, Holder(kind), "PInvoke_eq(", "\"ios\", 99, 0");
        Assert.DoesNotContain("\"ios\", 50, 0", cs);
    }

    [Theory]
    [InlineData(Kind.Enum)]
    [InlineData(Kind.RefStruct)]
    [InlineData(Kind.ValueStruct)]
    [InlineData(Kind.Class)]
    public void EqualityHolder_RefusesBelowTheEquatableConformancesInferredFloor(Kind kind)
    {
        var type = Create(kind);
        type.ModuleDecl!.InferredConformanceFloors[EquatableDescriptor] = new List<AvailabilityAnnotation> { InferredFloor("27.2") };

        var (swift, cs) = Emit(kind, type);

        Assert.Contains("@available(iOS 27.2, *)", swift);
        AssertRefusesBefore(cs, Holder(kind), "PInvoke_eq(", "\"ios\", 27, 2");
    }

    [Theory]
    [InlineData(Kind.Enum)]
    [InlineData(Kind.RefStruct)]
    [InlineData(Kind.ValueStruct)]
    [InlineData(Kind.Class)]
    public void PublicEquality_AdvertisesAnEqualityFloorAboveTheTypes(Kind kind)
    {
        var type = Create(kind);
        AddOperator(type, rhs: $"{Module}.Shape", Ann("iOS", "99.0"));

        var (_, cs) = Emit(kind, type);

        foreach (var member in new[] { "public override bool Equals(object? obj)", "public bool Equals(", "operator ==(", "operator !=(" })
            Assert.Contains("SupportedOSPlatform(\"ios99.0\")", AttributesBefore(cs, member));
    }

    [Theory]
    [InlineData(Kind.Enum)]
    [InlineData(Kind.RefStruct)]
    [InlineData(Kind.ValueStruct)]
    [InlineData(Kind.Class)]
    public void Equality_AtTheTypesOwnFloor_IsGuardedButNotReattributed(Kind kind)
    {
        var type = Create(kind);
        type.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann("iOS", "99.0") };

        var (_, cs) = Emit(kind, type);

        AssertRefusesBefore(cs, Holder(kind), "PInvoke_eq(", "\"ios\", 99, 0");
        Assert.DoesNotContain("SupportedOSPlatform", AttributesBefore(cs, "public override bool Equals(object? obj)"));
    }

    /// <summary>
    /// An Objective-C-rooted Hashable class hashes through a Swift wrapper that goes through the
    /// Hashable witness, so GetHashCode, which calls it, refuses below that conformance's floor.
    /// </summary>
    [Fact]
    public void ObjCRootedHashableClass_HashRefusesBelowTheHashableConformancesFloor()
    {
        var type = (ClassDecl)Create(Kind.Class, objCRooted: true);
        type.Conformances.Add(new TypeConformance(type.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), HashableDescriptor));
        type.ModuleDecl!.InferredConformanceFloors[HashableDescriptor] = new List<AvailabilityAnnotation> { InferredFloor("27.2") };

        var (swift, cs) = Emit(Kind.Class, type);

        var hashWrapper = swift.IndexOf("witnessHash", StringComparison.Ordinal);
        Assert.True(hashWrapper > 0, swift);
        var before = swift[..hashWrapper];
        var cdecl = before.LastIndexOf("@_cdecl", StringComparison.Ordinal);
        var previousDeclarationEnd = before.LastIndexOf('}', cdecl);
        Assert.Contains("@available(iOS 27.2, *)", before[(previousDeclarationEnd + 1)..cdecl]);
        AssertRefusesBefore(cs, "public override int GetHashCode()", "PInvoke_hash(", "\"ios\", 27, 2");
        Assert.Contains("SupportedOSPlatform(\"ios27.2\")", AttributesBefore(cs, "public override int GetHashCode()"));
    }

    /// <summary>
    /// A class compares through its Equatable witness, the <c>==</c> of the class declaring the
    /// conformance. A subclass inheriting it lists the conformance with no descriptor, so its own
    /// <c>==</c> (floored above the OS, say) does not floor the wrapper, and the declaring base's does.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ClassInheritingEquatable_TakesTheFloorOfTheDeclaringBasesOperator(bool baseOperatorFloored, bool derivedOperatorFloored)
    {
        var derived = (ClassDecl)Create(Kind.Class);
        var baseClass = (ClassDecl)Create(Kind.Class);
        baseClass.Name = "Base";
        baseClass.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Base");
        baseClass.Conformances[0] = new TypeConformance(baseClass.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), "$s10TestModule4BaseCSQAAMc");
        derived.Conformances[0] = derived.Conformances[0] with { ProtocolConformanceDescriptor = string.Empty };
        derived.ResolvedSuperclass = baseClass;
        if (baseOperatorFloored)
            AddOperator(baseClass, rhs: $"{Module}.Base", Ann("iOS", "99.0"), lhs: $"{Module}.Base");
        if (derivedOperatorFloored)
            AddOperator(derived, rhs: $"{Module}.Shape", Ann("iOS", "99.0"));

        Assert.Same(baseClass, EqualityAvailability.ConformanceDeclarer(derived, "Swift.Equatable"));
        var (swift, cs) = Emit(Kind.Class, derived);

        Assert.Equal(baseOperatorFloored, swift.Contains("@available(iOS 99.0, *)", StringComparison.Ordinal));
        Assert.Equal(baseOperatorFloored, Body(cs, Holder(Kind.Class)).Contains("\"ios\", 99, 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declaring base's Equatable conformance floor reaches a subclass inheriting it, whose own
    /// listing of the conformance carries no descriptor to look a floor up by.
    /// </summary>
    [Fact]
    public void ClassInheritingEquatable_RefusesBelowTheDeclaringBasesConformanceFloor()
    {
        var derived = (ClassDecl)Create(Kind.Class);
        var baseClass = (ClassDecl)Create(Kind.Class);
        baseClass.Name = "Base";
        baseClass.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Base");
        derived.Conformances[0] = derived.Conformances[0] with { ProtocolConformanceDescriptor = string.Empty };
        derived.ResolvedSuperclass = baseClass;
        baseClass.ModuleDecl!.InferredConformanceFloors[EquatableDescriptor] = new List<AvailabilityAnnotation> { InferredFloor("27.2") };

        var (_, cs) = Emit(Kind.Class, derived);

        AssertRefusesBefore(cs, Holder(Kind.Class), "PInvoke_eq(", "\"ios\", 27, 2");
    }

    /// <summary>
    /// With no class in the resolved chain declaring the conformance (a superclass in another module),
    /// the witness cannot be named, and the class's own <c>==</c> floor stands in: never less than before.
    /// </summary>
    [Fact]
    public void ClassWhoseConformanceDeclarerIsOutsideTheModule_KeepsItsOwnOperatorsFloor()
    {
        var derived = (ClassDecl)Create(Kind.Class);
        derived.Conformances[0] = derived.Conformances[0] with { ProtocolConformanceDescriptor = string.Empty };
        AddOperator(derived, rhs: $"{Module}.Shape", Ann("iOS", "99.0"));

        Assert.Same(derived, EqualityAvailability.ConformanceDeclarer(derived, "Swift.Equatable"));
        var (_, cs) = Emit(Kind.Class, derived);

        AssertRefusesBefore(cs, Holder(Kind.Class), "PInvoke_eq(", "\"ios\", 99, 0");
    }

    // --- helpers ---

    private const string EquatableDescriptor = "$s10TestModule5ShapeVSQAAMc";
    private const string HashableDescriptor = "$s10TestModule5ShapeVSHAAMc";

    private static AvailabilityAnnotation Ann(string platform, string introduced) =>
        new(platform, introduced, null, null, false, false, null, null);

    private static AvailabilityAnnotation InferredFloor(string introduced) =>
        Ann("iOS", introduced) with { IsRuntimeSupportFloor = true, IsInferredOsFloor = true };

    private static string Holder(Kind kind) => kind switch
    {
        Kind.ValueStruct => "private static unsafe bool _PInvoke_eq_value(",
        Kind.Class => "private static bool _SwiftEquals(",
        _ => "private static bool _PInvoke_eq_pinned(",
    };

    private static TypeDecl Create(Kind kind, bool objCRooted = false)
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
        var name = SwiftTypeName.FromModuleQualifiedName($"{Module}.Shape");
        var conformances = new List<TypeConformance>
        {
            new(name, SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), EquatableDescriptor),
        };
        TypeDecl type = kind switch
        {
            Kind.Enum => new EnumDecl
            {
                Name = "Shape", SwiftTypeName = name, MangledName = "$s10TestModule5ShapeO",
                MetadataAccessor = "$s10TestModule5ShapeOMa", IsFrozen = false,
                Cases = new List<EnumCaseDecl>(), Conformances = conformances,
                Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(), ParentDecl = module, ModuleDecl = module,
            },
            Kind.Class => new ClassDecl
            {
                Name = "Shape", SwiftTypeName = name, MangledName = "$s10TestModule5ShapeC",
                Conformances = conformances, IsObjCRooted = objCRooted,
                Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(), ParentDecl = module, ModuleDecl = module,
            },
            _ => new StructDecl
            {
                Name = "Shape", SwiftTypeName = name, MangledName = "$s10TestModule5ShapeV",
                MetadataAccessor = "$s10TestModule5ShapeVMa", IsFrozen = kind == Kind.ValueStruct,
                Conformances = conformances,
                Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(), ParentDecl = module, ModuleDecl = module,
            },
        };
        return type;
    }

    private static void AddOperator(TypeDecl type, string rhs, AvailabilityAnnotation floor, string? lhs = null)
    {
        ArgumentDecl Arg(string typeName, string label) => new()
        {
            SwiftTypeSpec = new NamedTypeSpec(typeName), Name = label, PrivateName = label,
            IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = type.ModuleDecl,
        };
        type.Operators.Add(new OperatorDecl
        {
            Name = "==",
            OperatorSymbol = "==",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = new MethodDecl
            {
                Name = "==",
                MangledName = $"$s10TestModule5ShapeV2eeoiySbAC_{rhs.Length}tFZ",
                MethodType = MethodType.Static,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl> { Arg("Swift.Bool", string.Empty), Arg(lhs ?? $"{Module}.Shape", "lhs"), Arg(rhs, "rhs") },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = type,
                ModuleDecl = type.ModuleDecl,
                Throws = false,
                IsAsync = false,
                IsSynthesizedAccessor = false,
            },
            ParentDecl = type,
            ModuleDecl = type.ModuleDecl,
            AvailabilityAnnotations = new List<AvailabilityAnnotation> { floor },
        });
    }

    private static (string Swift, string CSharp) Emit(Kind kind, TypeDecl type)
    {
        var cs = new StringWriter();
        var swift = new StringWriter();
        var csWriter = new CSharpWriter(cs);
        var swiftWriter = new SwiftWriter(swift);
        var context = new ModuleEmissionContext();
        const string wrapper = "TestModuleSwiftBindings";
        switch (kind)
        {
            case Kind.Enum:
                new EnumEqualityMethodsWriter(csWriter, (EnumDecl)type, "Shape", swiftWriter, context, wrapper).WriteSwiftEquatableImplementation();
                break;
            case Kind.Class:
                new ClassEqualityMethodsWriter(csWriter, (ClassDecl)type, "Shape", hasExplicitEqualityOperator: false,
                    hasExplicitInequalityOperator: false, swiftWriter, context, wrapper).WriteSwiftEquatableImplementation();
                break;
            default:
                new EqualityMethodsWriter(csWriter, (StructDecl)type, refType: kind == Kind.RefStruct, "Shape",
                    hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: false,
                    swiftWriter, context, wrapper).WriteSwiftEquatableImplementation();
                break;
        }
        csWriter.Flush();
        swiftWriter.Flush();
        return (swift.ToString(), cs.ToString());
    }

    /// <summary>
    /// The body of the member whose declaration starts with <paramref name="signature"/> must throw
    /// PlatformNotSupportedException for a floor of <paramref name="versionArguments"/> before it
    /// reaches <paramref name="call"/>.
    /// </summary>
    internal static void AssertRefusesBefore(string cs, string signature, string call, string versionArguments)
    {
        var body = Body(cs, signature);
        var guard = body.IndexOf("PlatformNotSupportedException", StringComparison.Ordinal);
        var native = body.IndexOf(call, StringComparison.Ordinal);
        Assert.True(native > 0, $"'{signature}' does not call {call}:\n{body}");
        Assert.True(guard >= 0 && guard < native, $"'{signature}' reaches {call} before refusing:\n{body}");
        Assert.Contains(versionArguments, body[..native]);
    }

    /// <summary>The text from <paramref name="signature"/> to the end of its brace-delimited body.</summary>
    internal static string Body(string cs, string signature)
    {
        var start = cs.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not emitted:\n{cs}");
        var open = cs.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < cs.Length; i++)
        {
            if (cs[i] == '{')
                depth++;
            else if (cs[i] == '}' && --depth == 0)
                return cs[start..(i + 1)];
        }
        return cs[start..];
    }

    /// <summary>The attribute lines directly above the declaration that starts with <paramref name="signature"/>.</summary>
    internal static string AttributesBefore(string cs, string signature)
    {
        var start = cs.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not emitted:\n{cs}");
        var lines = cs[..start].Split('\n').Reverse().Skip(1)
            .TakeWhile(l => l.TrimStart().StartsWith('[') || l.TrimStart().StartsWith("#pragma", StringComparison.Ordinal));
        return string.Join("\n", lines);
    }
}
