// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="UnderscoreProtocolSynthesizer"/>, which injects
/// <see cref="ProtocolDecl"/> entries for underscore-prefixed public protocols
/// that <c>swift-api-digester</c> drops from its ABI JSON output (e.g.
/// <c>AppIntents._IntentValue</c>).
///
/// The synthesizer is the prerequisite for projecting
/// <c>IntentParameter&lt;Value&gt;</c> and ~15 other dependent AppIntents types
/// that are otherwise tombstoned with <c>IndeterminatePwtShape</c>: protocol
/// not projected in the type database.
/// </summary>
public class UnderscoreProtocolSynthesizerTests
{
    [Fact]
    public void Synthesize_AllowlistedProtocol_InjectsDeclWithExpectedMangledName()
    {
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue {
                associatedtype DisplayName
                associatedtype ParameterSummary
                associatedtype EditableValueType
                static var defaultValue: Self { get }
            }
            """);

        try
        {
            var synthesized = UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Contains("AppIntents._IntentValue", synthesized);
            var decl = Assert.Single(module.Protocols, p => p.Name == "_IntentValue");
            Assert.Equal("$s10AppIntents12_IntentValueP", decl.MangledName);
            // Descriptor symbol mirrors ModuleProcessor.ConvertProtocolTypeToDescriptorSymbol:
            // EndsWith('P') ? [..^1] + "Mp" : + "Mp". With our mangled name that yields
            // "$s10AppIntents12_IntentValueMp", which the runtime descriptor lookup needs.
            Assert.EndsWith("P", decl.MangledName);
            Assert.Equal(3, decl.AssociatedTypes.Count);
            // Method-level Self usage (`static var defaultValue: Self`) is NOT a Self
            // requirement under the parser's tight rule — matches SwiftABIParser:1695-1697.
            // PAT projection still fires on AssociatedTypes.Count > 0 alone.
            Assert.False(decl.HasSelfRequirement);
            Assert.True(decl.IsModuleInternal);
            Assert.False(decl.IsSpiProtected);
            Assert.Same(module, decl.ModuleDecl);
            Assert.Contains(decl, module.Types);
            Assert.True(moduleTypes.ContainsKey(new NamedTypeSpec("AppIntents._IntentValue")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_NotOnAllowlist_IsNoOp()
    {
        var module = CreateEmptyModule("SomeRandomFramework");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface("public protocol _IntentValue { associatedtype X }");

        try
        {
            var synthesized = UnderscoreProtocolSynthesizer.Synthesize(
                "SomeRandomFramework", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Empty(synthesized);
            Assert.Empty(module.Protocols);
            Assert.Empty(moduleTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_DeclAlreadyPresent_SkipsToAvoidDuplicate()
    {
        // If the digester ever starts emitting `_IntentValue` (or a future
        // toolchain regresses and emits underscored protocol nodes for some
        // module-version combinations), the synthesizer must not duplicate the
        // existing decl.
        var module = CreateEmptyModule("AppIntents");
        var existing = new ProtocolDecl
        {
            Name = "_IntentValue",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("AppIntents._IntentValue"),
            MangledName = "$s10AppIntents12_IntentValueP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Protocols.Add(existing);
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface("public protocol _IntentValue { associatedtype X }");

        try
        {
            var synthesized = UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Empty(synthesized);
            Assert.Single(module.Protocols);
            Assert.Same(existing, module.Protocols[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_MissingSwiftInterface_LogsAndReturnsEmpty()
    {
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();

        var synthesized = UnderscoreProtocolSynthesizer.Synthesize(
            "AppIntents", swiftInterfacePath: null,
            module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

        Assert.Empty(synthesized);
        Assert.Empty(module.Protocols);
    }

    [Fact]
    public void Synthesize_AllowlistedButNotDeclaredInSource_SkipsOne()
    {
        // Only `_IntentValue` is in the interface; `_ParameterSummarySwitchCase`
        // is allowlisted but not present. The synthesizer must produce one decl
        // and warn (but not throw) about the missing one.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            "public protocol _IntentValue { associatedtype Value }");

        try
        {
            var synthesized = UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Contains("AppIntents._IntentValue", synthesized);
            Assert.DoesNotContain("AppIntents._ParameterSummarySwitchCase", synthesized);
            Assert.Single(module.Protocols);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_ProtocolWithInheritance_CapturesInheritedProtocols()
    {
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue : Swift.Hashable, Swift.Sendable {
                associatedtype Value
                static var sentinel: Self { get }
            }
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.Equal(2, decl.InheritedProtocols.Count);
            var names = decl.InheritedProtocols
                .OfType<NamedTypeSpec>()
                .Select(n => n.Name)
                .ToList();
            Assert.Contains("Swift.Hashable", names);
            Assert.Contains("Swift.Sendable", names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_WithSelfDotAccess_HasSelfRequirementTrue()
    {
        // `Self.Value` (associated-type access through Self) is the tight-rule
        // trigger from SwiftABIParser:1695-1697. Same-type constraints
        // (`where Self == Foo`) are the other trigger.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue {
                associatedtype Value
                static func make() -> Self.Value
            }
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.True(decl.HasSelfRequirement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_ProtocolWithoutSelfRequirement_HasSelfRequirementFalse()
    {
        // Plain associated types alone do not force Self into the requirement.
        // Only `Self.` member access or `Self ==` equality flips the flag.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue {
                associatedtype Value
                var current: Value { get }
            }
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.False(decl.HasSelfRequirement);
            Assert.Single(decl.AssociatedTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_SameLineAttribute_StillExtracts()
    {
        // The real AppIntents.swiftinterface ships `_IntentValue` as
        //   `@_alwaysEmitConformanceMetadata public protocol _IntentValue { … }`
        // on a single line. An earlier draft of the extractor required attribute
        // lines to terminate in `\n` before the `public protocol` keyword, which
        // silently skipped this shape in production while passing every test.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            "@_alwaysEmitConformanceMetadata public protocol _IntentValue { associatedtype Value }");

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.Equal("_IntentValue", decl.Name);
            Assert.Single(decl.AssociatedTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_ParenthesizedSameLineAttribute_StillExtracts()
    {
        // `@available(iOS 26, *) public protocol _IntentValue` — attribute carries
        // a parenthesized argument list AND lives on the same line. Both shapes
        // exist across Apple SDKs, and the brace-counter must not eat the `}`
        // from a future attribute string before depth reaches zero.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            "@available(iOS 26, *) public protocol _IntentValue { associatedtype Value }");

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.Equal("_IntentValue", decl.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_BodyWithStringLiteralBrace_DoesNotMiscount()
    {
        // Brace-counter must skip braces inside string literals. The deprecation
        // message here has a `}` that would otherwise close the protocol body
        // prematurely and yield a malformed/truncated capture.
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue {
                @available(*, deprecated, message: "use Foo { bar }")
                associatedtype Value
            }
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            Assert.Single(decl.AssociatedTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_AssociatedTypeWithBound_RecordsConstraint()
    {
        var module = CreateEmptyModule("AppIntents");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue {
                associatedtype DisplayName : Swift.CustomStringConvertible
            }
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            var decl = Assert.Single(module.Protocols);
            var at = Assert.Single(decl.AssociatedTypes);
            Assert.Equal("DisplayName", at.Name);
            Assert.Contains("Swift.CustomStringConvertible", at.Constraints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Gap A: re-attaching digester-stripped conformance records --------------

    [Fact]
    public void Synthesize_AttachesStrippedConformance_ToLocalReferenceTypedConformers()
    {
        // The digester strips `_IntentValue` AND its conformance records. After synthesis,
        // unconditional extensions on local reference-typed nominals (non-frozen struct,
        // class) must regain the conformance so bound-generic constraint checks pass.
        var module = CreateEmptyModule("AppIntents");
        var intentFile = AddStruct(module, "IntentFile", isFrozen: false);
        var someClass = AddClass(module, "SomeReferenceType");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension AppIntents.IntentFile : AppIntents._IntentValue {}
            extension AppIntents.SomeReferenceType : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Contains(intentFile.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
            Assert.Contains(someClass.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
            // Empty descriptor: the conformance is a type-database fact only; every
            // runtime-conformance emission path skips empty-descriptor / PAT entries.
            Assert.All(intentFile.Conformances, c => Assert.Equal(string.Empty, c.ProtocolConformanceDescriptor));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_AttachesStrippedConformance_ToFrozenValueTypeConformer()
    {
        // A frozen (value-typed) local struct now regains the conformance just like a
        // reference type. The relaxed GenericTypeEmitter seed (descriptor-path-safe PATs drop
        // the ISwiftObject seed) lets a closed IntentParameter<FrozenStruct> type-check, so
        // satisfying the constraint enables a usable binding rather than a dead one.
        var module = CreateEmptyModule("AppIntents");
        var frozen = AddStruct(module, "FrozenValue", isFrozen: true);
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension AppIntents.FrozenValue : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Contains(frozen.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
            // Type-database fact only: empty descriptor, never emitted as runtime conformance.
            Assert.All(frozen.Conformances, c => Assert.Equal(string.Empty, c.ProtocolConformanceDescriptor));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_SkipsConditionalConformance()
    {
        // `extension X : _IntentValue where ...` is a conditional conformance. Attaching it
        // unconditionally would let element types that don't themselves conform slip the
        // constraint check, so the conditional extension must be skipped even for a local
        // reference-typed conformer.
        var module = CreateEmptyModule("AppIntents");
        var box = AddStruct(module, "Box", isFrozen: false);
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension AppIntents.Box : AppIntents._IntentValue where Element : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Empty(box.Conformances);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_RegistersForeignConformer_OnTypeDatabase()
    {
        // Foreign / stdlib conformers (Swift.Int, Foundation.Date) have no local TypeDecl to
        // carry a conformance, so their (concrete, protocol) fact is recorded on the type
        // database instead. BoundGenericsHandler.SatisfiesConstraint consults this in its
        // typeArgumentDecl == null branch so a closed IntentParameter<Int> is not skipped.
        // The local reference-typed conformer still gets the conformance attached to its decl.
        var module = CreateEmptyModule("AppIntents");
        var intentFile = AddStruct(module, "IntentFile", isFrozen: false);
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var typeDatabase = new TypeDatabase();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension Swift.Int : AppIntents._IntentValue {}
            extension Foundation.Date : AppIntents._IntentValue {}
            extension AppIntents.IntentFile : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, typeDatabase, NullLogger.Instance);

            var protocolName = SwiftTypeName.FromModuleQualifiedName("AppIntents._IntentValue");
            // Foreign conformers land in the fact table, not on any local decl.
            Assert.True(typeDatabase.HasStrippedConformance(
                SwiftTypeName.FromModuleQualifiedName("Swift.Int"), protocolName));
            Assert.True(typeDatabase.HasStrippedConformance(
                SwiftTypeName.FromModuleQualifiedName("Foundation.Date"), protocolName));
            // The local conformer is attached to its decl, NOT the foreign fact table.
            Assert.Contains(intentFile.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
            Assert.False(typeDatabase.HasStrippedConformance(intentFile.SwiftTypeName, protocolName));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_AttachesStrippedConformance_ToLocalNestedConformer()
    {
        // Nested conformers live under their parent's Types list and are written dotted in the
        // swiftinterface — either fully qualified (AppIntents.Outer.Inner) or module-relative
        // (Outer.Inner). Both forms must resolve against the recursively-built index so a nested
        // local reference type regains the stripped conformance just like a top-level one.
        var module = CreateEmptyModule("AppIntents");
        var outerQualified = AddClass(module, "OuterQualified");
        var innerQualified = AddNestedClass(outerQualified, module, "Inner");
        var outerRelative = AddClass(module, "OuterRelative");
        var innerRelative = AddNestedClass(outerRelative, module, "Inner");
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension AppIntents.OuterQualified.Inner : AppIntents._IntentValue {}
            extension OuterRelative.Inner : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Contains(innerQualified.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
            Assert.Contains(innerRelative.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synthesize_StrippedConformanceIsIdempotent()
    {
        // A conformer that appears under a multi-protocol extension list must not gain a
        // duplicate _IntentValue conformance.
        var module = CreateEmptyModule("AppIntents");
        var intentFile = AddStruct(module, "IntentFile", isFrozen: false);
        var moduleTypes = new Dictionary<NamedTypeSpec, TypeDecl>();
        var path = WriteInterface(
            """
            public protocol _IntentValue { associatedtype Value }
            extension AppIntents.IntentFile : Swift.Sendable, AppIntents._IntentValue {}
            extension AppIntents.IntentFile : AppIntents._IntentValue {}
            """);

        try
        {
            UnderscoreProtocolSynthesizer.Synthesize(
                "AppIntents", path, module, moduleTypes, new TypeDatabase(), NullLogger.Instance);

            Assert.Single(intentFile.Conformances, c => c.Protocol.ModuleQualifiedName == "AppIntents._IntentValue");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Gap B: decoupling synthesized names from the internal-type-name set ----

    [Fact]
    public void MergeSuppressed_ExcludesSynthesizedButKeepsGenuineInternal()
    {
        // The decoupling contract: a synthesized public-underscore protocol
        // (AppIntents._IntentValue) must NOT enter the internal-reach set — otherwise the
        // member-reach gate suppresses every member whose generic constraint names it. A
        // genuinely module-internal underscore type still flows through and suppresses.
        var suppressed = new[] { "AppIntents._IntentValue", "AppIntents._GenuinelyInternal" };
        var synthesized = new HashSet<string>(StringComparer.Ordinal) { "AppIntents._IntentValue" };

        var merged = UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            internalTypeNames: null, suppressed, synthesized);

        Assert.NotNull(merged);
        Assert.DoesNotContain("AppIntents._IntentValue", merged!);
        Assert.Contains("AppIntents._GenuinelyInternal", merged);
    }

    [Fact]
    public void MergeSuppressed_PreservesPreexistingInternalNames()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "AppIntents.AlreadyInternal" };
        var suppressed = new[] { "AppIntents._IntentValue", "AppIntents._RealInternal" };
        var synthesized = new HashSet<string>(StringComparer.Ordinal) { "AppIntents._IntentValue" };

        var merged = UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            existing, suppressed, synthesized);

        Assert.Same(existing, merged); // mutated in place so decl.InternalTypeNames re-sync is correct.
        Assert.NotNull(merged);
        Assert.Contains("AppIntents.AlreadyInternal", merged!);
        Assert.Contains("AppIntents._RealInternal", merged!);
        Assert.DoesNotContain("AppIntents._IntentValue", merged!);
    }

    [Fact]
    public void MergeSuppressed_EmptySuppressedSet_ReturnsInputUnchanged()
    {
        var synthesized = new HashSet<string>(StringComparer.Ordinal) { "AppIntents._IntentValue" };

        // Null passthrough: nothing to merge means no allocation forced.
        Assert.Null(UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            internalTypeNames: null, Array.Empty<string>(), synthesized));

        var existing = new HashSet<string>(StringComparer.Ordinal) { "AppIntents.AlreadyInternal" };
        Assert.Same(existing, UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            existing, Array.Empty<string>(), synthesized));
    }

    [Fact]
    public void MergeSuppressed_OnlySynthesizedSuppressed_ReturnsInputUntouched()
    {
        // When the only suppressed name is the synthesized protocol, nothing is added: the
        // protocol stays reachable from generated wrappers and the original input is returned
        // untouched — no set is allocated for a null input, and an existing set is not mutated.
        var synthesized = new HashSet<string>(StringComparer.Ordinal) { "AppIntents._IntentValue" };

        // Null input + only-synthesized => null passthrough (no forced allocation).
        Assert.Null(UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            internalTypeNames: null, new[] { "AppIntents._IntentValue" }, synthesized));

        // Existing set + only-synthesized => same instance, unchanged contents.
        var existing = new HashSet<string>(StringComparer.Ordinal) { "AppIntents.AlreadyInternal" };
        var merged = UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
            existing, new[] { "AppIntents._IntentValue" }, synthesized);
        Assert.Same(existing, merged);
        Assert.DoesNotContain("AppIntents._IntentValue", merged!);
        Assert.Contains("AppIntents.AlreadyInternal", merged!);
    }

    private static StructDecl AddStruct(ModuleDecl module, string name, bool isFrozen)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
            MangledName = $"$sFake{name}V",
            IsFrozen = isFrozen,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = string.Empty,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(decl);
        return decl;
    }

    private static ClassDecl AddClass(ModuleDecl module, string name)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
            MangledName = $"$sFake{name}C",
            Conformances = new List<TypeConformance>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(decl);
        return decl;
    }

    private static ClassDecl AddNestedClass(TypeDecl parent, ModuleDecl module, string name)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{parent.SwiftTypeName.ModuleQualifiedName}.{name}"),
            MangledName = $"$sFake{name}C",
            Conformances = new List<TypeConformance>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
        };
        parent.Types.Add(decl);
        return decl;
    }

    private static ModuleDecl CreateEmptyModule(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static string WriteInterface(string body)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"underscore-synth-test-{Guid.NewGuid():N}.swiftinterface");
        File.WriteAllText(path, body);
        return path;
    }
}
