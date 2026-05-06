// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detects and projects Swift types that conform to <c>_Concurrency.AsyncSequence</c>
/// into the .NET <c>IAsyncEnumerable&lt;T&gt;</c> contract. Without this projection
/// consumers of any AsyncSequence-shaped binding (StoreKit Transaction.Updates,
/// MusicKit MusicSubscription.Updates, Stripe progress observers) cannot use
/// the canonical <c>await foreach</c> pattern and have to hand-roll a
/// <c>MakeAsyncIterator()</c>/<c>NextAsync()</c> loop instead.
/// </summary>
public sealed class AsyncSequenceHandler
{
    /// <summary>
    /// Module-qualified names the parser may surface for AsyncSequence. The
    /// _Concurrency-module form is the canonical Swift name; "Swift.AsyncSequence"
    /// is the umbrella-module rewriting used by the type database (see
    /// SwiftBindingsTestLibDatabase.xml entries with
    /// <c>protocolConformances="...,Swift.AsyncSequence,..."</c>).
    /// </summary>
    public static readonly string[] AsyncSequenceProtocolNames =
    {
        "_Concurrency.AsyncSequence",
        "Swift.AsyncSequence",
    };

    /// <summary>Module-qualified names for the AsyncIteratorProtocol.</summary>
    public static readonly string[] AsyncIteratorProtocolNames =
    {
        "_Concurrency.AsyncIteratorProtocol",
        "Swift.AsyncIteratorProtocol",
    };

    private readonly ITypeDatabase _typeDatabase;

