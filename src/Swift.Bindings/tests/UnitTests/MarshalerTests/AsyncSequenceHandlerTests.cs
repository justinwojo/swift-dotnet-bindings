// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace BindingsGeneration.Tests;

using System.Diagnostics.CodeAnalysis;
using Xunit;

public class AsyncSequenceHandlerTests
{
    [Fact]
    public void TryResolveElementCSharpType_NestedOptionalElement_ReportsElementOptional()
    {
        // An async sequence with Element=User? — its iterator's next() therefore returns
        // Optional<Optional<User>>. The emitter needs to distinguish the outer "iteration
        // done" None from the inner "element is null" None, so the handler must surface
        // isElementOptional=true.
        var asyncSeq = BuildAsyncSequence(
            elementSpec: new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int")));
        var handler = new AsyncSequenceHandler(new StubDb());

        Assert.True(handler.TryResolveElementCSharpType(asyncSeq, out _, out var isElementOptional));
        Assert.True(isElementOptional);
    }

    [Fact]
    public void TryResolveElementCSharpType_NonOptionalElement_ReportsNotOptional()
    {
        // StoreKit Transaction.Updates has Element=VerificationResult<Transaction>
        // (non-optional) — next() returns Optional<VerificationResult<...>>, and
        // the emitter should take the single-Optional fast-path with `is { }`.
        var asyncSeq = BuildAsyncSequence(elementSpec: new NamedTypeSpec("Swift.Int"));
        var handler = new AsyncSequenceHandler(new StubDb());

        Assert.True(handler.TryResolveElementCSharpType(asyncSeq, out _, out var isElementOptional));
        Assert.False(isElementOptional);
    }

    [Fact]
    public void TryResolveElementCSharpType_NoAsyncSequenceConformance_ReturnsFalse()
    {
        var notAsyncSeq = new StructDecl
        {
            Name = "Plain",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.Plain"),
            MangledName = "$s1M5PlainV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = null,
        };
        var handler = new AsyncSequenceHandler(new StubDb());

        Assert.False(handler.TryResolveElementCSharpType(notAsyncSeq, out _, out _));
    }

    [Fact]
    public void TryHideRawIteratorSurface_ResolvableElement_DemotesMakeAsyncIterator()
    {
        // When the Element gate succeeds, the IAsyncEnumerable<T> bridge is emitted, so the
        // raw makeAsyncIterator factory is demoted to [EditorBrowsable(Never)] — consumers
        // get the idiomatic await-foreach surface and the raw factory no longer clutters
        // IntelliSense (it stays public + callable).
        var asyncSeq = BuildAsyncSequence(elementSpec: new NamedTypeSpec("Swift.Int"));
        var makeIter = asyncSeq.Methods.Single(m => m.Name == "makeAsyncIterator");
        Assert.False(makeIter.HideRawAsyncIteratorSurface); // precondition: default off

        AsyncSequenceEmitter.TryHideRawIteratorSurface(asyncSeq, new StubDb());

        Assert.True(makeIter.HideRawAsyncIteratorSurface);
    }

    [Fact]
    public void TryHideRawIteratorSurface_UnprojectableElement_KeepsMakeAsyncIteratorVisible()
    {
        // An Element that does not project to a real C# type (falls back to "object")
        // emits NO bridge — so the raw makeAsyncIterator MUST stay visible as the only
        // way to consume the sequence. Gating the demotion on the SAME predicate the
        // bridge uses keeps the two from disagreeing.
        var asyncSeq = BuildAsyncSequence(elementSpec: new NamedTypeSpec("M.SelfTyped"));
        var makeIter = asyncSeq.Methods.Single(m => m.Name == "makeAsyncIterator");

        AsyncSequenceEmitter.TryHideRawIteratorSurface(asyncSeq, new StubDb());

        Assert.False(makeIter.HideRawAsyncIteratorSurface);
    }

    [Fact]
    public void TryResolveElementCSharpType_SkippedElement_DropsBridge()
    {
        // The Element type projects to a real C# type (Swift.Int -> nint), so absent the
        // skip gate the handler would emit IAsyncEnumerable<nint>. Once the ingestion-
        // quarantine proven-closure walk (or any type-skip) withdraws the Element, that type
        // is never declared and the bridge would reference a non-existent C# type. The handler
        // must drop the whole bridge, mirroring "if a type is skipped, every use of it is too".
        var asyncSeq = BuildAsyncSequence(elementSpec: new NamedTypeSpec("Swift.Int"));
        var handler = new AsyncSequenceHandler(new StubDb());

        WithSkippedType("Swift.Int", () =>
            Assert.False(handler.TryResolveElementCSharpType(asyncSeq, out _, out _)));
    }

    [Fact]
    public void TryResolveElementCSharpType_SkippedTypeInsideGenericElement_DropsBridge()
    {
        // The withdrawal check recurses into generic arguments: an Element of Array<Swift.Int>
        // whose inner Swift.Int was withdrawn must also drop the bridge, since the emitted
        // IAsyncEnumerable<Array<...>> still names the withdrawn type in its argument list.
        var asyncSeq = BuildAsyncSequence(
            elementSpec: new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int")));
        var handler = new AsyncSequenceHandler(new StubDb());

        WithSkippedType("Swift.Int", () =>
            Assert.False(handler.TryResolveElementCSharpType(asyncSeq, out _, out _)));
    }

    [Fact]
    public void TryResolveElementCSharpType_UnskippedElement_KeepsBridge()
    {
        // Negative control: with a DIFFERENT type recorded as skipped, the Swift.Int Element
        // is untouched and the bridge is kept. The gate must not over-fire and suppress a
        // perfectly resolvable sequence just because SOME unrelated type was withdrawn.
        var asyncSeq = BuildAsyncSequence(elementSpec: new NamedTypeSpec("Swift.Int"));
        var handler = new AsyncSequenceHandler(new StubDb());

        WithSkippedType("M.SomethingElse", () =>
            Assert.True(handler.TryResolveElementCSharpType(asyncSeq, out _, out _)));
    }

    [Fact]
    public void SpecReferencesSkippedType_UmbrellaSpelledElement_MatchesSourceModuleSkip()
    {
        // The Element bridge delegates to ValidationRuleSet.SpecReferencesSkippedType (the same
        // umbrella-remap-aware oracle the member-signature gate uses). RealityFoundation declares
        // compileImportModule=RealityKit, so a type withdrawn under its source-module key
        // (RealityFoundation.Widget) but named via the umbrella (RealityKit.Widget) must still be
        // recognized as skipped — otherwise the IAsyncEnumerable<RealityKit.Widget> bridge would
        // reference a type the emitter never declares (CS0234).
        Assert.Contains("RealityFoundation",
            AppleFrameworkRegistry.GetCompileImportSourceModules("RealityKit"));

        WithSkippedType("RealityFoundation.Widget", () =>
        {
            Assert.True(ValidationRuleSet.SpecReferencesSkippedType(new NamedTypeSpec("RealityKit.Widget")));
            // Recurses into generic args under the umbrella spelling too.
            Assert.True(ValidationRuleSet.SpecReferencesSkippedType(
                new NamedTypeSpec("Swift.Array", new NamedTypeSpec("RealityKit.Widget"))));
            // Negative control: a sibling umbrella type is untouched.
            Assert.False(ValidationRuleSet.SpecReferencesSkippedType(new NamedTypeSpec("RealityKit.Other")));
        });
    }

    [Fact]
    public void SpecReferencesSkippedType_WithdrawnNestedTypeAfterGenericOuter_IsCaught()
    {
        // A nested type withdrawn under its full key "TestModule.Outer.Inner" is referenced as
        // "TestModule.Outer<T>.Inner" — a NamedTypeSpec whose Name is only "TestModule.Outer"
        // (the generic arg lives in GenericParameters) with the nested segment in InnerType.
        // Probing only Name would miss the skip and let the emitter name the withdrawn nested
        // type (CS0426). The oracle must walk the InnerType chain, reconstructing the
        // generics-stripped "TestModule.Outer.Inner" key TypeSkipPrePass records.
        WithSkippedType("TestModule.Outer.Inner", () =>
        {
            var nested = new NamedTypeSpec("TestModule.Outer", new NamedTypeSpec("T"))
            {
                InnerType = new NamedTypeSpec("Inner"),
            };
            Assert.True(ValidationRuleSet.SpecReferencesSkippedType(nested));

            // The generic outer alone is not withdrawn — only the nested type is.
            Assert.False(ValidationRuleSet.SpecReferencesSkippedType(
                new NamedTypeSpec("TestModule.Outer", new NamedTypeSpec("T"))));
            // A different nested segment on the same outer is untouched.
            Assert.False(ValidationRuleSet.SpecReferencesSkippedType(
                new NamedTypeSpec("TestModule.Outer", new NamedTypeSpec("T"))
                {
                    InnerType = new NamedTypeSpec("Other"),
                }));
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a ReportCollector session in which a single type
    /// (keyed by its module-qualified name) is recorded as skipped, then tears the session
    /// down. Mirrors the emitter's type-skip pre-pass so
    /// <see cref="ReportCollector.IsTypeSkipped(string)"/> returns true for that key.
    /// </summary>
    private static void WithSkippedType(string moduleQualifiedName, Action body)
    {
        var dot = moduleQualifiedName.IndexOf('.');
        var moduleName = dot >= 0 ? moduleQualifiedName[..dot] : moduleQualifiedName;
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
        };
        var typeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        var skipped = new StructDecl
        {
            Name = typeName.Name,
            SwiftTypeName = typeName,
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(skipped, SkipReason.Unknown);
            body();
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    private static StructDecl BuildAsyncSequence(NamedTypeSpec elementSpec)
    {
        // Iterator struct M.Updates.AsyncIterator with `next() -> Optional<Element>`.
        var iterator = new StructDecl
        {
            Name = "AsyncIterator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.Updates.AsyncIterator"),
            MangledName = "$s1M7UpdatesV13AsyncIteratorV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = null,
        };
        iterator.Methods.Add(new MethodDecl
        {
            Name = "next",
            MangledName = "$s1M7UpdatesV13AsyncIteratorV4nextxSgyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", elementSpec),
                        Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                        ParentDecl = null, ModuleDecl = null },
            },
            Throws = false,
            IsAsync = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = iterator,
            ModuleDecl = null,
        });

        var asyncSeq = new StructDecl
        {
            Name = "Updates",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.Updates"),
            MangledName = "$s1M7UpdatesV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { iterator },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName("M.Updates"),
                    SwiftTypeName.FromModuleQualifiedName("_Concurrency.AsyncSequence"),
                    "Mc"),
            },
            IsFrozen = false,
            MetadataAccessor = "",
            ParentDecl = null,
            ModuleDecl = null,
        };
        asyncSeq.Methods.Add(new MethodDecl
        {
            Name = "makeAsyncIterator",
            MangledName = "$s1M7UpdatesV17makeAsyncIteratorAB0E0VyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = new NamedTypeSpec("M.Updates.AsyncIterator"),
                        Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                        ParentDecl = null, ModuleDecl = null },
            },
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = asyncSeq,
            ModuleDecl = null,
        });
        iterator.ParentDecl = asyncSeq;
        return asyncSeq;
    }

    /// <summary>
    /// Type database stub seeded with Swift.Int so the handler's Element
    /// translation step ("Step 6") resolves to System.Int64 instead of falling
    /// back to "object" (which would short-circuit the return before our
    /// isElementOptional signal could be observed).
    /// </summary>
    private sealed class StubDb : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new()
        {
            ["Swift.Int"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            },
            ["Swift.Optional"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            },
        };

        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
