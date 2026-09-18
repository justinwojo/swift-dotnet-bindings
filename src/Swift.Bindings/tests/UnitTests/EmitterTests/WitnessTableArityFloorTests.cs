// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the ABI floor arm that refuses a generic member whose Swift entry point takes a
/// protocol witness table the P/Invoke does not pass.
///
/// <para>The defect these pin: Swift's generic calling convention appends one metadata pointer per
/// generic parameter and one witness table per protocol requirement on a generic parameter, but the
/// P/Invoke declares a witness-table slot only for a conformance the type database can project. A
/// constraint to <c>Swift.Sequence</c> — which is in no database — therefore leaves the callee
/// reading its witness table from a register nobody wrote, and no compiler sees both sides of the
/// call. Silent at generation time, a fault on the first witness call at runtime.</para>
///
/// <para>The hard requirement is the negative direction: a superclass bound, an <c>AnyObject</c>
/// layout requirement and a marker protocol are all spelled like conformances on the parsed side
/// (and a marker is even mangled as one) while passing no witness table at all. Refusing those
/// would tombstone members that call correctly today, so both directions are asserted, against
/// symbols the Swift 6.2 toolchain emitted for a module "M":
/// <code>
///   public class Base {}; public protocol P {}
///   public func sup&lt;T: Base&gt;(_: T); public func pro&lt;T: P&gt;(_: T)
///   public func seq&lt;T: Sequence&gt;(_: T) where T.Element: Hashable
///   public func anyobj&lt;T: AnyObject&gt;(_: T); public func snd&lt;T: Sendable&gt;(_: T)
///   public func both&lt;T: P &amp; Sendable&gt;(_: T)
/// </code></para>
/// </summary>
public class WitnessTableArityFloorTests
{
    private const string SuperclassBound = "$s1M3supyyxAA4BaseCRbzlF";
    private const string ProjectedProtocol = "$s1M3proyyxAA1PRzlF";
    private const string UnknownProtocol = "$s1M3seqyyxSTRzSH7ElementRpzlF";
    private const string AnyObjectLayout = "$s1M6anyobjyyxRlzClF";
    private const string MarkerProtocol = "$s1M3sndyyxs8SendableRzlF";
    private const string ProjectedPlusMarker = "$s1M4bothyyxAA1PRzs8SendableRzlF";

    [Fact]
    public void Floor_ConstraintToUnprojectedProtocol_Fires()
    {
        // The Euclid/CoreStore shape. Swift.Sequence is in no type database, so the conformance is
        // dropped from the P/Invoke — while the entry point still expects its witness table.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(UnknownProtocol, moduleDecl, Conformance("Swift.Sequence"));

        Assert.True(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_ConstraintToProjectedProtocol_DoesNotFire()
    {
        // The load-bearing positive control: a projected protocol DOES get a witness-table slot, so
        // the arities agree and the member calls correctly. This is the surface an over-broad gate
        // would destroy.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(ProjectedProtocol, moduleDecl, Conformance("TestModule.MyProtocol"));

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_SuperclassBound_DoesNotFire()
    {
        // The parser records a superclass bound as a Protocol-kind conformance, so the parsed
        // signature alone cannot tell it from one. Swift passes no witness table for it, and the
        // mangling says so — Rb, not R.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(SuperclassBound, moduleDecl, Conformance("TestModule.MyClass"));

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_AnyObjectLayoutRequirement_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(AnyObjectLayout, moduleDecl, Conformance("Swift.AnyObject"));

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Theory]
    [InlineData(MarkerProtocol)]
    [InlineData(ProjectedPlusMarker)]
    public void Floor_MarkerProtocolConstraint_DoesNotFire(string mangledName)
    {
        // A marker protocol IS mangled as a conformance requirement but is erased at runtime and
        // carries no witness table, so counting it as required refuses working members — including
        // any generic member of a Sendable-annotated API alongside a real, projected constraint.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var conformances = mangledName == ProjectedPlusMarker
            ? new[] { Conformance("TestModule.MyProtocol"), Conformance("Swift.Sendable") }
            : new[] { Conformance("Swift.Sendable") };
        var method = GenericMethod(mangledName, moduleDecl, conformances);

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_CdeclWrappedMember_DoesNotFire()
    {
        // A @_cdecl wrapper cannot be generic: it declares exactly the slots it reads and its body
        // has to satisfy the requirement, which swiftc checks. The floor exists for the routes no
        // compiler sees both sides of, so it must not reach this one.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(UnknownProtocol, moduleDecl, Conformance("Swift.Sequence"));
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_NonGenericMember_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod(ProjectedProtocol, moduleDecl);

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_UndemangleableSymbolWithDroppedConformance_FailsClosed()
    {
        // Nothing can be read from the symbol, so the decision falls back to the parsed signature.
        // A conformance target that is neither projected, nor a known class, nor a marker is
        // positive evidence that a slot was dropped — refuse rather than emit a call that may fault.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod("not a symbol", moduleDecl, Conformance("Swift.Sequence"));

        Assert.True(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_UndemangleableSymbolWithProjectedConformance_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var method = GenericMethod("not a symbol", moduleDecl, Conformance("TestModule.MyProtocol"));

        Assert.False(WrapperValidation.HasWitnessTableArityMismatch(new MethodEnvironment(method, typeDb)));
    }

    private static GenericParameterConformance Conformance(string target) =>
        new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(target),
            ConformanceKind.Protocol);

    private static MethodDecl GenericMethod(
        string mangledName, ModuleDecl moduleDecl, params GenericParameterConformance[] conformances)
    {
        var genericParameters = conformances.Length == 0
            ? new List<GenericArgumentDecl>()
            : new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0",
                    "T",
                    conformances.ToList(),
                    new List<GenericParameterConformance>())
            };

        return new MethodDecl
        {
            Name = "member",
            MangledName = mangledName,
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = genericParameters,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateEnvironment()
    {
        var typeDb = new TypeDatabase();

        var moduleDecl = new ModuleDecl
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

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IMyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "$s10TestModule10MyProtocolMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }
}
