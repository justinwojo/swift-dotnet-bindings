// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Tests;
using Swift.Runtime;
using Xunit;

namespace LibraryTests
{
    public class SwiftEquatableTests
    {
        [Theory]
        [InlineData(42L, 42L, true)]
        [InlineData(42L, 100L, false)]
        [InlineData(0x1_0000_002AL, 0x1_0000_002AL, true)]
        [InlineData(42L, 0x1_0000_002AL, false)]
        [InlineData(-1L, 0xFFFF_FFFFL, false)]
        public void SwiftIntMock_Equals_UsesFullSwiftIntValue(long lhs, long rhs, bool expected)
        {
            var left = new SwiftIntMock(checked((nint)lhs));
            var right = new SwiftIntMock(checked((nint)rhs));

            Assert.Equal(expected, SwiftEquatable.Equals(left, right));
        }
    }
}
