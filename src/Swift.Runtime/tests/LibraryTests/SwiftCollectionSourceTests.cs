// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The element conversions a binding's C#-built Swift collections read their source through. A
/// collection's builder copies each converted element before it asks for the next, so a conversion
/// may rely on the element it was given staying usable until then, and a temporary it built may be
/// disposed as soon as the builder moves on.
/// </summary>
public class SwiftCollectionSourceTests
{
    private sealed class Token : IDisposable
    {
        public Token(int value) => Value = value;
        public int Value { get; }
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    // A source that owns each element only while it is current, as `using var x = …; yield return x;` does.
    private static IEnumerable<Token> ScopedTokens(int count, List<Token> created)
    {
        for (int i = 0; i < count; i++)
        {
            using var token = new Token(i);
            created.Add(token);
            yield return token;
        }
    }

    [Fact]
    public void Borrowing_ConvertsEachElementWhileTheSourceStillHoldsIt()
    {
        // The source is read one element at a time: materializing it first would hand the conversion
        // elements their source has already disposed.
        var created = new List<Token>();
        var converted = new List<int>();
        foreach (var value in SwiftCollectionSource.Borrowing(ScopedTokens(3, created), token =>
        {
            Assert.False(token.IsDisposed, "the conversion sees the element its source still holds");
            return token.Value;
        }))
        {
            converted.Add(value);
        }

        Assert.Equal(new[] { 0, 1, 2 }, converted);
        Assert.All(created, token => Assert.True(token.IsDisposed));
    }

    [Fact]
    public void Consuming_DisposesEachValueOnceTheReaderMovesOn()
    {
        var built = new List<Token>();
        using var reader = SwiftCollectionSource.Consuming(new[] { 10, 20, 30 }, value =>
        {
            var token = new Token(value);
            built.Add(token);
            return token;
        }).GetEnumerator();

        Assert.True(reader.MoveNext());
        Assert.False(reader.Current.IsDisposed, "the current value is the reader's to copy");
        var first = reader.Current;

        Assert.True(reader.MoveNext());
        Assert.True(first.IsDisposed, "a value the reader moved past was copied and is disposed");
        Assert.False(reader.Current.IsDisposed);

        Assert.True(reader.MoveNext());
        Assert.False(reader.MoveNext());
        Assert.All(built, token => Assert.True(token.IsDisposed));
    }

    [Fact]
    public void Consuming_DisposesTheCurrentValueWhenTheReaderStops()
    {
        // A builder that throws partway abandons the sequence; the value it held is disposed with it.
        var built = new List<Token>();
        var reader = SwiftCollectionSource.Consuming(new[] { 1, 2, 3 }, value =>
        {
            var token = new Token(value);
            built.Add(token);
            return token;
        }).GetEnumerator();

        Assert.True(reader.MoveNext());
        reader.Dispose();

        var only = Assert.Single(built);
        Assert.True(only.IsDisposed);
    }

    [Fact]
    public void Conversions_RefuseANullSourceWhenCalled()
    {
        Assert.Throws<ArgumentNullException>(() => SwiftCollectionSource.Borrowing<int, int>(null!, v => v));
        Assert.Throws<ArgumentNullException>(() => SwiftCollectionSource.Consuming<int, Token>(null!, v => new Token(v)));
    }
}
