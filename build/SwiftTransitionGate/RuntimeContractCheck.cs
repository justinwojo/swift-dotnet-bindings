// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// The generated-binding epochs a <c>SwiftBindings.Runtime</c> accepts at module load: from
/// <c>RuntimeContract.MinimumSupportedGeneratedVersion</c> to <c>RuntimeContract.Version</c>, with
/// epoch 0 (a dev build) on either side always accepted.
/// </summary>
/// <param name="Version">The runtime's own epoch (<c>major*1000 + minor</c> of its package version).</param>
/// <param name="MinimumSupported">The oldest generated epoch the runtime loads.</param>
/// <param name="PackageVersion">The runtime's package version, as it records it.</param>
public sealed record RuntimeContractWindow(int Version, int MinimumSupported, string PackageVersion)
{
    /// <summary>
    /// Whether a binding generated against <paramref name="epoch"/> loads on this runtime: the same
    /// test as <c>RuntimeContract.IsGeneratedVersionSupported</c>, which the module initializer runs.
    /// </summary>
    public bool Supports(int epoch) =>
        Version == 0 || epoch == 0 || (epoch <= Version && epoch >= MinimumSupported);
}

/// <summary>What a compiled assembly records of the runtime contract it was generated against.</summary>
/// <param name="Generated">Whether the assembly is a generated binding (its module initializer is the generator's).</param>
/// <param name="Epochs">The epochs its module initializers pass to <c>RuntimeContract.AssertCompatible</c>; empty for a binding generated before the handshake.</param>
public sealed record GeneratedRuntimeContract(bool Generated, ImmutableArray<int> Epochs);

/// <summary>
/// Reads, from metadata and IL alone, the runtime contract a generated binding was emitted against
/// and the window a <c>SwiftBindings.Runtime</c> assembly accepts, so a build can refuse a binding
/// whose module initializer would throw <c>SwiftRuntimeContractMismatchException</c> when the app
/// loads it. Base class library and <c>System.Reflection.Metadata</c> only, so the MSBuild tasks
/// link-compile it.
/// </summary>
public static class RuntimeContractCheck
{
    private const string RuntimeNamespace = "Swift.Runtime";
    private const string ContractType = "RuntimeContract";
    private const string AssertMethod = "AssertCompatible";

