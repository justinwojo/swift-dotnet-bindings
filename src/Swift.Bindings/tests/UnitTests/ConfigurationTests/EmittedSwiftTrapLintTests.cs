// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Unit tests for the F40 trap-anonymity lint (<see cref="EmittedSwiftTrapLint"/>). The lint is
    /// read-only: it verifies that every emitted Swift trap carries the <c>[SwiftBindings]</c>
    /// breadcrumb and reports the force-cast surface, but does not rewrite output.
    /// </summary>
    public class EmittedSwiftTrapLintTests
    {
        [Fact]
        public void Inspect_PrefixedFatalError_ReportsNoUnprefixedTrap()
        {
            var swift = "func f() {\n    fatalError(\"[SwiftBindings] bad state\")\n}";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Empty(result.UnprefixedTraps);
        }

        [Fact]
        public void Inspect_BareFatalError_FlagsUnprefixedTrap()
        {
            var swift = "func f() {\n    fatalError(\"bad state\")\n}";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            var trap = Assert.Single(result.UnprefixedTraps);
            Assert.Equal(2, trap.LineNumber);
        }

        [Fact]
        public void Inspect_BarePreconditionFailure_FlagsUnprefixedTrap()
        {
            var swift = "preconditionFailure(\"unreachable\")";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Single(result.UnprefixedTraps);
        }

        [Fact]
        public void Inspect_PrefixedPreconditionFailure_ReportsNoUnprefixedTrap()
        {
            var swift = "preconditionFailure(\"[SwiftBindings] unreachable\")";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Empty(result.UnprefixedTraps);
        }

        [Fact]
        public void Inspect_ForceCast_CountsForceCast()
        {
            var swift = "let x = a as! Foo\nlet y = b as! Bar";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Equal(2, result.ForceCastCount);
        }

        [Fact]
        public void Inspect_OptionalCastAndTypeAlias_NotCountedAsForceCast()
        {
            // `as?` is a safe cast (no trap); `class!` etc. has no `as` token. Only `as!` counts.
            var swift = "let x = a as? Foo\nlet y: Int = 3";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Equal(0, result.ForceCastCount);
        }

        [Fact]
        public void Inspect_TrapInComment_NotFlagged()
        {
            // A commented-out trap or one merely mentioned in a comment must not be counted.
            var swift = "// historically this called fatalError(\"old\") and used x as! Y";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Empty(result.UnprefixedTraps);
            Assert.Equal(0, result.ForceCastCount);
        }

        [Fact]
        public void Inspect_NonLiteralTrapArgument_NotFlagged()
        {
            // A trap whose argument is not a string literal cannot carry a prefix and is not flagged.
            var swift = "fatalError(message)";
            var result = EmittedSwiftTrapLint.Inspect(swift);
            Assert.Empty(result.UnprefixedTraps);
        }

        [Fact]
        public void Inspect_EmptyOrNull_ReturnsEmptyResult()
        {
            var result = EmittedSwiftTrapLint.Inspect("");
            Assert.Empty(result.UnprefixedTraps);
            Assert.Equal(0, result.ForceCastCount);
        }
    }
}
