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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "SomeRandomFramework", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
            module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
                "AppIntents", path, module, moduleTypes, NullLogger.Instance);

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
