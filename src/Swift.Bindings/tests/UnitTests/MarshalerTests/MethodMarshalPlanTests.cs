// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Construction tests for MethodMarshalPlan and its supporting record types.
/// Validates that the data model can represent all method-level marshalling concerns.
/// </summary>
public class MethodMarshalPlanTests
{
    [Fact]
    public void CanConstruct_SimpleStaticMethod()
    {
        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "GetLength",
                ReturnType = "Int64",
                Parameters = new[] { ("string", "text") },
                IsStatic = true
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "$s4Test9getLengthySix_tF",
                LibraryName = "TestLib",
                ReturnType = "Int64",
                Parameters = new[] { new PInvokeParameterInfo { Type = "SwiftString", Name = "text" } }
            },
            ParameterPlans = new[]
            {
                new ParameterMarshalInfo
                {
                    PublicName = "text",
                    PInvokeName = "textSwift",
                    Plan = new StringProjection().GetParameterPlan("text"),
                    Projection = new StringProjection()
                }
            },
            ReturnPlan = new BlittableProjection("Int64").GetReturnPlan("result", ReturnStrategy.Direct)
        };

        Assert.Equal("GetLength", plan.PublicSignature.Name);
        Assert.True(plan.PublicSignature.IsStatic);
        Assert.Single(plan.ParameterPlans);
        Assert.Equal("text", plan.ParameterPlans[0].PublicName);
        Assert.Null(plan.SwiftSelf);
        Assert.Null(plan.SwiftError);
        Assert.Null(plan.GenericMetadata);
        Assert.Null(plan.Async);
        Assert.Empty(plan.CallbackDeclarations);
    }

    [Fact]
    public void CanConstruct_InstanceMethodWithSwiftSelf()
    {
        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "GetName",
                ReturnType = "string",
                Parameters = Array.Empty<(string, string)>(),
                IsStatic = false
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "$s4Test7getNameSSyF",
                LibraryName = "TestLib",
                ReturnType = "SwiftString",
                Parameters = new[]
                {
                    new PInvokeParameterInfo { Type = "SwiftSelf", Name = "self" }
                }
            },
            ParameterPlans = Array.Empty<ParameterMarshalInfo>(),
            ReturnPlan = new StringProjection().GetReturnPlan("result", ReturnStrategy.Direct),
            SwiftSelf = new SwiftSelfSetup
            {
                Kind = SwiftSelfKind.NonFrozenStruct,
                CreationCode = "var self = new SwiftSelf((void*)_payload.DangerousGetHandle());",
                ResolvedTypeName = "MyStruct"
            },
            RequiresUnsafe = true
        };

        Assert.NotNull(plan.SwiftSelf);
        Assert.Equal(SwiftSelfKind.NonFrozenStruct, plan.SwiftSelf.Kind);
        Assert.Contains("DangerousGetHandle", plan.SwiftSelf.CreationCode);
        Assert.True(plan.RequiresUnsafe);
    }

    [Fact]
    public void CanConstruct_ThrowingMethodWithSwiftError()
    {
        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "Parse",
                ReturnType = "void",
                Parameters = new[] { ("string", "input") },
                IsStatic = true
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "$s4Test5parseyySStKF",
                LibraryName = "TestLib",
                ReturnType = "void",
                Parameters = new[]
                {
                    new PInvokeParameterInfo { Type = "SwiftString", Name = "input" },
                    new PInvokeParameterInfo { Type = "SwiftError*", Name = "error" }
                }
            },
            ParameterPlans = new[]
            {
                new ParameterMarshalInfo
                {
                    PublicName = "input",
                    PInvokeName = "inputSwift",
                    Plan = new StringProjection().GetParameterPlan("input"),
                    Projection = new StringProjection()
                }
            },
            SwiftError = new SwiftErrorSetup
            {
                IsTypedThrows = true,
                TypedErrorTypeName = "ParseError",
                ErrorCheckCode = "if (swiftError.Tag != 0) throw new SwiftException<ParseError>(swiftError);"
            }
        };

        Assert.NotNull(plan.SwiftError);
        Assert.True(plan.SwiftError.IsTypedThrows);
        Assert.Equal("ParseError", plan.SwiftError.TypedErrorTypeName);
        Assert.Contains("SwiftException<ParseError>", plan.SwiftError.ErrorCheckCode);
    }

    [Fact]
    public void CanConstruct_GenericMethodWithMetadata()
    {
        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "Convert",
                ReturnType = "T0",
                Parameters = new[] { ("T0", "value") },
                GenericTypeParameters = new[] { "T0" },
                IsStatic = true
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "$s4Test7convertyxxlF",
                LibraryName = "TestLib",
                ReturnType = "IntPtr",
                Parameters = new[]
                {
                    new PInvokeParameterInfo { Type = "IntPtr", Name = "value" },
                    new PInvokeParameterInfo { Type = "TypeMetadata", Name = "T0Metadata" }
                }
            },
            ParameterPlans = new[]
            {
                new ParameterMarshalInfo
                {
                    PublicName = "value",
                    PInvokeName = "value",
                    Plan = MarshalPlan.PassThrough("value"),
                    Projection = new BlittableProjection("T0")
                }
            },
            GenericMetadata = new GenericMetadataSetup
            {
                Parameters = new[]
                {
                    new GenericParameterMetadata
                    {
                        ParameterName = "T0",
                        MetadataCode = "var T0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();"
                    }
                }
            }
        };

        Assert.NotNull(plan.GenericMetadata);
        Assert.Single(plan.GenericMetadata.Parameters);
        Assert.Equal("T0", plan.GenericMetadata.Parameters[0].ParameterName);
        Assert.Single(plan.PublicSignature.GenericTypeParameters);
    }

    [Fact]
    public void CanConstruct_AsyncMethodWithSetup()
    {
        var successCallback = new CallbackDeclaration(
            "OnSuccess", "CallConvCdecl", "(IntPtr context, Int64 result)", "void",
            new List<MarshalStatement>
            {
                new MarshalStatement.Line("var tcs = (TaskCompletionSource<Int64>)holder[0];"),
                new MarshalStatement.Line("tcs.SetResult(result);")
            },
            "[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");

        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "FetchCount",
                ReturnType = "Task<Int64>",
                Parameters = Array.Empty<(string, string)>(),
                IsStatic = true
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "SB_FetchCount",
                LibraryName = "SwiftBindings",
                ReturnType = "void",
                Parameters = new[]
                {
                    new PInvokeParameterInfo { Type = "IntPtr", Name = "context" },
                    new PInvokeParameterInfo { Type = "IntPtr", Name = "callback" },
                    new PInvokeParameterInfo { Type = "IntPtr", Name = "errorCallback" }
                }
            },
            ParameterPlans = Array.Empty<ParameterMarshalInfo>(),
            Async = new AsyncMethodSetup
            {
                TaskCompletionSourceType = "TaskCompletionSource<Int64>",
                SuccessCallback = successCallback,
                ErrorCallback = null,
                SwiftWrapperCode = "@_silgen_name(\"SB_FetchCount\")\nfunc SB_FetchCount(...) { ... }"
            },
            SwiftWrapperCode = "@_silgen_name(\"SB_FetchCount\")\nfunc SB_FetchCount(...) { ... }",
            CallbackDeclarations = new[] { successCallback }
        };

        Assert.NotNull(plan.Async);
        Assert.Equal("TaskCompletionSource<Int64>", plan.Async.TaskCompletionSourceType);
        Assert.NotNull(plan.Async.SuccessCallback);
        Assert.Null(plan.Async.ErrorCallback);
        Assert.NotNull(plan.SwiftWrapperCode);
        Assert.Single(plan.CallbackDeclarations);
    }

    [Fact]
    public void CanConstruct_IndirectResult()
    {
        var setup = new IndirectResultSetup
        {
            IsConstructor = false,
            ReturnTypeName = "LargeStruct",
            AllocationCode = "byte* buffer = stackalloc byte[TypeMetadata.GetTypeMetadataOrThrow<LargeStruct>().Size];"
        };

        Assert.False(setup.IsConstructor);
        Assert.Equal("LargeStruct", setup.ReturnTypeName);
    }

    [Fact]
    public void CanConstruct_OptionalPointerWrapper()
    {
        var setup = new OptionalPointerWrapperSetup
        {
            OptionalTypeName = "SwiftOptional<SwiftString>",
            AllocationCode = "byte* optBuf = stackalloc byte[TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<SwiftString>>().Size];"
        };

        Assert.Equal("SwiftOptional<SwiftString>", setup.OptionalTypeName);
    }

    [Fact]
    public void SwiftSelfKind_AllVariants()
    {
        Assert.Equal(7, Enum.GetValues<SwiftSelfKind>().Length);
        Assert.Equal(SwiftSelfKind.FrozenStructValue, (SwiftSelfKind)0);
        Assert.Equal(SwiftSelfKind.None, (SwiftSelfKind)6);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var plan = new MethodMarshalPlan
        {
            PublicSignature = new MethodSignatureInfo
            {
                Name = "Test",
                ReturnType = "void",
                Parameters = Array.Empty<(string, string)>()
            },
            PInvokeDeclaration = new PInvokeDeclarationInfo
            {
                EntryPoint = "test",
                LibraryName = "lib",
                ReturnType = "void",
                Parameters = Array.Empty<PInvokeParameterInfo>()
            },
            ParameterPlans = Array.Empty<ParameterMarshalInfo>()
        };

        Assert.False(plan.RequiresUnsafe);
        Assert.False(plan.RequiresFixed);
        Assert.Null(plan.ReturnPlan);
        Assert.Null(plan.SwiftSelf);
        Assert.Null(plan.SwiftError);
        Assert.Null(plan.GenericMetadata);
        Assert.Null(plan.IndirectResult);
        Assert.Null(plan.Async);
        Assert.Null(plan.OptionalPointerWrapper);
        Assert.Null(plan.SwiftWrapperCode);
        Assert.Empty(plan.CallbackDeclarations);
        Assert.Equal("public", plan.PublicSignature.AccessModifier);
        Assert.Empty(plan.PublicSignature.GenericTypeParameters);
        Assert.Single(plan.PInvokeDeclaration.CallingConventions);
    }
}
