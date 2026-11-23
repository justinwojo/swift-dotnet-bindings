// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The default-argument trims of a failable initializer. Its primary emits as
/// <c>TryCreate…(…, out T)</c>, which can carry no inline C# default, so the trims are the only
/// way to omit a defaulted argument; and a trim is costed only by a sibling factory that can share
/// its C# name.
/// </summary>
public class FailableFactoryDefaultOverloadTests
{
    private sealed record Fixture(
        MethodEnvironment Env, StructDecl Type, MethodDecl Source, MethodDecl Trim, MethodDecl FailableSibling, TypeDatabase Db);

    private static ArgumentDecl Arg(string name, BaseDecl parent, ModuleDecl module, bool defaulted = false) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
        IsInOut = false,
        IsGeneric = false,
        HasDefaultArg = defaulted,
        SwiftDefaultExpression = defaulted ? "3" : null,
        ParentDecl = parent,
        ModuleDecl = module,
    };

    private static MethodDecl Init(string mangled, StructDecl parent, ModuleDecl module, bool failable,
        params (string Label, bool Defaulted)[] args)
    {
        var init = new MethodDecl
        {
            Name = "init",
            MangledName = mangled,
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = failable,
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            GenericParameters = new(),
            CSSignature = new(),
        };
        init.CSSignature.Add(new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = new NamedTypeSpec("M.S"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = init,
            ModuleDecl = module,
        });
        foreach (var (label, defaulted) in args)
            init.CSSignature.Add(Arg(label, init, module, defaulted));
        return init;
    }

    // `struct S { init(a:); init?(second:); init?(_ x:, y: = 3) }`: `init?(second:)` shares
    // `init(a:)`'s constructor signature and recovers as `TryCreateWithSecond(int)`, so the
    // `TryCreate(int x)` trim of `init?(_:y:)` duplicates nothing.
    private static Fixture CreateFixture(bool interiorDefault = false)
    {
        var db = new TypeDatabase { AsyncLibraryName = "MWrapper" };
        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swift.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int32"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            MetadataAccessor = "$ss5Int32VMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(swift);
        var m = new ModuleTypeDatabase("M", "/tmp/M.dylib");
        var sName = SwiftTypeName.FromModuleQualifiedName("M.S");
        m.RegisterType(sName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", "S"),
            SwiftTypeName = sName,
            MetadataAccessor = "$s1M1SVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(m);

        var module = new ModuleDecl
        {
            Name = "M", ParentDecl = null, ModuleDecl = null,
            Properties = new(), Methods = new(), Types = new(), Dependencies = new(), Protocols = new(),
        };
        var type = new StructDecl
        {
            Name = "S",
            SwiftTypeName = sName,
            MangledName = "$s1M1SVN",
            Properties = new(), Methods = new(), Types = new(), Operators = new(), Subscripts = new(),
            GenericParameters = new(), Conformances = new(),
            ParentDecl = module, ModuleDecl = module, IsFrozen = true, MetadataAccessor = "$s1M1SVMa",
        };
        module.Types.Add(type);

        // The interior-default shape, `init(a:b:); init?(second:third:); init?(_ x:, y: = 3, z:)`,
        // reaches its two-argument form only as the all-defaults overload, not as a trailing trim.
        var plain = interiorDefault
            ? Init("$s1M1SV1a1bACs5Int32V_AEtcfC", type, module, failable: false, ("a", false), ("b", false))
            : Init("$s1M1SV1aACs5Int32V_tcfC", type, module, failable: false, ("a", false));
        var failableSibling = interiorDefault
            ? Init("$s1M1SV6second5thirdACSgs5Int32V_AEtcfC", type, module, failable: true, ("second", false), ("third", false))
            : Init("$s1M1SV6secondACSgs5Int32V_tcfC", type, module, failable: true, ("second", false));
        var source = interiorDefault
            ? Init("$s1M1SV_1y1zACSgs5Int32V_A2EtcfC", type, module, failable: true, ("", false), ("y", true), ("z", false))
            : Init("$s1M1SV_1yACSgs5Int32V_AEtcfC", type, module, failable: true, ("", false), ("y", true));
        type.Methods.AddRange(new[] { plain, failableSibling, source });
        var trim = Init("$s1M1SV_1yACSgs5Int32V_AEtcfC_trim1", type, module, failable: true, ("", false));

        var env = new MethodEnvironment(source, db)
        {
            EmissionContext = new ModuleEmissionContext(),
            EmittedProjectedSignatures = new(),
            ReservedOverloadShapes = new(),
        };
        return new Fixture(env, type, source, trim, failableSibling, db);
    }

    private static string FactoryKey(string name, MethodDecl decl, TypeDatabase db)
    {
        var key = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(decl, db);
        return "failable-factory:" + name + key.Substring(key.IndexOf('('));
    }

    private static bool Collides(Fixture f) => DefaultParameterOverloadEmitter.TryFindSignatureCollision(
        f.Source, f.Trim, FactoryKey("TryCreate", f.Trim, f.Db), 1, f.Env, out _,
        // The trim of a mappable default is new with failable-factory trims, so it is costed as C#
        // counts its arity.
        costByProjectedArity: true);

    [Fact]
    public void Trim_IsNotCostedBy_ASiblingThatEmittedUnderAnotherFactoryName()
    {
        var f = CreateFixture();
        f.FailableSibling.EmittedFailableFactoryName = "TryCreateWithSecond";
        f.Env.EmittedProjectedSignatures!.Add(FactoryKey("TryCreateWithSecond", f.FailableSibling, f.Db));

        Assert.False(Collides(f));
    }

    [Fact]
    public void Trim_IsCostedBy_ASiblingThatEmittedUnderTheSameFactoryName()
    {
        var f = CreateFixture();
        f.FailableSibling.EmittedFailableFactoryName = "TryCreate";
        f.Env.EmittedProjectedSignatures!.Add(FactoryKey("TryCreate", f.FailableSibling, f.Db));

        Assert.True(Collides(f));
    }

    [Fact]
    public void Trim_IsCostedBy_ASiblingWhoseNameThisWalkHasNotFixed()
    {
        // A stamp left by an earlier verify-recover pass, with no reservation in this walk: the
        // sibling has not emitted yet, its name is still open, and the trim stays conservative.
        var f = CreateFixture();
        f.FailableSibling.EmittedFailableFactoryName = "TryCreateWithSecond";

        Assert.True(Collides(f));
    }

    // `init?(late: Int32, file: StaticString = #fileID)`, which projects as the one-argument
    // `TryCreate(int, out S)` though Swift counts two parameters.
    private static MethodDecl AddCallerLocationSibling(Fixture f)
    {
        var module = f.Type.ModuleDecl!;
        var late = Init("$s1M1SV4late4fileACSgs5Int32V_s12StaticStringVtcfC", f.Type, module, failable: true, ("late", false));
        late.CSSignature.Add(new ArgumentDecl
        {
            Name = "file",
            PrivateName = "file",
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = true,
            SwiftDefaultExpression = "#fileID",
            ParentDecl = late,
            ModuleDecl = module,
        });
        f.Type.Methods.Add(late);
        return late;
    }

    [Fact]
    public void Trim_IsCostedBy_ASiblingWhoseCallerLocationParameterTheProjectionDrops()
    {
        // Its two Swift parameters hid `init?(late:file:)` from the arity filter, and the trim then
        // pushed the existing factory to a label-suffixed name.
        var f = CreateFixture();
        f.Type.Methods.Remove(f.FailableSibling);
        AddCallerLocationSibling(f);

        Assert.True(Collides(f));
    }

    [Fact]
    public void Trim_AdmittedByAnotherFactoryName_IsCostedAsCSharpCountsItsArity()
    {
        // A trim of a default C# cannot spell. `init?(second:)` costed it until the factory-name
        // exemption, so it is new, and must not push the later `init?(late:file:)` off `TryCreate`.
        var f = CreateFixture();
        f.FailableSibling.EmittedFailableFactoryName = "TryCreateWithSecond";
        f.Env.EmittedProjectedSignatures!.Add(FactoryKey("TryCreateWithSecond", f.FailableSibling, f.Db));
        AddCallerLocationSibling(f);

        Assert.True(DefaultParameterOverloadEmitter.TryFindSignatureCollision(
            f.Source, f.Trim, FactoryKey("TryCreate", f.Trim, f.Db), 1, f.Env, out _));
    }

    [Fact]
    public void AllDefaultsForm_IsNotCostedBy_ASiblingThatEmittedUnderAnotherFactoryName()
    {
        // The all-defaults preflight must read the form's key as it emits, `TryCreate(int, int)`:
        // under the constructor key the factory-name check never applied, and the form was declined
        // for a sibling that already emitted as `TryCreateWithSecondThird(int, int)`.
        var f = CreateFixture(interiorDefault: true);
        f.FailableSibling.EmittedFailableFactoryName = "TryCreateWithSecondThird";
        f.Env.EmittedProjectedSignatures!.Add(FactoryKey("TryCreateWithSecondThird", f.FailableSibling, f.Db));

        var csText = new StringWriter();
        var cs = new CSharpWriter(csText);
        var swift = new SwiftWriter(new StringWriter());
        using (cs.BeginFragment(FragmentOwners.ForDecl(f.Source)))
        using (swift.BeginFragment(FragmentOwners.ForDeclWrapper(f.Source)))
            DefaultParameterOverloadEmitter.TryEmitOverloads(cs, swift, f.Env, NullLogger.Instance, f.Env.EmissionContext);

        Assert.Contains("failable-factory:TryCreate(int,int)", f.Env.EmittedProjectedSignatures!);
    }

    [Fact]
    public void PrimaryFailableFactory_WithAMappableDefault_StillGetsItsTrim()
    {
        // `init?(_ x: Int32, y: Int32 = 3)`: `3` maps to a C# default, but `TryCreate(int x, int y,
        // out S result)` cannot carry it, so the one-argument `TryCreate(int x, out S)` is only
        // reachable as a trim.
        var f = CreateFixture();
        f.Type.Methods.Remove(f.FailableSibling);

        var csText = new StringWriter();
        var swiftText = new StringWriter();
        var cs = new CSharpWriter(csText);
        var swift = new SwiftWriter(swiftText);
        using (cs.BeginFragment(FragmentOwners.ForDecl(f.Source)))
        using (swift.BeginFragment(FragmentOwners.ForDeclWrapper(f.Source)))
            DefaultParameterOverloadEmitter.TryEmitOverloads(cs, swift, f.Env, NullLogger.Instance, f.Env.EmissionContext);

        Assert.Contains("failable-factory:TryCreate(int)", f.Env.EmittedProjectedSignatures!);
        Assert.Contains("TryCreate(", csText.ToString());
    }
}
