// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate,
    /// with support for struct parameters that need marshalling.
    /// For closures like (ImageDecodingContext) -> (any ImageDecoding)?, the struct
    /// parameter must be marshalled to a native buffer before calling the Swift function.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitClosureReturnMarshallingWithStructParams(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result",
        string? invokeThunkPInvokeName = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);

        // Start building the closure body with struct marshalling
        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
            """);

        csWriter.Indent += 3;

        // Generate marshalling code for each struct parameter
        var invokeArgs = new List<string>();
        for (int i = 0; i < argTypes.Count; i++)
        {
            var arg = argTypes[i];
            if (closureHandler.IsFrozenStruct(arg))
            {
                // Generate marshalling for frozen struct
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = stackalloc byte[(int)_arg{{i}}Metadata.Size];
                    var _arg{{i}}Span = new Span<byte>(_arg{{i}}Buffer, (int)_arg{{i}}Metadata.Size);
                    SwiftMarshal.MarshalToSwift(_arg{{i}}, ref _arg{{i}}Span);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
            }
            else if (MarshallingHelpers.IsBoolType(arg))
            {
                // Bool conversion
                invokeArgs.Add($"(byte)(_arg{i} ? 1 : 0)");
            }
            else if (closureHandler.NeedsWellKnownProtocolWrapping(arg, out _))
            {
                // Well-known protocol types: unwrap to ExistentialContainer for function pointer
                invokeArgs.Add($"_arg{i}.GetExistentialContainer()");
            }
            else if (closureHandler.NeedsProxyWrapping(arg, out _))
            {
                // Known protocol: extract container from interface for function pointer
                if (closureHandler.ShouldUseGetOrCreate(arg))
                {
                    var pt = closureHandler.GetPublicExistentialType(arg) ?? "object";
                    invokeArgs.Add($"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{i})");
                }
                else
                {
                    var ct = closureHandler.GetPInvokeExistentialType(arg);
                    invokeArgs.Add($"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>)_arg{i}).GetExistentialContainer()");
                }
            }
            else if (closureHandler.IsExistentialParam(arg))
            {
                // Unknown protocol: unbox object to container for function pointer
                var ct = closureHandler.GetPInvokeExistentialType(arg);
                invokeArgs.Add($"({ct})_arg{i}");
            }
            else if (closureHandler.IsClassType(arg))
            {
                // Class types: extract handle as void* for function pointer invocation
                invokeArgs.Add($"(void*)_arg{i}.Payload.DangerousGetHandle()");
            }
            else if (closureHandler.IsObjCBridgedClass(arg))
            {
                // ObjC bridged class types: extract .Handle as void* for function pointer invocation
                invokeArgs.Add($"(void*)_arg{i}.Handle");
            }
            else
            {
                // Direct pass
                invokeArgs.Add($"_arg{i}");
            }
        }
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Generate the invoke and return
        string invokeExpr = $"_fp({invokeArgsString})";
        if (!hasReturn)
        {
            csWriter.WriteLine($"{invokeExpr};");
        }
        else if (returnIsBool)
        {
            csWriter.WriteLine($"return {invokeExpr} != 0;");
        }
        else if (closureHandler.NeedsWellKnownProtocolWrapping(closureTypeSpec.ReturnType, out var wrapFrozenReturn))
        {
            csWriter.WriteLine($"return new {wrapFrozenReturn}({invokeExpr});");
        }
        else if (closureHandler.NeedsProxyWrapping(closureTypeSpec.ReturnType, out var frozenProxy))
        {
            csWriter.WriteLine($"return new {frozenProxy}({invokeExpr});");
        }
        else if (closureHandler.IsExistentialParam(closureTypeSpec.ReturnType))
        {
            csWriter.WriteLine($"return (object){invokeExpr};");
        }
        else
        {
            csWriter.WriteLine($"return {invokeExpr};");
        }

        csWriter.Indent -= 3;
        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }

    /// <summary>
    /// Emits code to convert a SwiftClosureData return value into a C# delegate,
    /// with support for non-frozen struct parameters that require heap allocation.
    /// Non-frozen structs cannot use stackalloc since their size is not known at compile time.
    /// Uses NativeMemory.Alloc + InitializeWithCopy with proper cleanup via Destroy + NativeMemory.Free.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler for type translation.</param>
    /// <param name="resultVariableName">The name of the variable holding the SwiftClosureData result.</param>
    public static void EmitClosureReturnMarshallingWithNonFrozenParams(
        CSharpWriter csWriter,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string resultVariableName = "result",
        string? invokeThunkPInvokeName = null)
    {
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var funcPtrType = closureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
        var funcPtrTypeWithContext = AddContextToFunctionPointerType(funcPtrType);

        // Build lambda parameter list
        var parameters = new List<string>();
        var argTypes = new List<TypeSpec>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            parameters.Add($"_arg{argIndex}");
            argTypes.Add(arg);
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var parameterListWithParens = parameters.Count == 1 ? parametersString : $"({parametersString})";

        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnIsBool = hasReturn && MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType);

        // Start building the closure body with non-frozen struct marshalling
        csWriter.WriteLines($$"""
            // Wrap Swift closure in SwiftEscapingClosure for ARC management
            var _closureWrapper = SwiftEscapingClosure<{{delegateType}}>.FromSwift({{resultVariableName}}.FunctionPointer, {{resultVariableName}}.Context);

            // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
            {{delegateType}} _invoker = {{parameterListWithParens}} =>
            {
                unsafe
                {
                    var _fp = ({{funcPtrTypeWithContext}})_closureWrapper.FunctionPointer;
                    var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
            """);

        csWriter.Indent += 3;

        // Track which arguments need cleanup and collect invoke args
        var invokeArgs = new List<string>();
        var nonFrozenArgs = new List<int>();

        for (int i = 0; i < argTypes.Count; i++)
        {
            var arg = argTypes[i];
            if (closureHandler.IsNonFrozenStruct(arg))
            {
                // Generate marshalling for non-frozen struct using NativeMemory
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    // Non-frozen struct: allocate on heap, initialize, and clean up after call
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = (byte*)NativeMemory.Alloc((nuint)_arg{{i}}Metadata.Size, (nuint)_arg{{i}}Metadata.Stride);
                    _arg{{i}}Metadata.ValueWitnessTable->InitializeWithCopy(
                        (void*)_arg{{i}}Buffer,
                        (void*)_arg{{i}}.Payload.DangerousGetHandle(),
                        _arg{{i}}Metadata);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
                nonFrozenArgs.Add(i);
            }
            else if (closureHandler.IsFrozenStruct(arg))
            {
                // Generate marshalling for frozen struct using stackalloc
                var csharpType = closureHandler.TranslateTypeSpecToCSharp(arg);
                csWriter.WriteLines($$"""
                    var _arg{{i}}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{{csharpType}}>();
                    byte* _arg{{i}}Buffer = stackalloc byte[(int)_arg{{i}}Metadata.Size];
                    var _arg{{i}}Span = new Span<byte>(_arg{{i}}Buffer, (int)_arg{{i}}Metadata.Size);
                    SwiftMarshal.MarshalToSwift(_arg{{i}}, ref _arg{{i}}Span);
                    """);
                invokeArgs.Add($"_arg{i}Buffer");
            }
            else if (MarshallingHelpers.IsBoolType(arg))
            {
                // Bool conversion
                invokeArgs.Add($"(byte)(_arg{i} ? 1 : 0)");
            }
            else if (closureHandler.NeedsWellKnownProtocolWrapping(arg, out _))
            {
                // Well-known protocol types: unwrap to ExistentialContainer for function pointer
                invokeArgs.Add($"_arg{i}.GetExistentialContainer()");
            }
            else if (closureHandler.NeedsProxyWrapping(arg, out _))
            {
                // Known protocol: extract container from interface for function pointer
                if (closureHandler.ShouldUseGetOrCreate(arg))
                {
                    var pt = closureHandler.GetPublicExistentialType(arg) ?? "object";
                    invokeArgs.Add($"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{pt}>(_arg{i})");
                }
                else
                {
                    var ct = closureHandler.GetPInvokeExistentialType(arg);
                    invokeArgs.Add($"((Swift.Runtime.ISwiftExistentialConvertible<{ct}>)_arg{i}).GetExistentialContainer()");
                }
            }
            else if (closureHandler.IsExistentialParam(arg))
            {
                // Unknown protocol: unbox object to container for function pointer
                var ct = closureHandler.GetPInvokeExistentialType(arg);
                invokeArgs.Add($"({ct})_arg{i}");
            }
            else if (closureHandler.IsClassType(arg))
            {
                // Class types: extract handle as void* for function pointer invocation
                invokeArgs.Add($"(void*)_arg{i}.Payload.DangerousGetHandle()");
            }
            else if (closureHandler.IsObjCBridgedClass(arg))
            {
                // ObjC bridged class types: extract .Handle as void* for function pointer invocation
                invokeArgs.Add($"(void*)_arg{i}.Handle");
            }
            else
            {
                // Direct pass
                invokeArgs.Add($"_arg{i}");
            }
        }
        // Add context (SwiftSelf) as last argument
        invokeArgs.Add("_swiftSelf");
        var invokeArgsString = string.Join(", ", invokeArgs);

        // Generate the try block for non-frozen args that need cleanup
        if (nonFrozenArgs.Count > 0)
        {
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Generate the invoke and return
        string invokeExpr = $"_fp({invokeArgsString})";
        if (!hasReturn)
        {
            csWriter.WriteLine($"{invokeExpr};");
        }
        else if (returnIsBool)
        {
            csWriter.WriteLine($"return {invokeExpr} != 0;");
        }
        else if (closureHandler.NeedsWellKnownProtocolWrapping(closureTypeSpec.ReturnType, out var wrapNonFrozenReturn))
        {
            csWriter.WriteLine($"return new {wrapNonFrozenReturn}({invokeExpr});");
        }
        else if (closureHandler.NeedsProxyWrapping(closureTypeSpec.ReturnType, out var nonFrozenProxy))
        {
            csWriter.WriteLine($"return new {nonFrozenProxy}({invokeExpr});");
        }
        else if (closureHandler.IsExistentialParam(closureTypeSpec.ReturnType))
        {
            csWriter.WriteLine($"return (object){invokeExpr};");
        }
        else
        {
            csWriter.WriteLine($"return {invokeExpr};");
        }

        // Generate finally block for cleanup
        if (nonFrozenArgs.Count > 0)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (var i in nonFrozenArgs)
            {
                csWriter.WriteLines($$"""
                    _arg{{i}}Metadata.ValueWitnessTable->Destroy((void*)_arg{{i}}Buffer, _arg{{i}}Metadata);
                    NativeMemory.Free(_arg{{i}}Buffer);
                    """);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent -= 3;
        csWriter.WriteLines("""
                }
            };

            return _invoker;
            """);
    }
}
