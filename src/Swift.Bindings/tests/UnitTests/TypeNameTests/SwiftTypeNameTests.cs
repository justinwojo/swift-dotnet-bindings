// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests
{
    public class SwiftTypeNameTests
    {
        [Fact]
        public void FromModuleQualifiedName_ValidName_SetsPropertiesCorrectly()
        {
            var typeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
            Assert.Equal("Swift", typeName.Module);
            Assert.Equal("String", typeName.Name);
            Assert.Equal("Swift.String", typeName.ModuleQualifiedName);
        }

        [Fact]
        public void FromModuleQualifiedName_NestedTypeName_SetsPropertiesCorrectly()
        {
            var typeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSString.SubType");
            Assert.Equal("Foundation", typeName.Module);
            Assert.Equal("SubType", typeName.Name);
            Assert.Equal("Foundation.NSString.SubType", typeName.ModuleQualifiedName);
        }

        [Fact]
        public void FromModuleQualifiedName_GenericTypeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SwiftTypeName.FromModuleQualifiedName("Swift.Array<Swift.String>"));
        }

        [Fact]
        public void FromModuleQualifiedName_NullName_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => SwiftTypeName.FromModuleQualifiedName(null));
        }

        [Fact]
        public void FromModuleQualifiedName_EmptyName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SwiftTypeName.FromModuleQualifiedName(""));
        }

        [Theory]
        [InlineData("String")]
        [InlineData(".String")]
        [InlineData("Swift.")]
        public void FromModuleQualifiedName_InvalidFormat_ThrowsArgumentException(string name)
        {
            Assert.Throws<ArgumentException>(() => SwiftTypeName.FromModuleQualifiedName(name));
        }

        [Fact]
        public void VoidType_HasCorrectProperties()
        {
            Assert.Equal("()", SwiftTypeName.VoidType.Name);
            Assert.Equal("", SwiftTypeName.VoidType.Module);
            Assert.Equal("()", SwiftTypeName.VoidType.ModuleQualifiedName);
        }

        [Fact]
        public void AnyType_HasCorrectProperties()
        {
            Assert.Equal("Any", SwiftTypeName.AnyType.Name);
            Assert.Equal("", SwiftTypeName.AnyType.Module);
            Assert.Equal("Any", SwiftTypeName.AnyType.ModuleQualifiedName);
        }

        [Fact]
        public void TryFromModuleQualifiedName_ValidName_ReturnsTrueAndSetsProperties()
        {
            Assert.True(SwiftTypeName.TryFromModuleQualifiedName("Swift.String", out var typeName));
            Assert.Equal("Swift", typeName.Module);
            Assert.Equal("String", typeName.Name);
            Assert.Equal("Swift.String", typeName.ModuleQualifiedName);
        }

        [Fact]
        public void TryFromModuleQualifiedName_NestedTypeName_ReturnsTrueAndSetsProperties()
        {
            Assert.True(SwiftTypeName.TryFromModuleQualifiedName("Foundation.NSString.SubType", out var typeName));
            Assert.Equal("Foundation", typeName.Module);
            Assert.Equal("SubType", typeName.Name);
        }

        // A bare unsubstituted generic parameter is the shape that reaches the SwiftUI bridge from a
        // printed ABI type name. It must report "not a type name" rather than throw: a throw there
        // aborts the whole module's generation over one unbindable init parameter.
        [Theory]
        [InlineData("τ_0_0")]
        [InlineData("τ_1_0")]
        public void TryFromModuleQualifiedName_BareGenericParameter_ReturnsFalse(string name)
        {
            Assert.False(SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName));
            Assert.Null(typeName);
        }

        // The dotted form is the dangerous one: it splits into >=2 segments, so a naive parse
        // reports module "τ_0_0" — a module that does not exist. Everything downstream then works
        // from a fabricated identity, up to rendering that spelling into Swift source and into
        // @_cdecl symbol names.
        [Theory]
        [InlineData("τ_0_0.Bridge.T")]
        [InlineData("τ_0_0.Element")]
        public void TryFromModuleQualifiedName_PlaceholderRootedName_ReturnsFalse(string name)
        {
            Assert.False(SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName));
            Assert.Null(typeName);
        }

        // A one-letter name in TYPE position is a real type, not a placeholder. Only the root
        // segment sits in module position, so only it can disqualify the name.
        [Theory]
        [InlineData("MyModule.T", "MyModule", "T")]
        [InlineData("MyModule.Outer.T", "MyModule", "T")]
        [InlineData("MyModule.E", "MyModule", "E")]
        public void TryFromModuleQualifiedName_ShortLeafName_IsAcceptedAsRealType(
            string name, string expectedModule, string expectedName)
        {
            Assert.True(SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName));
            Assert.Equal(expectedModule, typeName.Module);
            Assert.Equal(expectedName, typeName.Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("String")]
        [InlineData(".String")]
        [InlineData("Swift.")]
        [InlineData("Swift.Array<Swift.String>")]
        public void TryFromModuleQualifiedName_InvalidInput_ReturnsFalseAndDoesNotThrow(string name)
        {
            Assert.False(SwiftTypeName.TryFromModuleQualifiedName(name, out var typeName));
            Assert.Null(typeName);
        }

    }
}
