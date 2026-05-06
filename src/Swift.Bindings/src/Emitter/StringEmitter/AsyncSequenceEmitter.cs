// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits the .NET <c>IAsyncEnumerable&lt;T&gt;</c> bridge for Swift types that
/// conform to <c>_Concurrency.AsyncSequence</c>. The interface adoption is
/// added by <see cref="ProtocolConformanceHelper.GetImplementedInterfaces"/>;
/// this helper writes the matching <c>GetAsyncEnumerator</c> body so the
/// emitted type satisfies the interface and consumers can use the canonical
/// <c>await foreach (var x in seq) { ... }</c> pattern instead of hand-rolling
/// <c>MakeAsyncIterator()</c>/<c>NextAsync()</c>.
/// </summary>
internal static class AsyncSequenceEmitter
{
    /// <summary>
    /// Writes the <c>GetAsyncEnumerator</c> method and a private async-iterator
    /// helper to <paramref name="csWriter"/> when <paramref name="typeDecl"/>
    /// conforms to AsyncSequence and its Element type can be resolved.
    /// </summary>
    /// <returns>True when the bridge was emitted.</returns>
    public static bool TryEmitAsyncEnumerableBridge(
        CSharpWriter csWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase)
    {
        if (!AsyncSequenceHandler.IsAsyncSequence(typeDecl))
            return false;

        var handler = new AsyncSequenceHandler(typeDatabase);
        if (!handler.TryResolveElementCSharpType(typeDecl, out var elementCSharpType))
            return false;

        // The emitted body delegates through a private async-iterator helper:
        // a method that returns IAsyncEnumerator<T> directly cannot use
        // `yield return`, so we route the actual enumeration through an
        // IAsyncEnumerable<T> helper and call its GetAsyncEnumerator.
        //
        // The Swift-side iterator (MakeAsyncIterator → NextAsync(ct)) returns
        // Task<Element?>. The trailing-null sentinel terminates iteration; the
        // `is { } element` pattern matches both reference- and value-typed
        // Element. The using-block disposes the Swift iterator's SafeHandle
        // payload deterministically — even when the consumer breaks out of
        // the foreach early.
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>");
        csWriter.WriteLine("/// Returns an asynchronous enumerator that adapts the Swift");
        csWriter.WriteLine("/// AsyncSequence's <c>MakeAsyncIterator()</c> + <c>NextAsync()</c>");
        csWriter.WriteLine("/// shape to <c>IAsyncEnumerator&lt;T&gt;</c>. Enables");
        csWriter.WriteLine("/// <c>await foreach (var x in seq)</c>.");
        csWriter.WriteLine("/// </summary>");
        csWriter.WriteLine($"public global::System.Collections.Generic.IAsyncEnumerator<{elementCSharpType}> GetAsyncEnumerator(global::System.Threading.CancellationToken cancellationToken = default)");
        csWriter.WriteLine($"    => __SbAsyncSequenceImpl(cancellationToken).GetAsyncEnumerator(cancellationToken);");
        csWriter.WriteLine();
        csWriter.WriteLine($"private async global::System.Collections.Generic.IAsyncEnumerable<{elementCSharpType}> __SbAsyncSequenceImpl(");
        csWriter.WriteLine("    [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken = default)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Some iterator types are projected as C# struct (no IDisposable surface)
        // and others as ref-typed classes (have Dispose). `using var` works for
        // both: structs are disposed by the compiler-generated try/finally only
        // when they implement IDisposable, otherwise the `using` is a no-op.
        // For value types that don't, the iterator simply goes out of scope.
        csWriter.WriteLine("var iter = MakeAsyncIterator();");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("while (await iter.NextAsync(cancellationToken).ConfigureAwait(false) is { } __sbAsyncElement)");
        csWriter.WriteLine("    yield return __sbAsyncElement;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (iter is global::System.IDisposable __sbAsyncIterDisposable)");
        csWriter.WriteLine("    __sbAsyncIterDisposable.Dispose();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        return true;
    }
}
