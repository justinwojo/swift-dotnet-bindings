// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// The <c>[UnmanagedCallConv]</c> attributes on an assembly's P/Invokes, read from its metadata.
/// </summary>
/// <remarks>
/// The Mono AOT compiler decodes the attribute of every P/Invoke that carries one with a decoder that
/// allocates on the managed heap, and it cannot collect garbage while it compiles. An app with a few
/// tens of thousands of attributed P/Invokes fills the nursery and the compiler aborts. The C
/// convention is the platform default on every Apple target, so an attribute that names only
/// <c>CallConvCdecl</c> changes nothing about the call and only adds to that count: the generator writes
/// the attribute only for the Swift convention. A <c>CallConvCdecl</c> in an attribute that also carries
/// a modifier (<c>CallConvSuppressGCTransition</c>) is just as redundant, since the modifier alone means
/// the same call. The census reads the P/Invokes the compiler reads: methods marked <c>pinvokeimpl</c>,
/// which for a source-generated <c>LibraryImport</c> is the inner declaration its attribute is
/// forwarded to.
/// </remarks>
public sealed record CallConvAttributeCensus(
    string Assembly,
    int PInvokes,
    int Attributed,
    int Swift,
    ImmutableArray<string> Redundant,
    ImmutableArray<string> Errors)
{
    private const string AttributeName = "System.Runtime.InteropServices.UnmanagedCallConvAttribute";
    private const string CdeclName = Conventions.CompilerServicesNamespace + ".CallConvCdecl";
    private const string SwiftName = Conventions.CompilerServicesNamespace + ".CallConvSwift";

    /// <summary>
    /// No P/Invoke carries a <c>CallConvCdecl</c> or an attribute naming no convention, nothing failed to
    /// decode, and the reader saw at least one Swift-convention attribute: an assembly whose census found
    /// none would pass the redundancy check without having read a single attribute.
    /// </summary>
    public bool Passed => Redundant.IsEmpty && Errors.IsEmpty && Swift > 0;

    public static CallConvAttributeCensus Take(string path) => Take(File.ReadAllBytes(path), Path.GetFileName(path));

    public static CallConvAttributeCensus Take(byte[] image, string displayName)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var md = pe.GetMetadataReader();
        int pinvokes = 0, attributed = 0, swift = 0;
        var redundant = new List<string>();
        var errors = new List<string>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var (ns, name) = TypeNames.OfDefinition(md, typeHandle);
            var typeName = (ns.Length == 0 ? name : ns + "." + name).Replace('/', '+');
            foreach (var methodHandle in md.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                    continue;
                pinvokes++;
                var member = typeName + "::" + md.GetString(method.Name);
                foreach (var attributeHandle in method.GetCustomAttributes())
                {
                    var attribute = md.GetCustomAttribute(attributeHandle);
                    if (AttributeTypeName(md, attribute) != AttributeName)
                        continue;
                    attributed++;
                    ImmutableArray<string> names;
                    try
                    {
                        names = CallConvNames(attribute.DecodeValue(SwiftTransitionScanner.AttributeTypeProvider.Instance));
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
                    {
                        errors.Add($"{member}: the attribute does not decode: {ex.Message}");
                        continue;
                    }
                    if (names.Contains(SwiftName))
                        swift++;
                    if (names.IsEmpty || names.Contains(CdeclName))
                    {
                        var entryPoint = md.GetString(method.GetImport().Name);
                        redundant.Add($"{member} -> {(entryPoint.Length == 0 ? md.GetString(method.Name) : entryPoint)} " +
                                      $"[{string.Join(", ", names.Select(n => n.Substring(n.LastIndexOf('.') + 1)))}]");
                    }
                }
            }
        }

        return new CallConvAttributeCensus(displayName, pinvokes, attributed, swift,
            redundant.Order(StringComparer.Ordinal).ToImmutableArray(), errors.ToImmutableArray());
    }

    private static ImmutableArray<string> CallConvNames(CustomAttributeValue<string> value) =>
        value.NamedArguments
            .Where(a => a.Name == "CallConvs" && a.Value is ImmutableArray<CustomAttributeTypedArgument<string>>)
            .SelectMany(a => (ImmutableArray<CustomAttributeTypedArgument<string>>)a.Value!)
            .Select(a => a.Value as string)
            .Where(s => s is not null)
            .Select(s => SwiftTransitionScanner.AttributeTypeProvider.StripAssemblyQualification(s!))
            .ToImmutableArray();

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle parent;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                parent = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return null;
        }
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
            {
                var (ns, name, _) = TypeNames.OfReference(md, (TypeReferenceHandle)parent);
                return ns + "." + name;
            }
            case HandleKind.TypeDefinition:
            {
                var (ns, name) = TypeNames.OfDefinition(md, (TypeDefinitionHandle)parent);
                return ns + "." + name;
            }
            default:
                return null;
        }
    }
}
