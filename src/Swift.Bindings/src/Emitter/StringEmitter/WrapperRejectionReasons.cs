// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Turns a <see cref="WrapperEligibility.Reason"/> token into one plain sentence a C# consumer
/// can act on. The tokens are short snake_case identifiers chosen at the guard site — good for a
/// histogram bucket, useless in an <c>[Obsolete]</c> message. This table is the only place that
/// translation happens, so the marker text, the binding report and the debug log all say the
/// same thing.
///
/// <para>Sentences are written for someone reading a generated binding, not for someone reading
/// the generator: they name the Swift shape that blocked the wrapper, never a generator type or
/// an internal gate name. They carry no trailing period — the caller composes them into a larger
/// message.</para>
/// </summary>
public static class WrapperRejectionReasons
{
    /// <summary>
    /// Reason token to consumer-facing sentence. Keys are the literals passed to
    /// <see cref="WrapperEligibility.Reject"/> in the four wrapper emitters plus the shared
    /// tokens returned by <c>WrapperValidation.GetMemberRejectionReason</c>.
    /// </summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["accessor"] = "the member is a property accessor, which is wrapped through the property path instead",
        ["actor_isolated"] = "the member is actor-isolated, so it cannot be entered from a synchronous C entry point",
        ["async"] = "the member is async, and no async wrapper shape applies to it",
        ["async_accessor"] = "the subscript accessor is async, and no async wrapper shape applies to it",
        ["async_closure"] = "an async closure parameter in a position the wrapper cannot start a task from",
        ["async_property"] = "the property accessor is async, and no async wrapper shape applies to it",
        ["cdecl_property_wrapper"] = "the member is already served by a property wrapper",
        ["closure_index_param"] = "a closure-typed subscript index, which has no C-callable representation",
        ["closure_params"] = "a closure parameter shape the C wrapper bridge cannot marshal",
        ["closure_return_type"] = "the subscript returns a closure, which the wrapper bridge cannot hand back",
        ["const_literal"] = "a parameter that Swift requires to be a compile-time constant literal",
        ["const_literal_parameter"] = "a parameter that Swift requires to be a compile-time constant literal",
        ["constructor"] = "the member is an initializer, which is wrapped through the constructor path instead",
        ["custom_actor_constructor"] = "the initializer is isolated to a custom global actor",
        ["direct_closure_setter"] = "a property setter taking a closure directly, which the wrapper bridge cannot marshal",
        ["dynamic_self_non_class"] = "a Self return on a struct or enum, which has no stable pointer representation",
        ["failable_non_frozen_struct"] = "a failable initializer on a struct whose layout is not frozen",
        ["generic_parent"] = "the declaring type is generic in a way the wrapper cannot specialize",
        ["generic_parent_inout"] = "an inout parameter whose type is one of the declaring type's generic parameters",
        ["generic_parent_metadata_buffer_mode"] = "the declaring type is generic and its metadata cannot be threaded through the wrapper",
        ["generic_parent_type"] = "the declaring type is generic in a way the wrapper cannot specialize",
        ["generic_parent_unresolved_pwt_constraint"] = "the declaring type carries a generic constraint whose witness table cannot be resolved",
        ["inherited_generic_context"] = "the member inherits a generic context from an enclosing declaration",
        ["inout_abi_mismatch"] = "an inout parameter whose type does not fit the wrapper's pointer write-back",
        ["metatype_index_param"] = "a metatype subscript index, which has no C-callable representation",
        ["metatype_param"] = "a metatype parameter, which has no C-callable representation",
        ["metatype_property"] = "a metatype-typed property, which has no C-callable representation",
        ["metatype_return"] = "a metatype return, which has no C-callable representation",
        ["method_level_generics"] = "the method declares its own generic parameters, which have no C-callable ABI",
        ["module_internal"] = "the member is internal to its Swift module",
        ["nested_frozen_struct_index_param"] = "a subscript index that is a frozen struct containing another struct",
        ["nested_frozen_struct_param"] = "a parameter that is a frozen struct containing another struct",
        ["nested_frozen_struct_parameter"] = "a parameter that is a frozen struct containing another struct",
        ["nested_type_return"] = "the subscript returns a nested type the wrapper cannot name at the boundary",
        ["no_parent"] = "the member has no declaring type or module to hang a wrapper on",
        ["non_copyable_struct_parameter"] = "a non-copyable struct parameter, which cannot be passed by the wrapper",
        ["non_primitive_frozen_struct_index_param"] = "a subscript index that is a frozen struct of non-primitive fields",
        ["not_constructor"] = "the member is not an initializer",
        ["opaque_return_type"] = "the subscript returns an opaque type the wrapper cannot box",
        ["optional_closure_not_cdecl_compatible"] = "an optional closure whose shape the C wrapper bridge cannot marshal",
        ["optional_self_non_class"] = "an optional Self return on a struct or enum, which has no stable pointer representation",
        ["parent_module_internal"] = "the declaring type is internal to its Swift module",
        ["raw_generic_type_params"] = "an unsubstituted generic parameter in the signature",
        ["self_property"] = "a Self-typed property, which the wrapper cannot represent at the boundary",
        ["spi_protected"] = "the member is marked @_spi and is not part of the library's public surface",
        ["static_subscript"] = "the subscript is static, and static subscripts have no wrapper shape",
        ["unsupported_buffer_pointer_parameter"] = "a buffer-pointer parameter shape the wrapper cannot bridge",
        ["unsupported_generic_container"] = "a generic container in the signature (such as an array, dictionary or Result) that the wrapper cannot bridge",
        ["unsupported_generic_container_param"] = "a generic container subscript index that the wrapper cannot bridge",
        ["uses_wrapper_library"] = "the member is already served by a hand-written wrapper",
        ["variadic_expansion_pattern"] = "a parameter-pack expansion, which has no C-callable ABI",
        ["variadic_parameter"] = "a variadic parameter shape the wrapper bridge does not support",
        ["variadic_params"] = "a variadic parameter shape the wrapper bridge does not support",
        ["xcframework_mode"] = "wrappers are not generated in this input mode",
    };

    /// <summary>Every token this table translates explicitly. Exposed so a test can prove coverage.</summary>
    public static IReadOnlyCollection<string> KnownReasons => Descriptions.Keys;

    /// <summary>
    /// One plain sentence explaining a wrapper-eligibility rejection, with no trailing period.
    /// Never throws: an unknown token is humanized rather than dropped, and a reason that is
    /// already written as a sentence (a guard that carries its own SWIFTBIND text) passes through
    /// unchanged.
    /// </summary>
    public static string Describe(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "an unspecified wrapper-eligibility guard";

        if (Descriptions.TryGetValue(reason, out var described))
            return described;

        // Guards that already phrase their own rejection (they carry a SWIFTBIND code plus prose)
        // are self-describing — re-humanizing them would mangle the sentence.
        if (reason.Contains(' '))
            return reason.TrimEnd('.');

        return $"the {reason.Replace('_', ' ')} guard rejected this member";
    }
}