    public AsyncSequenceHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Returns true when <paramref name="typeDecl"/> declares (or inherits) a
    /// conformance to Swift's <c>AsyncSequence</c> protocol.
    /// </summary>
    public static bool IsAsyncSequence(TypeDecl typeDecl)
    {
        var conformances = GetConformances(typeDecl);
        foreach (var c in conformances)
        {
            foreach (var qualified in AsyncSequenceProtocolNames)
            {
                if (c.Protocol.ModuleQualifiedName == qualified)
                    return true;
            }
            if (c.Protocol.Name == "AsyncSequence" &&
                (string.IsNullOrEmpty(c.Protocol.Module) ||
                 c.Protocol.Module == "_Concurrency" ||
                 c.Protocol.Module == "Swift"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the AsyncSequence's <c>Element</c> by walking
    /// <c>makeAsyncIterator</c> → iterator type → iterator's <c>next</c> →
    /// <c>Optional&lt;Element&gt;</c>. Bails on any unexpected shape so callers
    /// can fall back to the legacy MakeAsyncIterator-only emit.
    /// </summary>
    /// <param name="typeDecl">The AsyncSequence-conforming type.</param>
    /// <param name="elementCSharpType">Receives the C# Element type name (already qualified) on success.</param>
    /// <returns>True when Element was resolved; false when projection should be skipped.</returns>
    public bool TryResolveElementCSharpType(TypeDecl typeDecl, out string elementCSharpType)
    {
        elementCSharpType = "";

        if (!IsAsyncSequence(typeDecl))
            return false;

        // Step 1: find makeAsyncIterator on the AsyncSequence type itself.
        // The Swift name in the parser model is the lowercased original
        // (`makeAsyncIterator`); accept the PascalCased C# variant too in case
        // the parser ever feeds us already-renamed methods.
        var makeIter = typeDecl.Methods.FirstOrDefault(m =>
            !m.IsConstructor &&
            m.CSSignature.Count >= 1 &&
            (m.Name == "makeAsyncIterator" || m.Name == "MakeAsyncIterator"));
        if (makeIter == null)
            return false;

        // Step 2: the iterator's TypeSpec is the return type of makeAsyncIterator.
        var iteratorSpec = makeIter.CSSignature[0].SwiftTypeSpec;
        if (iteratorSpec is not NamedTypeSpec iteratorNamed)
            return false;

        // Step 3: locate the iterator decl. The dominant real-world shape (StoreKit
        // Transactions, MusicKit MusicSubscription.Updates, Stripe progress
        // observers) nests the iterator directly under the AsyncSequence type.
        var iteratorDecl = FindIteratorDecl(typeDecl, iteratorNamed);
        if (iteratorDecl == null)
            return false;

        // Step 4: locate the iterator's `next` method.
        var nextMethod = iteratorDecl.Methods.FirstOrDefault(m =>
            !m.IsConstructor &&
            m.CSSignature.Count >= 1 &&
            (m.Name == "next" || m.Name == "Next"));
        if (nextMethod == null)
            return false;

        // Step 5: unwrap the Optional<Element> return type.
        var nextReturnSpec = nextMethod.CSSignature[0].SwiftTypeSpec;
        if (nextReturnSpec is not NamedTypeSpec optNamed)
            return false;
        if (!IsSwiftOptional(optNamed) || optNamed.GenericParameters.Count != 1)
            return false;

        var elementSpec = optNamed.GenericParameters[0];

        // Step 6: project Element to a C# type via the type database. Reuse the
        // same translator that AsyncStreamHandler uses so generic parameters,
        // pointer types, and unknown types fall back to the same conventions.
        elementCSharpType = TranslateElementTypeToCSharp(elementSpec);
        return !string.IsNullOrEmpty(elementCSharpType) && elementCSharpType != "object";
    }

    private static IEnumerable<TypeConformance> GetConformances(TypeDecl typeDecl)
    {
        return typeDecl switch
        {
            ClassDecl classDecl => classDecl.Conformances,
            StructDecl structDecl => structDecl.Conformances,
            EnumDecl enumDecl => enumDecl.Conformances,
            _ => Enumerable.Empty<TypeConformance>(),
        };
    }

    private static bool IsSwiftOptional(NamedTypeSpec named)
    {
        return named.Name == "Swift.Optional" || named.Name == "Optional";
    }

    private static TypeDecl? FindIteratorDecl(TypeDecl asyncSequenceType, NamedTypeSpec iteratorNamed)
    {
        // Match by full Swift name first (handles modules-qualified spec).
        var direct = asyncSequenceType.Types.FirstOrDefault(t =>
            t.SwiftTypeName.ModuleQualifiedName == iteratorNamed.Name ||
            $"{asyncSequenceType.SwiftTypeName.ModuleQualifiedName}.{t.Name}" == iteratorNamed.Name ||
            t.Name == iteratorNamed.Name);
        if (direct != null)
            return direct;

        // Strip the leading "Module." or parent path and try the trailing simple name.
        var lastDot = iteratorNamed.Name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < iteratorNamed.Name.Length - 1)
        {
            var simpleName = iteratorNamed.Name.Substring(lastDot + 1);
            var byName = asyncSequenceType.Types.FirstOrDefault(t => t.Name == simpleName);
            if (byName != null)
                return byName;
        }

        // Recurse into nested types (covers iterator-nested-deeper shapes).
        foreach (var nested in asyncSequenceType.Types)
        {
            var deep = FindIteratorDecl(nested, iteratorNamed);
            if (deep != null)
                return deep;
        }

        return null;
    }

    private string TranslateElementTypeToCSharp(TypeSpec typeSpec)
    {
        // Prefer the public projected type for well-known stdlib shapes: Swift.String → string,
        // Foundation.Data → byte[], Foundation.Date → double, etc. The iterator's NextAsync
        // signature uses these projections (EmitAsyncWrapperForString detects Swift.String at
        // the unwrapped Optional inner spec and emits Task<string?>), so the IAsyncEnumerable<T>
        // bridge MUST use the same surface — otherwise the `yield return` of the projected value
        // into a raw-typed IAsyncEnumerable<Swift.SwiftString> fails CS0029 at compile time.
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(namedTypeSpec, new ProjectionContext
            {
                TypeDatabase = _typeDatabase,
                IsParameter = false,
            });
            if (projection != null && !string.IsNullOrEmpty(projection.PublicType))
            {
                return projection.PublicType;
            }
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            if (_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            {
                var baseName = typeRecord.CSharpTypeName.FullyQualifiedName;

                if (typeRecord == TypeDatabaseExtensions.IntPtrType)
                    return baseName;

                if (namedType.GenericParameters.Count > 0)
                {
                    var translatedParams = namedType.GenericParameters
                        .Select(TranslateElementTypeToCSharp)
                        .ToList();
                    return $"{baseName}<{string.Join(", ", translatedParams)}>";
                }

                return baseName;
            }
        }
        return "object";
    }
}
