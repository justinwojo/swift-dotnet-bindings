// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class ArraySliceNormalizationEmitterTests
{
    #region IsArraySlice Tests

    [Fact]
    public void IsArraySlice_MatchesSwiftArraySlice()
    {
        var typeSpec = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        Assert.True(ArraySliceNormalizationEmitter.IsArraySlice(typeSpec));
    }

    [Fact]
    public void IsArraySlice_DoesNotMatchSwiftArray()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));
        Assert.False(ArraySliceNormalizationEmitter.IsArraySlice(typeSpec));
    }

    [Fact]
    public void IsArraySlice_DoesNotMatchPlainType()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        Assert.False(ArraySliceNormalizationEmitter.IsArraySlice(typeSpec));
    }

    #endregion

    #region ContainsArraySlice Tests

    [Fact]
    public void ContainsArraySlice_DirectParam_ReturnsTrue()
    {
        var typeSpec = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        Assert.True(ArraySliceNormalizationEmitter.ContainsArraySlice(typeSpec));
    }

    [Fact]
    public void ContainsArraySlice_PlainArray_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));
        Assert.False(ArraySliceNormalizationEmitter.ContainsArraySlice(typeSpec));
    }

    [Fact]
    public void ContainsArraySlice_InOptional_ReturnsFalse()
    {
        // Optional<ArraySlice<UInt8>> — scope boundary
        var arraySlice = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var optional = new NamedTypeSpec("Swift.Optional", arraySlice);
        Assert.False(ArraySliceNormalizationEmitter.ContainsArraySlice(optional));
    }

    [Fact]
    public void ContainsArraySlice_InClosure_ReturnsFalse()
    {
        // Closures are a scope boundary
        var closureTypeSpec = new ClosureTypeSpec();
        Assert.False(ArraySliceNormalizationEmitter.ContainsArraySlice(closureTypeSpec));
    }

    [Fact]
    public void ContainsArraySlice_InTuple_ReturnsFalse()
    {
        // Tuples are a scope boundary
        var tupleTypeSpec = new TupleTypeSpec();
        Assert.False(ArraySliceNormalizationEmitter.ContainsArraySlice(tupleTypeSpec));
    }

    #endregion

    #region NormalizeTypeSpec Tests

    [Fact]
    public void NormalizeTypeSpec_ReplacesArraySliceWithArray()
    {
        var typeSpec = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var result = ArraySliceNormalizationEmitter.NormalizeTypeSpec(typeSpec);

        Assert.IsType<NamedTypeSpec>(result);
        var named = (NamedTypeSpec)result;
        Assert.Equal("Swift.Array", named.Name);
    }

    [Fact]
    public void NormalizeTypeSpec_PreservesGenericParams()
    {
        var typeSpec = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var result = ArraySliceNormalizationEmitter.NormalizeTypeSpec(typeSpec);

        var named = (NamedTypeSpec)result;
        Assert.Single(named.GenericParameters);
        var genericParam = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("Swift.UInt8", genericParam.Name);
    }

    [Fact]
    public void NormalizeTypeSpec_PlainType_ReturnsUnchanged()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");

        var result = ArraySliceNormalizationEmitter.NormalizeTypeSpec(typeSpec);

        Assert.Same(typeSpec, result);
    }

    [Fact]
    public void NormalizeTypeSpec_PreservesAttributesAndMetadata()
    {
        var typeSpec = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        typeSpec.IsInOut = true;
        typeSpec.IsAny = true;
        typeSpec.TypeLabel = "data";
        typeSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var result = ArraySliceNormalizationEmitter.NormalizeTypeSpec(typeSpec);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Array", named.Name);
        Assert.True(named.IsInOut);
        Assert.True(named.IsAny);
        Assert.Equal("data", named.TypeLabel);
        Assert.Single(named.Attributes);
    }

    [Fact]
    public void NormalizeTypeSpec_ContainerWithChangedGenericParams_PreservesMetadata()
    {
        // A non-ArraySlice container whose generic param contains ArraySlice
        // e.g. SomeWrapper<ArraySlice<UInt8>> — when SomeWrapper's generic is rebuilt,
        // its metadata (InnerType, Attributes, etc.) must be preserved
        var inner = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var outer = new NamedTypeSpec("TestModule.Wrapper", inner);
        outer.TypeLabel = "items";

        var result = ArraySliceNormalizationEmitter.NormalizeTypeSpec(outer);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("TestModule.Wrapper", named.Name);
        Assert.Equal("items", named.TypeLabel);
        // Generic param should be normalized
        var genericParam = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("Swift.Array", genericParam.Name);
    }

    #endregion

    #region NormalizeMethodDecl Tests

    [Fact]
    public void NormalizeMethodDecl_DeepCopiesCSSignature()
    {
        var (method, _, _) = CreateMethodWithArraySlice("encrypt");

        var normalized = ArraySliceNormalizationEmitter.NormalizeMethodDecl(method);

        // Should be different list instances
        Assert.NotSame(method.CSSignature, normalized.CSSignature);
        // Original should still have ArraySlice
        var origArg = method.CSSignature[1];
        Assert.IsType<NamedTypeSpec>(origArg.SwiftTypeSpec);
        Assert.Equal("Swift.ArraySlice", ((NamedTypeSpec)origArg.SwiftTypeSpec).Name);
    }

    [Fact]
    public void NormalizeMethodDecl_PreservesThrows()
    {
        var (method, _, _) = CreateMethodWithArraySlice("encrypt", throws: true);

        var normalized = ArraySliceNormalizationEmitter.NormalizeMethodDecl(method);

        Assert.True(normalized.Throws);
    }

    [Fact]
    public void NormalizeMethodDecl_SetsUsesWrapperLibrary()
    {
        var (method, _, _) = CreateMethodWithArraySlice("encrypt");

        var normalized = ArraySliceNormalizationEmitter.NormalizeMethodDecl(method);

        Assert.True(normalized.UsesWrapperLibrary);
    }

    [Fact]
    public void NormalizeMethodDecl_MangledNameIsWrapperSymbol()
    {
        var (method, _, _) = CreateMethodWithArraySlice("encrypt");

        var normalized = ArraySliceNormalizationEmitter.NormalizeMethodDecl(method);

        Assert.StartsWith("SBW_", normalized.MangledName);
        Assert.Contains("encrypt", normalized.MangledName);
    }

    #endregion

    #region TryEmit Tests

    [Fact]
    public void TryEmit_SingleArraySliceParam_EmitsSwiftWrapper()
    {
        var (method, typeDatabase, moduleDecl) = CreateMethodWithArraySlice("encrypt");

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("ArraySlice(", swiftOutput);
        Assert.Contains("extension", swiftOutput);
    }

    [Fact]
    public void TryEmit_ThrowingMethod_EmitsThrowsAndTry()
    {
        var (method, typeDatabase, moduleDecl) = CreateMethodWithArraySlice("encrypt", throws: true);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        Assert.Contains("throws", swiftOutput);
        Assert.Contains("try ", swiftOutput);
    }

    [Fact]
    public void TryEmit_FreeFunction_EmitsStandaloneWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Int");

        var method = new MethodDecl
        {
            Name = "sumSlice",
            MangledName = "$s10TestModule8sumSliceySSSaySSGF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("arg0", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("ArraySlice(", swiftOutput);
        Assert.Contains("TestModule.sumSlice", swiftOutput);
        // Free function — no extension block
        Assert.DoesNotContain("extension", swiftOutput);
    }

    [Fact]
    public void TryEmit_FreeFunction_KeywordModule_BacktickEscapes()
    {
        var typeDatabase = CreateTypeDatabase("_class");
        // Parser escapes "class" → "_class" via ExtractUniqueName
        var moduleDecl = CreateModuleDecl("_class");

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Int");

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s5class7processySSSaySSGF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("arg0", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // Should use backtick-escaped module name, not "_class"
        Assert.Contains("`class`.process(", swiftOutput);
        Assert.DoesNotContain("_class.process", swiftOutput);
    }

    [Fact]
    public void UnescapeModuleName_KeywordModule_BacktickWraps()
    {
        Assert.Equal("`class`", ArraySliceNormalizationEmitter.UnescapeModuleName("_class"));
        Assert.Equal("`for`", ArraySliceNormalizationEmitter.UnescapeModuleName("_for"));
        Assert.Equal("`int`", ArraySliceNormalizationEmitter.UnescapeModuleName("_int"));
    }

    [Fact]
    public void UnescapeModuleName_NormalModule_Unchanged()
    {
        Assert.Equal("TestModule", ArraySliceNormalizationEmitter.UnescapeModuleName("TestModule"));
        Assert.Equal("CryptoSwift", ArraySliceNormalizationEmitter.UnescapeModuleName("CryptoSwift"));
        // Underscore prefix that's NOT a keyword — unchanged
        Assert.Equal("_MyModule", ArraySliceNormalizationEmitter.UnescapeModuleName("_MyModule"));
    }

    [Fact]
    public void TryEmit_MultipleArraySliceParams_NormalizesAll()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("RSA", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "verify",
            MangledName = "$s10TestModule3RSAC6verifyySS_SaySSGtKF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("message", arraySliceUInt8, moduleDecl),
                CreateArgument("signature", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // Both params should be converted
        var arraySliceCount = CountOccurrences(swiftOutput, "ArraySlice(");
        Assert.Equal(2, arraySliceCount);
    }

    [Fact]
    public void TryEmit_NoArraySlice_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arrayUInt8 = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "encrypt",
            MangledName = "$s10TestModule3AESC7encryptySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, arrayUInt8, moduleDecl),
                CreateArgument("block", arrayUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // No ArraySlice → no normalization → returns false → empty output
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_SecondaryBlocker_ReturnsFalse()
    {
        // Method with ArraySlice + unresolvable type → normalized sig still has placeholder → false
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var unknownType = new NamedTypeSpec("CryptoSwift.CipherModeWorker");

        var method = new MethodDecl
        {
            Name = "worker",
            MangledName = "$s10TestModule3AESC6workerySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl),
                CreateArgument("worker", unknownType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_Accessor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "data_Get",
            MangledName = "$s10TestModule3AESC4dataSaySSGvg",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_Constructor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule3AESCyACSaySSGcfc",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.AES"), moduleDecl),
                CreateArgument("key", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_MutatingStructMethod_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentStruct = CreateStructDecl("ChaChaEncryptor", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "update",
            MangledName = "$s10TestModule15ChaChaEncryptorV6updateySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsMutating = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8")), moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentStruct,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_GenericMethod_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule3AESC7processySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_InOutArraySliceParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "encrypt",
            MangledName = "$s10TestModule3AESC7encryptySSSaySSGzKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                new ArgumentDecl
                {
                    SwiftTypeSpec = arraySliceUInt8,
                    Name = "block",
                    PrivateName = "block",
                    IsInOut = true,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    #endregion

    #region DeterministicHash Tests

    [Fact]
    public void DeterministicHash8_IsDeterministic()
    {
        var input = "$s10TestModule3AESC7encryptySSSaySSGKF";

        var hash1 = ArraySliceNormalizationEmitter.DeterministicHash8(input);
        var hash2 = ArraySliceNormalizationEmitter.DeterministicHash8(input);

        Assert.Equal(hash1, hash2);
        Assert.Equal(8, hash1.Length);
    }

    [Fact]
    public void DeterministicHash8_DifferentInputsDiffer()
    {
        var hash1 = ArraySliceNormalizationEmitter.DeterministicHash8("$s10TestModule3AESC7encryptySSSaySSGKF");
        var hash2 = ArraySliceNormalizationEmitter.DeterministicHash8("$s10TestModule3AESC7decryptySSSaySSGKF");

        Assert.NotEqual(hash1, hash2);
    }

    #endregion

    // IsMutating parser tests are in SwiftABIParserRuntimeTests.cs
    // (tests funcSelfKind → IsMutating through the full ParseModule pipeline)

    #region Binding Report Tests

    [Fact]
    public void TryEmit_BindingReport_RecordsWrappedItem()
    {
        var (method, typeDatabase, moduleDecl) = CreateMethodWithArraySlice("encrypt");

        ReportCollector.Start(moduleDecl);
        EmitMethodViaHandler(method, typeDatabase);
        var report = ReportCollector.Complete();

        Assert.NotNull(report);
        Assert.Single(report.WrappedItems);
        Assert.Equal("ArraySliceNormalization", report.WrappedItems[0].WrapperKind);

        ReportCollector.Reset();
    }

    #endregion

    #region Bug #17: Internal Method Skip Tests

    [Fact]
    public void TryEmit_InternalMethod_SkipsNormalization()
    {
        // Bug #17: Methods marked as internal shouldn't get Swift wrappers
        // because the wrapper would try to call an inaccessible method.
        var (method, typeDatabase, _) = CreateMethodWithArraySlice("process64");
        method.Visibility = Visibility.Internal;

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmit_PublicMethod_EmitsNormalization()
    {
        // Public methods should still be normalized.
        var (method, typeDatabase, _) = CreateMethodWithArraySlice("encrypt");
        method.Visibility = Visibility.Public;

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEmpty(csOutput);
        Assert.NotEmpty(swiftOutput);
    }

    [Fact]
    public void TryEmit_PrivateMethod_SkipsNormalization()
    {
        // Private methods shouldn't get wrappers either.
        var (method, typeDatabase, _) = CreateMethodWithArraySlice("helperMethod");
        method.Visibility = Visibility.Private;

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    #endregion

    #region Bug #15: Internal Parent Type Skip Tests

    [Fact]
    public void TryEmit_ParentTypeNotInTypeDatabase_SkipsNormalization()
    {
        // Bug #15: Methods on types without a TypeRecord (e.g., internal types like
        // BlockEncryptor) shouldn't get Swift wrapper extensions.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create a class but do NOT register it in the TypeDatabase
        var internalClass = new ClassDecl
        {
            Name = "BlockEncryptor",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BlockEncryptor"),
            MangledName = "$s10TestModule14BlockEncryptorCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "encrypt",
            MangledName = "$s10TestModule14BlockEncryptorC7encryptySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = internalClass,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmit_ParentTypeInTypeDatabase_EmitsNormalization()
    {
        // Methods on registered types should still be normalized.
        var (method, typeDatabase, _) = CreateMethodWithArraySlice("encrypt");

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.NotEmpty(csOutput);
        Assert.NotEmpty(swiftOutput);
    }

    [Fact]
    public void TryEmit_ParentTypeIsModuleInternal_SkipsNormalization()
    {
        // Bug #15: Types marked as @usableFromInline internal (IsModuleInternal=true)
        // are in the TypeDatabase but can't be extended from external modules.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var internalClass = CreateClassDecl("BlockEncryptor", moduleDecl, typeDatabase);
        internalClass.IsModuleInternal = true;

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = "update",
            MangledName = "$s10TestModule14BlockEncryptorC6updateySSSaySSGKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = internalClass,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    #endregion

    #region Helper Methods

    private static (MethodDecl method, TypeDatabase typeDatabase, ModuleDecl moduleDecl) CreateMethodWithArraySlice(
        string name,
        bool throws = false,
        bool isStatic = false)
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("AES", moduleDecl, typeDatabase);

        var arraySliceUInt8 = new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"));
        var returnType = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"));

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule3AESC{name.Length}{name}ySSSaySSGKF",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument("block", arraySliceUInt8, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        return (method, typeDatabase, moduleDecl);
    }

    private static TypeDatabase CreateTypeDatabase(string moduleName = "TestModule")
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt8"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Byte"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt8"),
                MetadataAccessor = "$ss5UInt8VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleDecl.Name}", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                MetadataAccessor = classDecl.MangledName,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });

        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleDecl.Name}", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                MetadataAccessor = structDecl.MetadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        return structDecl;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var logger = NullLogger.Instance;

        var result = ArraySliceNormalizationEmitter.TryEmitNormalizedMethod(csWriter, swiftWriter, env, logger);

        if (!result)
            return (string.Empty, string.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static void EmitMethodViaHandler(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(NullLogger.Instance);
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    #endregion
}
