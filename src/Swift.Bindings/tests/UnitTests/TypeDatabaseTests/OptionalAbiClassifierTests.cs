// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Direct unit coverage for <see cref="OptionalAbiClassifier.HasAppendedOptionalTag"/> — the
/// shared ABI oracle the field-layout walk (ModuleProcessor.ClassifyFieldType) and the register
/// walk (TypeLowering.LowerOptional) both consult so they cannot drift (Regression-R6 finding 4).
/// The behavioral contract: an <c>Optional&lt;T&gt;</c> gains a 1-byte discriminator tag ONLY when
/// T is a fixed-width integer/float scalar (it uses every bit pattern of its storage). Every
/// spare-inhabitant payload — Bool, pointers, class refs, enums, structs — keeps the inner size
/// and must NOT have a tag appended; fabricating one inflates the layout by a byte/slot.
/// </summary>
public class OptionalAbiClassifierTests
{
    [Theory]
    // Fixed-width integer scalars — every bit pattern used, no spare inhabitant → tag appended.
    [InlineData("Swift.Int")]
    [InlineData("Swift.UInt")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.UInt16")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.UInt8")]
    // Floating-point scalars.
    [InlineData("Swift.Float")]
    [InlineData("Swift.Double")]
    // CGFloat under both module spellings the type database can surface.
    [InlineData("CoreFoundation.CGFloat")]
    [InlineData("CoreGraphics.CGFloat")]
    public void HasAppendedOptionalTag_TagAddingScalar_ReturnsTrue(string swiftTypeName)
    {
        Assert.True(OptionalAbiClassifier.HasAppendedOptionalTag(swiftTypeName));
    }

    [Theory]
    // Bool folds .none into a spare bit pattern — Optional<Bool> is 1 byte, NOT 2.
    [InlineData("Swift.Bool")]
    // Pointers reserve the null representation as the spare inhabitant.
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Swift.OpaquePointer")]
    // Class references — Optional<AnyObject> is a single tagged pointer, no extra byte.
    [InlineData("Swift.AnyObject")]
    [InlineData("MyModule.SomeClass")]
    // Enums / structs carry their own spare bits.
    [InlineData("MyModule.SomeEnum")]
    [InlineData("MyModule.SomeStruct")]
    public void HasAppendedOptionalTag_SpareInhabitantPayload_ReturnsFalse(string swiftTypeName)
    {
        Assert.False(OptionalAbiClassifier.HasAppendedOptionalTag(swiftTypeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HasAppendedOptionalTag_NullOrEmpty_ReturnsFalse(string swiftTypeName)
    {
        Assert.False(OptionalAbiClassifier.HasAppendedOptionalTag(swiftTypeName));
    }
}
