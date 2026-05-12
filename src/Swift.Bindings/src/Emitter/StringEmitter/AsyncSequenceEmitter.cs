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
        if (!handler.TryResolveElementCSharpType(typeDecl, out var elementCSharpType, out var isElementOptional))
            return false;

        // The emitted body delegates through a private async-iterator helper:
        // a method that returns IAsyncEnumerator<T> directly cannot use
        // `yield return`, so we route the actual enumeration through an
        // IAsyncEnumerable<T> helper and call its GetAsyncEnumerator.
        //
        // The Swift-side iterator (MakeAsyncIterator → NextAsync(ct)) returns
        // Task<Element?>. The trailing-null sentinel terminates iteration; the
        // `is { } element` pattern matches both reference- and value-typed
        // Element. When Element is itself Optional, NextAsync returns
        // Task<SwiftOptional<SwiftOptional<T>>> and the bridge takes the
        // nested-Optional branch that distinguishes outer "done" None from
        // inner "element is null" None. The using-block disposes the Swift
        // iterator's SafeHandle payload deterministically — even when the
        // consumer breaks out of the foreach early.
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
        if (isElementOptional)
        {
            // Nested-Optional shape: AsyncSequence's Element is itself an Optional,
            // so Swift's `next() -> Element?` returns `Optional<Optional<T>>` which
            // projects to `Task<SwiftOptional<SwiftOptional<T>>>` — the two layers
            // can't collapse to a single C# nullable without losing the iteration
            // terminator. Walk the layers explicitly: outer None ends iteration,
            // outer Some unwraps to the inner Optional (which becomes T? via
            // SwiftOptional's implicit operator) for yield-return.
            //
            // Both SwiftOptional wrappers (outer + inner) own native payload buffers
            // and implement IDisposable. The implicit operator on the inner wrapper
            // reads .Some by value (class refs are independently ARC-retained,
            // value-type Element is copied into the result), so disposing AFTER
            // yield is safe and prevents per-iteration buffer leaks. yield break
            // still runs the pending finally blocks in C#'s async iterator state
            // machine — the outer wrapper is disposed even on iteration end.
            csWriter.WriteLine("while (true)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var __sbAsyncOuter = await iter.NextAsync(cancellationToken).ConfigureAwait(false);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (!__sbAsyncOuter.HasValue) yield break;");
            csWriter.WriteLine("var __sbAsyncInner = __sbAsyncOuter.Some;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("yield return __sbAsyncInner;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("__sbAsyncInner?.Dispose();");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("__sbAsyncOuter?.Dispose();");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else
        {
            csWriter.WriteLine("while (await iter.NextAsync(cancellationToken).ConfigureAwait(false) is { } __sbAsyncElement)");
            csWriter.WriteLine("    yield return __sbAsyncElement;");
        }
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
