// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Satisfies an interface property on a conforming class whose Swift witness is a property the
/// class inherits from an ancestor that emitted it under a different C# name.
/// </summary>
/// <remarks>
/// <para>A Swift property can be emitted off its projected name: <c>handle</c> on an
/// Objective-C-rooted class becomes <c>HandleValue</c> (NSObject owns <c>Handle</c>), and a property
/// named like its class takes a <c>Value</c> suffix (CS0542). The class that declares the property
/// bridges its own interfaces with explicit implementations (<c>PropertyHandler</c>). A subclass
/// that adopts a protocol whose requirement the inherited property witnesses lists the interface
/// itself, but inherits only the renamed member, so C# finds no implementation of the interface
/// member (CS0535, or CS0738 when an inherited member of another type owns the name — NSObject's
/// <c>Handle</c>). That failed the whole module, since a type's base list is not a member that
/// verify-recover can withdraw.</para>
/// <para>Here the subclass gets the same explicit forwarding the declaring class would emit:
/// <c>int IP.Handle => base.HandleValue;</c>, addressed to the declaring ancestor (<see cref="AncestorReceiver"/>) so a member of the subclass or a class in between that happens to take the same C# name cannot answer for it. A requirement whose inherited witness kept its name needs
/// nothing: C# lets the inherited public member implement the interface.</para>
/// </remarks>
internal static class InheritedRequirementForwarding
{
    /// <summary>
    /// Writes explicit interface implementations into <paramref name="classDecl"/>'s body for every
    /// renamed inherited witness of a protocol the class itself lists among
    /// <paramref name="listedInterfaces"/>.
    /// </summary>
    public static void EmitRenamedInheritedPropertyForwarding(CSharpWriter csWriter, ClassDecl classDecl,
        IReadOnlyCollection<string> listedInterfaces, ITypeDatabase typeDatabase,
        ModuleEmissionContext? emissionCtx, SortedDictionary<string, List<string>>? compositionCollector)
    {
        var moduleDecl = classDecl.ModuleDecl;
        if (moduleDecl is null || classDecl.ResolvedSuperclass is null || classDecl.Conformances.Count == 0)
            return;

        foreach (var conformance in classDecl.Conformances)
        {
            var protocolDecl = moduleDecl.Protocols.FirstOrDefault(p =>
                p.SwiftTypeName?.Module == conformance.Protocol.Module && p.SwiftTypeName?.Name == conformance.Protocol.Name);
            // A protocol with Self or associated-type requirements is a generic C# interface whose
            // member types name its type parameters; the projection below cannot spell them.
            if (protocolDecl is null || !protocolDecl.CanBeExistential)
                continue;

            var interfaceName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");
            if (!listedInterfaces.Contains(interfaceName))
                continue;

            foreach (var requirement in protocolDecl.Properties)
            {
                if (requirement.IsStatic || requirement.IsObjCOptional)
                    continue;
                // Declared on the class itself: PropertyHandler bridges its own renames.
                if (classDecl.Properties.Any(p => p.Name == requirement.Name && !p.IsStatic))
                    continue;
                if (FindInheritedWitness(classDecl, requirement.Name) is not ({ } ancestor, { } witness))
                    continue;
                if (TypeSkipConditions.ClassAncestorWillBeSkipped(ancestor)
                    || MemberEmissionValidator.CanEmitProperty(witness, typeDatabase, out _, out _) is not null)
                    continue;

                var inheritedName = NameProvider.GetFinalMemberName(
                    NameProvider.GetPropertyName(witness, ancestor),
                    NameProvider.ComputePropertyRenames(ancestor, typeDatabase));
                var interfaceMember = NameProvider.GetPropertyName(requirement);
                if (inheritedName == interfaceMember)
                    continue;
                if (!TryProjectRequirementType(requirement, protocolDecl, typeDatabase, compositionCollector, out var csTypeName))
                    continue;

                var member = $"{AncestorReceiver(classDecl, ancestor, typeDatabase)}.{inheritedName}";
                csWriter.WriteLine();
                csWriter.WriteLine($"{csTypeName} {interfaceName}.{interfaceMember}");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                // A getter whose suppressed-proxy read can only throw is compile-poisoned; the
                // forwarding stays a runtime throw, as the declaring class's own bridge does.
                if (emissionCtx?.WasGetterProduceThrow(witness) == true)
                    csWriter.WriteLine($"get => throw new NotSupportedException(\"{WrapperEmitter.ProxySuppressedMessage}\");");
                else
                    csWriter.WriteLine($"get => {member};");
                if (requirement.Accessors.OfType<SetAccessorDecl>().Any())
                    csWriter.WriteLine($"set => {member} = value;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }
    }

    /// <summary>
    /// The receiver that names the witness's own member: <c>base</c> for the direct superclass, and a
    /// cast to the declaring ancestor further up, so a class in between that happens to take the
    /// same C# name for an unrelated member cannot answer for it. A generic ancestor's C# name
    /// depends on how the hierarchy binds its parameters, so it keeps <c>base</c>.
    /// </summary>
    internal static string AncestorReceiver(ClassDecl classDecl, ClassDecl ancestor, ITypeDatabase typeDatabase)
    {
        if (ReferenceEquals(classDecl.ResolvedSuperclass, ancestor)
            || ancestor.GenericParameters.Count > 0
            || !typeDatabase.TryGetTypeRecord(ancestor.SwiftTypeName, out var record))
            return "base";
        return $"((global::{record.CSharpTypeName})this)";
    }

    /// <summary>The nearest same-module ancestor declaring an instance property named <paramref name="swiftName"/>.</summary>
    internal static (ClassDecl Ancestor, PropertyDecl Witness)? FindInheritedWitness(ClassDecl classDecl, string swiftName)
    {
        for (var ancestor = classDecl.ResolvedSuperclass; ancestor is not null; ancestor = ancestor.ResolvedSuperclass)
        {
            var witness = ancestor.Properties.FirstOrDefault(p => p.Name == swiftName && !p.IsStatic);
            if (witness is not null)
                return (ancestor, witness);
        }
        return null;
    }

    /// <summary>
    /// The C# type the interface declares for <paramref name="requirement"/>: the factory projection
    /// <c>ProtocolHandler</c> gives an interface property, narrowed the same way. False when the
    /// factory cannot project it (the interface then spells it through a fallback this does not
    /// reproduce, so no forwarding is written rather than one with a mismatched type).
    /// </summary>
    private static bool TryProjectRequirementType(PropertyDecl requirement, ProtocolDecl protocolDecl, ITypeDatabase typeDatabase,
        SortedDictionary<string, List<string>>? compositionCollector, out string csTypeName)
    {
        csTypeName = string.Empty;
        if (requirement.SwiftTypeSpec is AssociatedTypeReferenceSpec)
            return false;
        var projection = new TypeProjectionFactory().Project(requirement.SwiftTypeSpec, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = false,
            GenericContext = GenericContext.Empty,
            CompositionCollector = compositionCollector,
            CurrentModuleName = protocolDecl.ModuleDecl?.Name,
        });
        if (projection is null || projection.PublicType.Contains("AnyType"))
            return false;
        csTypeName = NativeIntOverloadEmitter.NarrowNativeIntType(projection.PublicType);
        return true;
    }
}