    /// <summary>
    /// The window <paramref name="runtimeImage"/> (a <c>Swift.Runtime</c> implementation assembly)
    /// accepts; null when it does not record one (a reference assembly drops the internal version
    /// constant, and a runtime from before the handshake has neither).
    /// </summary>
    public static RuntimeContractWindow? ReadWindow(byte[] runtimeImage)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(runtimeImage, writable: false));
            if (!pe.HasMetadata)
                return null;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (md.GetString(type.Namespace) != RuntimeNamespace || md.GetString(type.Name) != ContractType || !type.GetDeclaringType().IsNil)
                    continue;
                int? minimum = null;
                string? version = null;
                foreach (var fh in type.GetFields())
                {
                    var field = md.GetFieldDefinition(fh);
                    var constant = field.GetDefaultValue();
                    if (constant.IsNil)
                        continue;
                    var value = md.GetBlobReader(md.GetConstant(constant).Value).ReadConstant(md.GetConstant(constant).TypeCode);
                    switch (md.GetString(field.Name))
                    {
                        case "MinimumSupportedGeneratedVersion" when value is int i:
                            minimum = i;
                            break;
                        case "BuildVersion" when value is string s:
                            version = s;
                            break;
                    }
                }
                return minimum is { } floor && version is not null ? new RuntimeContractWindow(Epoch(version), floor, version) : null;
            }
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// What <paramref name="image"/> records of the contract it was generated against: the epoch
    /// each module initializer passes to <c>RuntimeContract.AssertCompatible</c>, and whether it is
    /// a generated binding at all. A generated binding is known by its module initializer's type,
    /// which every generator emits (<c>__SwiftFrameworkResolver_&lt;Module&gt;</c>, or the Apple
    /// supplement's <c>Swift.AppleSupplementRegistration</c>), so one generated before the handshake
    /// is told apart from a hand-written library that merely uses the runtime.
    /// </summary>
    public static GeneratedRuntimeContract ReadGenerated(byte[] image)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
                return new(false, []);
            var md = pe.GetMetadataReader();
            if (md.IsAssembly && md.GetString(md.GetAssemblyDefinition().Name) == "Swift.Runtime")
                return new(false, []);
            var generated = false;
            var epochs = new SortedSet<int>();
            foreach (var th in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(th);
                var name = md.GetString(type.Name);
                var initializerType = name.StartsWith("__SwiftFrameworkResolver_", StringComparison.Ordinal)
                                      || (name == "AppleSupplementRegistration" && md.GetString(type.Namespace) == "Swift");
                foreach (var mh in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(mh);
                    if (!IsModuleInitializer(md, method))
                        continue;
                    generated |= initializerType;
                    if (method.RelativeVirtualAddress == 0)
                        continue;
                    var decoded = IlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress));
                    if (!decoded.Succeeded)
                        continue;
                    var instructions = decoded.Instructions;
                    for (var i = 1; i < instructions.Length; i++)
                    {
                        if (instructions[i].OpCode is not (ILOpCode.Call or ILOpCode.Callvirt) || !IsAssertCompatible(md, instructions[i].Token))
                            continue;
                        if (Constant(instructions[i - 1]) is { } epoch)
                            epochs.Add(epoch);
                        generated = true;
                    }
                }
            }
            return new(generated, epochs.ToImmutableArray());
        }
        catch (BadImageFormatException)
        {
            return new(false, []);
        }
    }

    /// <summary>
    /// Why <paramref name="contract"/> does not load on a runtime of <paramref name="window"/>, or
    /// null when it does (or is not a generated binding).
    /// </summary>
    public static string? Problem(GeneratedRuntimeContract contract, RuntimeContractWindow window)
    {
        if (!contract.Generated)
            return null;
        var accepted = $"the SwiftBindings.Runtime this build resolves ({window.PackageVersion}) loads bindings generated against contract epochs {Describe(window.MinimumSupported)} to {Describe(window.Version)}";
        if (contract.Epochs.IsEmpty)
            return window.Version == 0
                ? null
                : $"it records no runtime contract epoch, so it was generated by a SwiftBindings.Sdk from before the load-time contract check, and {accepted}";
        var unsupported = contract.Epochs.Where(e => !window.Supports(e)).ToList();
        if (unsupported.Count == 0)
            return null;
        var why = unsupported.Any(e => e > window.Version)
            ? "it was generated against a newer runtime than this one"
            : "it predates this runtime's dispatch contract";
        return $"it was generated against runtime contract epoch {string.Join(", ", unsupported.Select(Describe))} and {why}: {accepted}. "
               + "Its module initializer throws SwiftRuntimeContractMismatchException when the app loads it, on every engine";
    }

    /// <summary>An epoch as the package version it stands for: <c>11000</c> is <c>11000 (11.0)</c>.</summary>
    public static string Describe(int epoch) => epoch == 0 ? "0 (a development build)" : $"{epoch} ({epoch / 1000}.{epoch % 1000})";

    /// <summary>
    /// A package version's contract epoch (<c>major*1000 + minor</c>), 0 for any version that is not
    /// <c>major.minor.*</c>: the runtime's <c>RuntimeContract.ParseEpoch</c>.
    /// </summary>
    public static int Epoch(string version)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot <= 0 || !int.TryParse(version[..firstDot], out var major))
            return 0;
        var rest = version[(firstDot + 1)..];
        var secondDot = rest.IndexOf('.');
        return int.TryParse(secondDot < 0 ? rest : rest[..secondDot], out var minor) ? major * 1000 + minor : 0;
    }

    private static bool IsModuleInitializer(MetadataReader md, MethodDefinition method)
    {
        foreach (var ah in method.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(ah);
            if (SwiftTransitionManifestReader.AttributeTypeName(md, attribute) == "System.Runtime.CompilerServices.ModuleInitializerAttribute")
                return true;
        }
        return false;
    }

    private static bool IsAssertCompatible(MetadataReader md, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference)
            return false;
        var reference = md.GetMemberReference((MemberReferenceHandle)handle);
        if (md.GetString(reference.Name) != AssertMethod || reference.Parent.Kind != HandleKind.TypeReference)
            return false;
        var parent = md.GetTypeReference((TypeReferenceHandle)reference.Parent);
        return md.GetString(parent.Name) == ContractType && md.GetString(parent.Namespace) == RuntimeNamespace;
    }

    private static int? Constant(IlInstruction instruction) => instruction.OpCode switch
    {
        ILOpCode.Ldc_i4_m1 => -1,
        ILOpCode.Ldc_i4_0 => 0,
        ILOpCode.Ldc_i4_1 => 1,
        ILOpCode.Ldc_i4_2 => 2,
        ILOpCode.Ldc_i4_3 => 3,
        ILOpCode.Ldc_i4_4 => 4,
        ILOpCode.Ldc_i4_5 => 5,
        ILOpCode.Ldc_i4_6 => 6,
        ILOpCode.Ldc_i4_7 => 7,
        ILOpCode.Ldc_i4_8 => 8,
        ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4 => (int)instruction.Int64Operand,
        _ => null,
    };
}
