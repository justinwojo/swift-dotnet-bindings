// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="ClosedConstrainedClosureEmitter.IsEligible"/>, the GTC-gate
/// rescue predicate. The runtime behaviour (callbacks fire with the right values) is exercised
/// end-to-end by BindingTests <c>ClosedConstrainedClosureTests</c>; these tests pin the
/// eligibility <em>boundary</em> — the shapes the gate must REJECT so it can never route a
/// member to <c>RoutedElsewhere</c> that the emitter would then silently drop. Because
/// <c>IsEligible</c> and the emitter share <c>TryBuildPlan</c>, a rejection here is a guarantee
/// the emitter also declines that shape.
/// </summary>
public class ClosedConstrainedClosureEmitterTests
{
    // ─── Positive: the canonical closed-instantiation shape ────────────

    [Fact]
    public void IsEligible_ConstrainedExtensionOnGenericClass_EscapingClosure_ReturnsTrue()
    {
        var (method, db) = BuildEligibleMethod();
        Assert.True(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── Direct (non-xcframework) mode: no @_cdecl wrapper exists (Finding A) ────

    [Fact]
    public void IsEligible_DirectMode_ReturnsFalse()
    {
        // AsyncLibraryName == null ⇒ GenerationMode.Direct ⇒ IsXCFrameworkMode false.
        // The gate MUST decline here so the member falls through to a visible skip rather
        // than routing away to an emitter that no-ops in Direct mode.
        var (method, db) = BuildEligibleMethod(xcframeworkMode: false);
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── Multi-parameter parent has no single closed arity (Finding C) ──

    [Fact]
    public void IsEligible_MultiGenericParameterParent_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(parentGenericParams: new[] { "Base", "Other" });
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── inout scalar has a different ABI than the by-value path (Finding E) ────

    [Fact]
    public void IsEligible_InOutScalarParam_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(scalarInOut: true);
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── A slot with >1 constraint: the anchor may not satisfy the extra one (Finding B) ──

    [Fact]
    public void IsEligible_MultiConstraintSlot_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(extraSlotConstraint: "TestModule.SomeProtocol");
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── A protocol-only constraint resolves to no class anchor ─────────

    [Fact]
    public void IsEligible_ProtocolOnlyConstraint_NoClassAnchor_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(anchorKind: TypeRecordKind.Protocol);
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── No closure param ⇒ never a GTC victim, nothing to rescue ───────

    [Fact]
    public void IsEligible_NoClosureParam_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(includeClosure: false);
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── Async / non-generic-parent / accessor are all out of scope ─────

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod();
        method.IsAsync = true;
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    [Fact]
    public void IsEligible_NonGenericParent_ReturnsFalse()
    {
        var (method, db) = BuildEligibleMethod(parentGenericParams: new string[0]);
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(method, db));
    }

    // ─── Label-only overloads collapse to one C# extension: decline the later one ──

    [Fact]
    public void IsEligible_LabelOnlyOverloadPair_EarlierEmits_LaterDeclined()
    {
        // register(success:) and register(failure:) — same name, same (Int32)->Void closure type,
        // differing only by argument label — project to the SAME C# `Register(Action<int>)` extension.
        // Emitting both is CS0111, so the gate must admit the earlier and decline the later (which then
        // surfaces a visible GenericTypeCallback skip). Otherwise the later would silently vanish.
        var (first, second, db) = BuildLabelOnlyOverloadPair();
        Assert.True(ClosedConstrainedClosureEmitter.IsEligible(first, db), "earlier-declared overload emits");
        Assert.False(ClosedConstrainedClosureEmitter.IsEligible(second, db), "later colliding overload is declined");
    }

    [Fact]
    public void IsEligible_DistinctClosureShapeOverloads_BothEligible()
    {
        // Same name but distinct closure shapes ((Int32)->Void vs (Int32,Int32)->Void) → distinct C#
        // delegate types (Action<int> vs Action<int,int>) → distinct, legal C# overloads. Neither
        // collides, so both stay eligible.
        var (first, second, db) = BuildLabelOnlyOverloadPair(distinctSecondClosure: true);
        Assert.True(ClosedConstrainedClosureEmitter.IsEligible(first, db));
        Assert.True(ClosedConstrainedClosureEmitter.IsEligible(second, db));
    }

    // ─── Scaffolding ────────────────────────────────────────────────────

    /// <summary>
    /// Builds one generic class parent carrying two closure methods with the SAME name declared in
    /// order (first, then second), differing only by argument label. When <paramref name="distinctSecondClosure"/>
    /// is false both take a <c>(Int32) -&gt; Void</c> closure (they collide in C#); otherwise the second
    /// takes a <c>(Int32, Int32) -&gt; Void</c> closure (distinct C# delegate → no collision).
    /// </summary>
    private static (MethodDecl first, MethodDecl second, TypeDatabase db) BuildLabelOnlyOverloadPair(
        bool distinctSecondClosure = false)
    {
        var (first, db) = BuildEligibleMethod();
        first.Name = "register";
        var parent = (ClassDecl)first.ParentDecl!;
        var moduleDecl = first.ModuleDecl!;
        // Relabel the first's closure param label to `success`.
        first.CSSignature[first.CSSignature.Count - 1].Name = "success";

        var closureArgs = distinctSecondClosure
            ? new[] { (TypeSpec)new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.Int32") }
            : new[] { (TypeSpec)new NamedTypeSpec("Swift.Int32") };
        var closure = new ClosureTypeSpec(new TupleTypeSpec(closureArgs), TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        var second = new MethodDecl
        {
            Name = "register",
            MangledName = "$s10TestModule11HostWrapperCA2A9PixelHostRczrlE8register_FAILURE",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                MakeArg(string.Empty, TupleTypeSpec.Empty, moduleDecl, isInOut: false),
                MakeArg("scaleBy", new NamedTypeSpec("Swift.Int32"), moduleDecl, isInOut: false),
                MakeArg("failure", closure, moduleDecl, isInOut: false),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("Base", "Base", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        new[] { "Base" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.PixelHost"),
                        ConformanceKind.Protocol)
                }, new List<GenericParameterConformance>())
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parent.Methods.Add(second); // declared AFTER first
        return (first, second, db);
    }


    /// <summary>
    /// Builds the canonical eligible shape — a generic class <c>HostWrapper&lt;Base&gt;</c> with an
    /// instance method <c>loadPixels(scaleBy: Int32, onSuccess: @escaping (Int32) -&gt; Void)</c>
    /// whose <c>Base</c> slot is constrained to the concrete class <c>PixelHost</c> — and lets each
    /// knob mutate exactly one axis so a negative test isolates a single rejection reason.
    /// </summary>
    private static (MethodDecl method, TypeDatabase db) BuildEligibleMethod(
        bool xcframeworkMode = true,
        string[]? parentGenericParams = null,
        bool scalarInOut = false,
        bool includeClosure = true,
        string? extraSlotConstraint = null,
        TypeRecordKind anchorKind = TypeRecordKind.Class)
    {
        parentGenericParams ??= new[] { "Base" };

        var db = new TypeDatabase();
        if (xcframeworkMode)
            db.AsyncLibraryName = "libSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        db.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PixelHost"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PixelHost"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PixelHost"),
                MetadataAccessor = "$s10TestModule9PixelHostCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = anchorKind
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.HostWrapper"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "HostWrapper"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HostWrapper"),
                MetadataAccessor = "$s10TestModule11HostWrapperCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        db.AddModuleDatabase(testModule);

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

        var parent = new ClassDecl
        {
            Name = "HostWrapper",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HostWrapper"),
            MangledName = "$s10TestModule11HostWrapperCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        foreach (var tp in parentGenericParams)
            parent.GenericParameters.Add(
                new GenericArgumentDecl(tp, tp, new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));
        moduleDecl.Types.Add(parent);

        // Constraint on the Base slot: `where Base: PixelHost` (+ optional extra constraint).
        var slotConformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "Base" },
                SwiftTypeName.FromModuleQualifiedName("TestModule.PixelHost"),
                ConformanceKind.Protocol)
        };
        if (extraSlotConstraint != null)
            slotConformances.Add(new GenericParameterConformance(
                new[] { "Base" },
                SwiftTypeName.FromModuleQualifiedName(extraSlotConstraint),
                ConformanceKind.Protocol));

        var args = new List<ArgumentDecl>
        {
            MakeArg(string.Empty, TupleTypeSpec.Empty, moduleDecl, isInOut: false),
            MakeArg("scaleBy", new NamedTypeSpec("Swift.Int32"), moduleDecl, isInOut: scalarInOut),
        };
        if (includeClosure)
        {
            var closure = new ClosureTypeSpec(
                new TupleTypeSpec(new[] { (TypeSpec)new NamedTypeSpec("Swift.Int32") }),
                TupleTypeSpec.Empty);
            closure.Attributes.Add(new TypeSpecAttribute("escaping"));
            args.Add(MakeArg("onSuccess", closure, moduleDecl, isInOut: false));
        }

        var method = new MethodDecl
        {
            Name = "loadPixels",
            MangledName = "$s10TestModule11HostWrapperCA2A9PixelHostRczrlE10loadPixels",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = args,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("Base", "Base", slotConformances, new List<GenericParameterConformance>())
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parent.Methods.Add(method);

        return (method, db);
    }

    private static ArgumentDecl MakeArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool isInOut)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = isInOut,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }
}
