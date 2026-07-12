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
