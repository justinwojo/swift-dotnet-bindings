// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Unit tests for <see cref="StructuralBraceScanner"/>, the shared lexer the brace-walking
    /// block finders rely on. The central guarantees: braces inside string/char literals and
    /// comments are NOT structural, block-comment nesting carries across lines, and on input with
    /// no braces inside strings/comments the scan is byte-for-byte identical to a raw <c>{</c>/<c>}</c>
    /// count (so the existing callers' behavior on clean generated source is preserved exactly).
    /// </summary>
    public class StructuralBraceScannerTests
    {
        private static int NetDelta(string line)
        {
            int bcd = 0;
            bool saw = false;
            return StructuralBraceScanner.NetLineDelta(line, ref bcd, ref saw);
        }

        private static (int delta, bool sawOpen) Scan(string line)
        {
            int bcd = 0;
            bool saw = false;
            int delta = StructuralBraceScanner.NetLineDelta(line, ref bcd, ref saw);
            return (delta, saw);
        }

        [Theory]
        [InlineData("func f() {", 1, true)]
        [InlineData("}", -1, false)]
        [InlineData("{ }", 0, true)]
        [InlineData("{ { }", 1, true)]
        [InlineData("} }", -2, false)]
        [InlineData("    return 0", 0, false)]
        public void CleanInput_MatchesRawBraceCount(string line, int expectedDelta, bool expectedSawOpen)
        {
            var (delta, sawOpen) = Scan(line);
            Assert.Equal(expectedDelta, delta);
            Assert.Equal(expectedSawOpen, sawOpen);
        }

        [Fact]
        public void BraceInsideStringLiteral_IsNotStructural()
        {
            // A Swift default value like `prefix: String = "}"` — the closing brace lives in a string.
            Assert.Equal(0, NetDelta("let suffix = \"}\""));
            Assert.Equal(1, NetDelta("func f(prefix: String = \"}\") {"));
            // An opening brace inside a string must not count either.
            Assert.Equal(0, NetDelta("let s = \"{\""));
        }

        [Fact]
        public void BraceInsideCharLiteral_IsNotStructural()
        {
            Assert.Equal(0, NetDelta("let c = '}'"));
            Assert.Equal(0, NetDelta("let c = '{'"));
        }

        [Fact]
        public void EscapedQuoteInsideString_DoesNotEndStringEarly()
        {
            // The escaped quote must not terminate the string, so the trailing brace stays in-string.
            Assert.Equal(0, NetDelta("let s = \"a\\\"}\""));
        }

        [Fact]
        public void BraceInsideLineComment_IsNotStructural()
        {
            Assert.Equal(0, NetDelta("doThing() // closes the } here"));
            // A real opening brace BEFORE the comment still counts.
            Assert.Equal(1, NetDelta("if x { // trailing } in comment"));
        }

        [Fact]
        public void BraceInsideBlockComment_IsNotStructural()
        {
            Assert.Equal(0, NetDelta("/* } { } */"));
            Assert.Equal(1, NetDelta("foo() { /* } */"));
        }

        [Fact]
        public void BlockCommentNestingCarriesAcrossLines()
        {
            // A block comment opened on one line and closed on a later line must suppress braces
            // on the lines in between.
            int bcd = 0;
            bool saw = false;
            var lines = new[]
            {
                "func f() {",       // +1, structural open
                "    /* opening",   //  0, enters block comment
                "    } still in comment", //  0, brace suppressed
                "    end */ }",     // -1, exits comment then a real close
            };
            int total = 0;
            foreach (var line in lines)
                total += StructuralBraceScanner.NetLineDelta(line, ref bcd, ref saw);
            Assert.Equal(0, total);
            Assert.Equal(0, bcd); // comment fully closed
            Assert.True(saw);     // the structural '{' on line 1 was seen
        }

        [Fact]
        public void NestedBlockComment_RequiresBothClosersBeforeBracesCountAgain()
        {
            // Swift permits nested block comments: /* /* */ */. Braces stay suppressed until the
            // OUTER comment closes.
            int bcd = 0;
            bool saw = false;
            Assert.Equal(0, StructuralBraceScanner.NetLineDelta("/* /* } */ }", ref bcd, ref saw));
            Assert.Equal(1, bcd); // still inside the outer comment (inner */ only dropped depth 2→1)
            // The leading brace on this continuation line is still suppressed (outer comment still open);
            // once the outer */ appears, the trailing '{' is structural again (+1).
            Assert.Equal(1, StructuralBraceScanner.NetLineDelta("} still nested */ {", ref bcd, ref saw));
            Assert.Equal(0, bcd);
        }

        [Fact]
        public void ScanLine_ReportsBracesInSourceOrder()
        {
            int bcd = 0;
            var deltas = new List<int>();
            StructuralBraceScanner.ScanLine("{ \"}\" } {", ref bcd, deltas.Add);
            // Open, (string brace skipped), close, open — in order.
            Assert.Equal(new[] { 1, -1, 1 }, deltas);
        }
    }
}
