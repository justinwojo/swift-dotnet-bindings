// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="AppEntityKeyPathSingletonEmitter.IsEligibleConformerType"/>,
/// the gate that decides which closed <c>AppIntents.AppEntity</c> conformers get a
/// <c>{Conformer}AppEntityKeyPaths</c> singleton container.
///
/// <para>The end-to-end happy path (a concrete, public, local conformer with three
/// <c>var</c> properties) is pinned by the MockBook BindingTests
/// (<c>MockAppEntityTests</c>). These tests pin the NEGATIVE gates that BindingTests
/// can't cheaply express without a fixture per case: generic / SPI / module-internal
/// conformers must be rejected, because the Root of <c>KeyPath&lt;Root, V&gt;</c> must be a
/// single closed type referenceable from the public container and the wrapper TU.</para>
/// </summary>
public class AppEntityKeyPathSingletonEmitterTests
{
    // ─── Positive: concrete, public, non-generic conformer is eligible ───

    [Fact]
    public void ConcretePublicStruct_IsEligible()
    {
        var conformer = BuildStruct("MockBook", frozen: false);
        Assert.True(AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformer));
    }

    [Fact]
    public void FrozenStruct_IsStillEligible()
    {
        // Frozen-ness is orthogonal to KeyPath rooting: a frozen struct still has
        // stored properties whose `\Root.prop` literals are valid. The gate must not
        // confuse @frozen with ineligibility.
        var conformer = BuildStruct("FrozenBook", frozen: true);
        Assert.True(AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformer));
    }

    // ─── Negative: generic conformer has no single closed Root ───────────

    [Fact]
    public void GenericStruct_IsRejected()
    {
        // `Box<T> : AppEntity` — `\Box<T>.prop` is not a concrete literal, so there is
        // no closed Root to anchor the singleton on.
        var conformer = BuildStruct("Box", frozen: false);
        conformer.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>()));
        Assert.True(conformer.IsGeneric);
        Assert.False(AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformer));
    }

    // ─── Negative: SPI / internal conformers can't back a public container ─

    [Fact]
    public void SpiProtectedStruct_IsRejected()
    {
        var conformer = BuildStruct("SpiBook", frozen: false);
        conformer.IsSpiProtected = true;
        Assert.False(AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformer));
    }

    [Fact]
    public void ModuleInternalStruct_IsRejected()
    {
        var conformer = BuildStruct("InternalBook", frozen: false);
        conformer.IsModuleInternal = true;
        Assert.False(AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformer));
    }

    // ─── Computed-property admission (allowComputed gate) ─────────────────
    //
    // The AppEntity emitter calls KeyPathBagWalker.IsEmittableProperty with
    // allowComputed: true, because a concrete root forms valid KeyPaths for computed
    // properties (`\Root.getOnly` → KeyPath, `\Root.getSet` → WritableKeyPath). The
    // nested-bag path keeps the default (allowComputed: false) so only stored
    // bag fields are KeyPath leaves. These pin both sides of that switch.

    [Fact]
    public void StoredProperty_IsEmittable_RegardlessOfAllowComputed()
    {
        var stored = BuildProperty("title", hasStorage: true, hasSetter: true);
        Assert.True(KeyPathBagWalker.IsEmittableProperty(stored, allowAbstract: false, allowComputed: false));
        Assert.True(KeyPathBagWalker.IsEmittableProperty(stored, allowAbstract: false, allowComputed: true));
    }

    [Fact]
    public void ComputedProperty_RejectedByDefault_AdmittedWithAllowComputed()
    {
        var computed = BuildProperty("summary", hasStorage: false, hasSetter: false);
        // Nested-bag default: computed property is rejected as a non-stored leaf.
        Assert.Equal("!HasStorage",
            KeyPathBagWalker.WhyPropertyNotEmittable(computed, allowAbstract: false, allowComputed: false));
        // AppEntity-direct-root path: admitted.
        Assert.True(KeyPathBagWalker.IsEmittableProperty(computed, allowAbstract: false, allowComputed: true));
    }

    [Fact]
    public void ComputedProperty_StillRejectedWhenStatic_EvenWithAllowComputed()
    {
        // allowComputed only lifts the !HasStorage gate; the other gates (static, internal,
        // SPI, missing getter) still apply. A static computed property has no instance
        // KeyPath, so it must stay rejected.
        var staticComputed = BuildProperty("shared", hasStorage: false, hasSetter: false, isStatic: true);
        Assert.Equal("IsStatic",
            KeyPathBagWalker.WhyPropertyNotEmittable(staticComputed, allowAbstract: false, allowComputed: true));
    }

    [Fact]
    public void ComputedProperty_WithThrowingGetter_IsRejected_EvenWithAllowComputed()
    {
        // `var foo: T { get throws }` is a valid effectful read-only property, but Swift
        // forbids forming a `\Root.foo` KeyPath to it. allowComputed must NOT let it
        // through, or the trampoline's key-path literal fails to compile.
        var throwingComputed = BuildProperty("riskyValue", hasStorage: false, hasSetter: false, getterThrows: true);
        Assert.Equal("EffectfulGetter",
            KeyPathBagWalker.WhyPropertyNotEmittable(throwingComputed, allowAbstract: false, allowComputed: true));
    }

    [Fact]
    public void ComputedProperty_WithAsyncGetter_IsRejected_EvenWithAllowComputed()
    {
        // `var foo: T { get async }` is likewise effectful and has no KeyPath literal.
        var asyncComputed = BuildProperty("loadedValue", hasStorage: false, hasSetter: false, getterAsync: true);
        Assert.Equal("EffectfulGetter",
            KeyPathBagWalker.WhyPropertyNotEmittable(asyncComputed, allowAbstract: false, allowComputed: true));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static PropertyDecl BuildProperty(
        string name, bool hasStorage, bool hasSetter,
        bool isStatic = false, bool getterThrows = false, bool getterAsync = false)
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl { Method = BuildAccessorMethod($"{name}_get", typeSpec, getterThrows, getterAsync) }
        };
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = BuildAccessorMethod($"{name}_set", typeSpec) });

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = isStatic,
            HasStorage = hasStorage,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static MethodDecl BuildAccessorMethod(string name, TypeSpec typeSpec, bool throws = false, bool isAsync = false) =>
        new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static StructDecl BuildStruct(string name, bool frozen) =>
        new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = frozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            ParentDecl = null,
            ModuleDecl = null,
        };
}
