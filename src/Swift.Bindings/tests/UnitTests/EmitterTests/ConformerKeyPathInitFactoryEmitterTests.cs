// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape"/>,
/// the shape recognizer that decides which dependency constructors are rescuable as
/// consumer-side EntityProperty factories.
///
/// <para>The end-to-end happy path (a real dependency <c>MiniEntityProperty&lt;Value&gt;</c>
/// with <c>init&lt;Entity: AppEntity&gt;(identifier:, getter:/getSetter:)</c>, closed against
/// the MockBook conformer) is pinned by the <c>EntityPropertyFactoryTests</c> BindingTests.
/// These tests pin the recognizer in isolation: the structural gates that admit the shape and,
/// just as importantly, the negatives that must reject it — because a false positive would
/// emit a factory + Swift trampoline that fails to type-check and gets silently stripped.</para>
/// </summary>
public class ConformerKeyPathInitFactoryEmitterTests
{
    private const string AppEntity = "AppIntents.AppEntity";

    // ─── Positive: the canonical getter / getSetter shapes ────────────────

    [Fact]
    public void GetterKeyPathInit_IsRecognized_AsReadOnlyFlavor()
    {
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.True(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out var r));
        Assert.False(r.KeyPathIsWritable);
        Assert.Equal("getter", r.KeyPathArgLabel);
        Assert.Equal(AppEntity, r.ConstraintProtocolQualifiedName);
        Assert.Equal(new[] { "identifier" }, r.ScalarLabels);
    }

    [Fact]
    public void GetSetterWritableKeyPathInit_IsRecognized_AsWritableFlavor()
    {
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.WritableKeyPath", keyPathLabel: "getSetter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.True(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out var r));
        Assert.True(r.KeyPathIsWritable);
        Assert.Equal("getSetter", r.KeyPathArgLabel);
    }

    [Fact]
    public void ReferenceWritableKeyPathInit_IsRejected()
    {
        // RWKP requires a reference-type (class) root. AppEntity conformers are value types,
        // and the singleton emitter only originates KeyPath / WritableKeyPath singletons — none
        // of which bind a RWKP parameter. The emission path also collapses every writable shape
        // to WritableKeyPath, so recognizing RWKP would emit a trampoline the wrapper strips.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.ReferenceWritableKeyPath", keyPathLabel: "getSetter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void NoScalarParams_IsRecognized_WithEmptyScalarList()
    {
        // The KeyPath is the only parameter — still rescuable; the factory just has no scalars.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: System.Array.Empty<string>());
        dep.Methods.Add(ctor);

        Assert.True(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out var r));
        Assert.Empty(r.ScalarLabels);
    }

    [Fact]
    public void MatchesByDesugaredGenericName_NotJustSugared()
    {
        // The ABI commonly names generics positionally (τ_1_0 for the method generic, τ_0_0 for
        // the class generic). Recognition must match either the desugared OR sugared spelling.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "τ_1_0", rootProtocol: AppEntity, valueName: "τ_0_0",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.True(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    // ─── Negative: class-shape gates ──────────────────────────────────────

    [Fact]
    public void NonGenericClass_IsRejected()
    {
        var dep = BuildGenericClass("Plain", genericCount: 0);
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void ClassWithTwoGenerics_IsRejected()
    {
        // v1 closes exactly one type argument; a two-parameter class has no single Value slot.
        var dep = BuildGenericClass("TwoParam", genericCount: 2);
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    // ─── Negative: constructor-shape gates ────────────────────────────────

    [Fact]
    public void NonConstructorMethod_IsRejected()
    {
        var dep = BuildGenericClass("MiniEntityProperty");
        var method = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        var asMethod = method with { IsConstructor = false };
        dep.Methods.Add(asMethod);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, asMethod, out _));
    }

    [Fact]
    public void ConstructorWithoutMethodGenerics_IsRejected()
    {
        // A non-generic init can't carry the `where Entity : AppEntity` constraint that makes
        // the KeyPath root closeable against a conformer. Such an init still lists the class's
        // own generic (τ_0_0/Value) but has no method-own generic.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        var ctorNoGenerics = ctor with
        {
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0", "Value",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>()),
            },
        };
        dep.Methods.Add(ctorNoGenerics);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctorNoGenerics, out _));
    }

    // ─── Negative: KeyPath-arg gates ──────────────────────────────────────

    [Fact]
    public void RootNotAMethodGeneric_IsRejected()
    {
        // KeyPath<MockBook, Value> with a *concrete* root (not the method generic) isn't the
        // rescuable shape — there's no `Entity` to close against an arbitrary conformer.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "SomeModule.ConcreteRoot", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" }, rootMatchesMethodGeneric: false);
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void MethodGenericWithoutProtocolConstraint_IsRejected()
    {
        // `init<Entity>(getter: KeyPath<Entity, Value>)` with no `: P` constraint gives no
        // protocol to enumerate conformers of.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: null, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void MethodGenericWithMultipleProtocolConstraints_IsRejected()
    {
        // `init<Entity: AppEntity & Identifiable>(getter: KeyPath<Entity, Value>)`. Enumerating
        // conformers of just the first protocol would include types that don't satisfy the
        // second; their trampolines fail to type-check and get stripped. Reject the shape.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        // Splice a second protocol conformance onto the method-own generic (Entity).
        var entityGeneric = ctor.GenericParameters.First(g => g.SugaredTypeName == "Entity");
        entityGeneric.GenericConformances.Add(new GenericParameterConformance(
            new[] { "τ_1_0" },
            SwiftTypeName.FromModuleQualifiedName("Swift.Identifiable"),
            ConformanceKind.Protocol));

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void MethodGenericWithAssociatedTypeConstraint_IsRejected()
    {
        // A `where Entity.ID == String`-style associated-type requirement can't be guaranteed by
        // enumerating bare protocol conformers, so the shape isn't faithfully rescuable.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        var entityGeneric = ctor.GenericParameters.First(g => g.SugaredTypeName == "Entity");
        entityGeneric.AssosiatedTypeConformances.Add(new GenericParameterConformance(
            new[] { "τ_1_0", "ID" },
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            ConformanceKind.Protocol));

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void ConstructorWithTwoMethodGenerics_IsRejected()
    {
        // `init<Entity: AppEntity, Other>(getter: KeyPath<Entity, Value>)`. `Other` is uninferable
        // from the rescued KeyPath + scalar arguments, so the generated Swift call can't compile.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        ctor.GenericParameters.Add(new GenericArgumentDecl(
            "τ_1_1", "Other",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void ValueNotTheClassGeneric_IsRejected()
    {
        // KeyPath<Entity, String> — value is concrete, not the class's `Value`. The factory
        // can't close `MiniEntityProperty<V>` to the conformer's property value type.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Swift.String",
            scalarLabels: new[] { "identifier" });
        dep.Methods.Add(ctor);

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void UnsupportedScalarParam_RejectsWholeShape()
    {
        // A non-String scalar (e.g. an Int, or a LocalizedStringResource) the v1 factory can't
        // marshal must reject the entire init, not silently drop the param.
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: new[] { "identifier" });
        // Splice an unsupported Int parameter before the keypath.
        ctor.CSSignature.Insert(1, Arg("count", new NamedTypeSpec("Swift.Int")));

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    [Fact]
    public void TwoKeyPathParams_IsRejected()
    {
        var dep = BuildGenericClass("MiniEntityProperty");
        var ctor = BuildKeyPathCtor(
            "Swift.KeyPath", keyPathLabel: "getter",
            rootName: "Entity", rootProtocol: AppEntity, valueName: "Value",
            scalarLabels: System.Array.Empty<string>());
        // Add a second KeyPath param — the v1 factory marshals exactly one.
        ctor.CSSignature.Add(Arg("other", KeyPathSpec("Swift.KeyPath", "Entity", "Value")));

        Assert.False(ConformerKeyPathInitFactoryEmitter.TryRecognizeInitShape(dep, ctor, out _));
    }

    // ─── Builders ─────────────────────────────────────────────────────────

    private static ClassDecl BuildGenericClass(string name, int genericCount = 1)
    {
        var generics = new List<GenericArgumentDecl>();
        for (int i = 0; i < genericCount; i++)
        {
            // The first class generic is the "Value" slot the recognizer checks against.
            var sugared = i == 0 ? "Value" : $"T{i}";
            generics.Add(new GenericArgumentDecl(
                $"τ_0_{i}", sugared,
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"DepModule.{name}"),
            MangledName = $"$s9DepModule{name.Length}{name}C",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = generics,
            Conformances = new List<TypeConformance>(),
            IsFinal = true,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static MethodDecl BuildKeyPathCtor(
        string keyPathFamily, string keyPathLabel,
        string rootName, string? rootProtocol, string valueName,
        IReadOnlyList<string> scalarLabels,
        bool rootMatchesMethodGeneric = true)
    {
        // The method-own generic `Entity`, optionally constrained `: AppEntity`.
        var conformances = new List<GenericParameterConformance>();
        if (rootProtocol is not null)
            conformances.Add(new GenericParameterConformance(
                new[] { "τ_1_0" },
                SwiftTypeName.FromModuleQualifiedName(rootProtocol),
                ConformanceKind.Protocol));
        // Sugared name is "Entity" so a rootName of "Entity" or "τ_1_0" both match — unless the
        // test wants a non-matching concrete root.
        var methodGeneric = new GenericArgumentDecl(
            "τ_1_0", rootMatchesMethodGeneric ? "Entity" : "UnrelatedSugar",
            conformances, new List<GenericParameterConformance>());

        var sig = new List<ArgumentDecl>
        {
            // Index 0 is the constructor return type; recognition starts at index 1.
            Arg("$return", new NamedTypeSpec("DepModule.MiniEntityProperty")),
        };
        foreach (var label in scalarLabels)
            sig.Add(Arg(label, new NamedTypeSpec("Swift.String")));
        sig.Add(Arg(keyPathLabel, KeyPathSpec(keyPathFamily, rootName, valueName)));

        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s9DepModuleInit",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = sig,
            // Mirror the real ABI shape: a constructor's generic list carries the enclosing
            // class's generic(s) (depth-0, τ_0_0/Value) ahead of the method-own generic
            // (depth-1, τ_1_0/Entity). The recognizer must isolate the method-own one.
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    "τ_0_0", "Value",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>()),
                methodGeneric,
            },
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static NamedTypeSpec KeyPathSpec(string family, string root, string value) =>
        new NamedTypeSpec(family, new NamedTypeSpec(root), new NamedTypeSpec(value));

    private static ArgumentDecl Arg(string name, TypeSpec spec) =>
        new ArgumentDecl
        {
            Name = name,
            SwiftTypeSpec = spec,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };
}
