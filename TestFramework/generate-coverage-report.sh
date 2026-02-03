#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Generates a machine-readable coverage report (coverage-matrix.json) by
# parsing the ABI JSON and binding-report.json produced by the generator.
#
# Usage: ./generate-coverage-report.sh
# Requires: regenerate-bindings.sh to have been run first (output/ must exist)

set -e
cd "$(dirname "$0")"

MODULE_NAME="SwiftBindingsTestLib"
XCFW_DIR=".build/$MODULE_NAME.xcframework"
SIM_FW_DIR=$(find "$XCFW_DIR" -type d -name "*.framework" 2>/dev/null | head -1)

ABI_JSON=""
if [ -n "$SIM_FW_DIR" ]; then
    ABI_JSON=$(find "$SIM_FW_DIR" -name "*.abi.json" 2>/dev/null | head -1)
fi

BINDING_REPORT="output/binding-report.json"

if [ -z "$ABI_JSON" ] && [ ! -f "$BINDING_REPORT" ]; then
    echo "Error: Neither ABI JSON nor binding report found."
    echo "Run ./build-xcframework.sh and ./regenerate-bindings.sh first."
    exit 1
fi

echo "=== Generating Coverage Report ==="
[ -n "$ABI_JSON" ] && echo "ABI JSON: $ABI_JSON"
[ -f "$BINDING_REPORT" ] && echo "Binding report: $BINDING_REPORT"

python3 - "$ABI_JSON" "$BINDING_REPORT" <<'PYTHON_SCRIPT'
import json
import sys
import os
from datetime import datetime, timezone

abi_json_path = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] else None
binding_report_path = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] else None

# Load ABI JSON
abi_data = None
if abi_json_path and os.path.isfile(abi_json_path):
    with open(abi_json_path) as f:
        abi_data = json.load(f)

# Load binding report
binding_report = None
if binding_report_path and os.path.isfile(binding_report_path):
    with open(binding_report_path) as f:
        binding_report = json.load(f)

# --- ABI Analysis ---

def classify_abi_children(children, module_name):
    """Classify ABI JSON children into feature categories."""
    stats = {
        "structs": 0,
        "classes": 0,
        "enums": 0,
        "protocols": 0,
        "free_functions": 0,
        "operators": 0,
        "type_aliases": 0,
        "extensions": 0,
        "methods": 0,
        "properties": 0,
        "constructors": 0,
        "async_methods": 0,
        "throwing_methods": 0,
        "generic_types": 0,
        "generic_functions": 0,
        "closures_in_signatures": 0,
        "existential_params": 0,
        "tuple_params": 0,
    }

    for child in children:
        kind = child.get("kind", "")
        decl_kind = child.get("declKind", "")

        if kind == "Import":
            continue

        if decl_kind == "Struct":
            stats["structs"] += 1
            count_members(child.get("children", []), stats)
            if has_generic_params(child):
                stats["generic_types"] += 1
        elif decl_kind == "Class":
            stats["classes"] += 1
            count_members(child.get("children", []), stats)
            if has_generic_params(child):
                stats["generic_types"] += 1
        elif decl_kind == "Enum":
            stats["enums"] += 1
            count_members(child.get("children", []), stats)
            if has_generic_params(child):
                stats["generic_types"] += 1
        elif decl_kind == "Protocol":
            stats["protocols"] += 1
            count_members(child.get("children", []), stats)
        elif decl_kind == "TypeAlias":
            stats["type_aliases"] += 1
        elif kind == "Function" and decl_kind == "Func":
            if is_operator(child):
                stats["operators"] += 1
            else:
                stats["free_functions"] += 1
                if has_generic_params(child):
                    stats["generic_functions"] += 1
                check_function_features(child, stats)
        elif kind == "TypeDecl" and decl_kind == "":
            # Extension
            stats["extensions"] += 1

    return stats


def count_members(children, stats):
    """Count member-level features inside a type."""
    for child in children:
        kind = child.get("kind", "")
        decl_kind = child.get("declKind", "")

        if kind == "Function" and decl_kind == "Func":
            stats["methods"] += 1
            if has_generic_params(child):
                stats["generic_functions"] += 1
            check_function_features(child, stats)
        elif kind == "Function" and decl_kind == "Constructor":
            stats["constructors"] += 1
            check_function_features(child, stats)
        elif kind == "Var" and decl_kind == "Var":
            stats["properties"] += 1


def has_generic_params(node):
    """Check if a node has generic parameters."""
    children = node.get("children", [])
    for child in children:
        if child.get("kind") == "TypeNominal" and child.get("name") == "GenericTypeParam":
            return True
        if child.get("printedName", "").startswith("τ_"):
            return True
    return any(child.get("name") == "GenericTypeParam" for child in children)


def is_operator(node):
    """Check if a function is an operator."""
    name = node.get("name", "")
    op_chars = set("+-*/%=<>!&|^~?")
    return len(name) > 0 and all(c in op_chars for c in name)


def check_function_features(node, stats):
    """Check for async, throwing, closure params, etc."""
    if node.get("async"):
        stats["async_methods"] += 1
    if node.get("throwing"):
        stats["throwing_methods"] += 1

    children = node.get("children", [])
    for child in children:
        printed = child.get("printedName", "")
        if "-> " in printed and "(" in printed:
            stats["closures_in_signatures"] += 1
        if printed.startswith("(") and "," in printed:
            stats["tuple_params"] += 1
        if "any " in printed:
            stats["existential_params"] += 1


# --- Feature mapping from source files ---

# Map source directory/file to feature categories
FEATURE_MAP = {
    "Types/Structs.swift": {
        "name": "structs",
        "features": ["frozen_struct", "non_frozen_struct", "nested_struct", "struct_with_ref_field"]
    },
    "Types/Classes.swift": {
        "name": "classes",
        "features": ["basic_class", "class_inheritance", "final_class", "weak_reference", "unowned_reference"]
    },
    "Types/Enums.swift": {
        "name": "enums",
        "features": ["raw_value_enum", "associated_value_enum", "generic_enum"]
    },
    "Protocols/BasicProtocols.swift": {
        "name": "protocols_basic",
        "features": ["simple_protocol", "protocol_with_properties", "protocol_with_methods", "protocol_inheritance"]
    },
    "Protocols/Composition.swift": {
        "name": "protocol_composition",
        "features": ["protocol_composition"]
    },
    "Protocols/Conformance.swift": {
        "name": "protocol_conformance",
        "features": ["type_conforming_to_protocol"]
    },
    "Generics/Functions.swift": {
        "name": "generic_functions",
        "features": ["generic_function", "generic_function_with_constraint"]
    },
    "Generics/Types.swift": {
        "name": "generic_types",
        "features": ["generic_struct", "generic_class", "bound_generic_type"]
    },
    "Generics/Constraints.swift": {
        "name": "generic_constraints",
        "features": ["where_clause"]
    },
    "Generics/Existentials.swift": {
        "name": "existentials",
        "features": ["any_protocol_existential"]
    },
    "Closures/Escaping.swift": {
        "name": "escaping_closures",
        "features": ["escaping_void_closure", "escaping_with_primitives", "escaping_with_frozen_struct"]
    },
    "Closures/ConventionC.swift": {
        "name": "convention_c_closures",
        "features": ["convention_c"]
    },
    "Closures/ClosureReturns.swift": {
        "name": "closure_returns",
        "features": ["method_returning_closure"]
    },
    "Async/Methods.swift": {
        "name": "async_methods",
        "features": ["async_method", "async_static_method"]
    },
    "Async/AsyncThrowing.swift": {
        "name": "async_throwing",
        "features": ["async_throwing_method"]
    },
    "Properties/Getters.swift": {
        "name": "property_getters",
        "features": ["stored_property_getter", "computed_property_getter"]
    },
    "Properties/Setters.swift": {
        "name": "property_setters",
        "features": ["property_setter"]
    },
    "Properties/Static.swift": {
        "name": "static_properties",
        "features": ["static_property"]
    },
    "Properties/Computed.swift": {
        "name": "computed_properties",
        "features": ["computed_property"]
    },
    "Operators/Arithmetic.swift": {
        "name": "arithmetic_operators",
        "features": ["arithmetic_operators"]
    },
    "Operators/Comparison.swift": {
        "name": "comparison_operators",
        "features": ["comparison_operators"]
    },
    "Operators/Bitwise.swift": {
        "name": "bitwise_operators",
        "features": ["bitwise_operators"]
    },
    "Operators/Unary.swift": {
        "name": "unary_operators",
        "features": ["unary_operators"]
    },
    "Tuples/BasicTuples.swift": {
        "name": "basic_tuples",
        "features": ["two_element_tuple", "seven_element_tuple"]
    },
    "Tuples/Named.swift": {
        "name": "named_tuples",
        "features": ["named_tuple_elements"]
    },
    "Tuples/TupleReturns.swift": {
        "name": "tuple_returns",
        "features": ["method_returning_tuple"]
    },
    "Initializers/BasicInit.swift": {
        "name": "basic_initializers",
        "features": ["standard_initializer"]
    },
    "Initializers/Failable.swift": {
        "name": "failable_initializers",
        "features": ["failable_initializer"]
    },
    "Initializers/Throwing.swift": {
        "name": "throwing_initializers",
        "features": ["throwing_initializer"]
    },
    "Parameters/Inout.swift": {
        "name": "inout_parameters",
        "features": ["inout_parameter"]
    },
    "Parameters/Defaults.swift": {
        "name": "default_parameters",
        "features": ["default_parameter_value"]
    },
    "ErrorHandling/ThrowingFunctions.swift": {
        "name": "throwing_functions",
        "features": ["synchronous_throws", "static_throws"]
    },
    "ErrorHandling/ErrorTypes.swift": {
        "name": "error_types",
        "features": ["custom_error_type"]
    },
    "MemoryManagement/LibraryEvolution.swift": {
        "name": "library_evolution",
        "features": ["non_frozen_layout_change", "non_frozen_class", "non_frozen_enum", "evolving_optional_fields"]
    },
    "EdgeCases/Unicode.swift": {
        "name": "unicode",
        "features": ["unicode_identifiers"]
    },
    "EdgeCases/Keywords.swift": {
        "name": "keywords",
        "features": ["reserved_word_handling"]
    },
    "EdgeCases/Visibility.swift": {
        "name": "visibility",
        "features": ["access_levels"]
    },
    "EdgeCases/Deprecation.swift": {
        "name": "deprecation",
        "features": ["available_attributes"]
    },
    "Foundation/Data.swift": {
        "name": "foundation_data",
        "features": ["data_as_parameter", "data_as_return", "optional_data"]
    },
    "Foundation/URL.swift": {
        "name": "foundation_url",
        "features": ["url_as_parameter", "optional_url_return", "struct_with_url"]
    },
    "Foundation/Date.swift": {
        "name": "foundation_date",
        "features": ["date_as_parameter", "date_as_return", "date_arithmetic"]
    },
    "Foundation/Extensions.swift": {
        "name": "foundation_extensions",
        "features": ["extension_on_foundation_type", "retroactive_conformance"]
    },
    "UnsafeTypes/Pointers.swift": {
        "name": "typed_pointers",
        "features": ["unsafe_pointer", "unsafe_mutable_pointer", "pointer_as_return"]
    },
    "UnsafeTypes/RawPointers.swift": {
        "name": "raw_pointers",
        "features": ["unsafe_raw_pointer", "unsafe_mutable_raw_pointer"]
    },
    "UnsafeTypes/OpaquePointer.swift": {
        "name": "opaque_pointer",
        "features": ["opaque_pointer", "optional_opaque_pointer"]
    },
    "ObjCInterop/NSObjectSubclass.swift": {
        "name": "nsobject_subclass",
        "features": ["nsobject_subclass", "nsobject_inheritance", "nsobject_as_parameter"]
    },
    "ObjCInterop/ObjCAttributes.swift": {
        "name": "objc_attributes",
        "features": ["objc_attribute", "objc_members", "objc_enum"]
    },
    "PropertyWrappers/Wrappers.swift": {
        "name": "property_wrappers",
        "features": ["property_wrapper_type", "wrapped_property_access", "projected_value"]
    },
    "Async/MainActor.swift": {
        "name": "main_actor",
        "features": ["main_actor_class", "main_actor_method"]
    },
    "Async/Sendable.swift": {
        "name": "sendable",
        "features": ["sendable_type", "sendable_closure"]
    },
    "Protocols/Conditional.swift": {
        "name": "conditional_conformance",
        "features": ["conditional_conformance"]
    },
    "Generics/KeyPaths.swift": {
        "name": "key_paths",
        "features": ["key_path", "writable_key_path", "key_path_as_parameter"]
    },
    "Generics/Metatypes.swift": {
        "name": "metatypes",
        "features": ["metatype_parameter", "metatype_return", "metatype_t_self"]
    },
    "Protocols/PATs.swift": {
        "name": "protocols_with_associated_types",
        "features": ["protocol_with_associated_type", "type_conforming_to_pat", "pat_as_constraint"]
    },
    "Parameters/Variadic.swift": {
        "name": "variadic_parameters",
        "features": ["variadic_int32", "variadic_string", "variadic_with_other_params"]
    },
    "Closures/Autoclosures.swift": {
        "name": "autoclosures",
        "features": ["autoclosure_parameter", "autoclosure_with_escaping"]
    },
    "Async/Actors.swift": {
        "name": "actors",
        "features": ["actor_type", "actor_isolated_method", "actor_nonisolated_method"]
    },
    "Generics/Existentials.swift+opaque": {
        "name": "opaque_return_types",
        "features": ["opaque_return_type", "opaque_return_property", "opaque_composition_return"]
    },
    "Closures/Escaping.swift+throwing": {
        "name": "throwing_closures",
        "features": ["throwing_closure", "throwing_closure_with_fallback"]
    },
    "Async/AsyncClosures.swift": {
        "name": "async_closures",
        "features": ["async_closure_parameter", "async_closure_with_param"]
    },
    "ObjCInterop/Selectors.swift": {
        "name": "selectors",
        "features": ["selector_parameter", "selector_from_method", "responds_to_selector"]
    },
    "Async/AsyncProperties.swift": {
        "name": "async_properties",
        "features": ["async_computed_property", "async_property_on_class"]
    },
}

# Features that are known unsupported (generator can't handle them yet).
# Updated for Phase 43: actors, opaque returns, throwing closures, and
# async closures now emit successfully and have been promoted to must_pass.
KNOWN_UNSUPPORTED_FEATURES = {
    "failable_initializer",
    "inout_parameter",
    "default_parameter_value",
    "non_frozen_layout_change",
    "unicode_identifiers",
    "reserved_word_handling",
    "weak_reference",
    "unowned_reference",
    "extension_on_foundation_type",
    "retroactive_conformance",
    "property_wrapper_type",
    "wrapped_property_access",
    "projected_value",
    "main_actor_class",
    "main_actor_method",
    "sendable_closure",
    "conditional_conformance",
    "key_path",
    "writable_key_path",
    "key_path_as_parameter",
    "metatype_parameter",
    "metatype_return",
    "metatype_t_self",
    "protocol_with_associated_type",
    "type_conforming_to_pat",
    "pat_as_constraint",
    "variadic_int32",
    "variadic_string",
    "variadic_with_other_params",
    "autoclosure_parameter",
    "autoclosure_with_escaping",
    "selector_parameter",
    "selector_from_method",
    "responds_to_selector",
    "async_computed_property",
    "async_property_on_class",
}


def get_source_dir():
    """Get the absolute path to the Swift source directory."""
    script_dir = os.getcwd()
    return os.path.join(script_dir, 'Sources', 'SwiftBindingsTestLib')


def get_source_files():
    """Find all Swift source files relative to the Sources directory."""
    source_dir = get_source_dir()

    files = []
    for root, dirs, filenames in os.walk(source_dir):
        for f in filenames:
            if f.endswith('.swift'):
                rel = os.path.relpath(os.path.join(root, f), source_dir)
                files.append(rel)
    return sorted(files)


import re

def build_declaration_map(source_dir, source_files):
    """Parse Swift source files to build declaration name -> file mapping.

    Scans for type declarations (class, struct, enum, protocol, actor) and
    top-level function declarations to map names back to source files.
    """
    decl_map = {}  # name -> relative file path
    type_pattern = re.compile(
        r'^\s*(?:public\s+)?(?:open\s+)?(?:@\w+[\s(][^)]*\)\s*)*'
        r'(?:final\s+)?(?:class|struct|enum|protocol|actor)\s+(\w+)',
        re.MULTILINE
    )
    func_pattern = re.compile(
        r'^(?:public\s+)?(?:static\s+)?func\s+(\w+)',
        re.MULTILINE
    )

    for rel_path in source_files:
        full_path = os.path.join(source_dir, rel_path)
        try:
            with open(full_path) as f:
                content = f.read()
            for m in type_pattern.finditer(content):
                decl_map[m.group(1)] = rel_path
            for m in func_pattern.finditer(content):
                decl_map[m.group(1)] = rel_path
        except OSError:
            pass
    return decl_map


# Per-feature declaration ownership for files hosting multiple features.
# Maps feature_name -> set of declaration names (types and top-level functions).
# Only required when multiple features share a source file; features not listed
# here fall back to file-level matching (all skips in the file apply).
FEATURE_DECLARATIONS = {
    # Generics/Types.swift — 3 features share one file
    "generic_struct": {"Wrapper", "GenericPair"},
    "generic_class": {"GenericClass"},
    "bound_generic_type": {"BoundIntPair", "BoundStringPair"},

    # Generics/Existentials.swift — existentials vs opaque returns
    "any_protocol_existential": {
        "describeAll", "acceptsAnyDescribable", "makeDescribable",
        "acceptsComposition", "makeIdentifiableDescribable",
        "ExistentialConsumer", "ExistentialBox",
    },
    "opaque_return_type": {"makeOpaqueDescribable"},
    "opaque_return_property": {"OpaqueProvider"},
    "opaque_composition_return": {"makeOpaqueComposition"},

    # Generics/Functions.swift — unconstrained vs constrained
    "generic_function": {"identity", "pair"},
    "generic_function_with_constraint": {
        "constrained", "multiConstrained", "compareIdentifiables",
    },

    # ObjCInterop/NSObjectSubclass.swift — subclass vs inheritance vs parameter
    "nsobject_subclass": {"SimpleNSObject", "createSimpleNSObject"},
    "nsobject_inheritance": {"LabeledItem", "SpecialItem"},
    "nsobject_as_parameter": {"describeNSObject"},

    # UnsafeTypes/OpaquePointer.swift — OpaquePointer vs Optional<OpaquePointer>
    "opaque_pointer": {"opaquePointerIsValid", "HandleWrapper"},
    "optional_opaque_pointer": {"optionalOpaquePointer"},

    # Closures/Escaping.swift — regular escaping vs throwing closures
    "escaping_void_closure": {"ClosureConsumer"},
    "escaping_with_primitives": {"ClosureConsumer"},
    "escaping_with_frozen_struct": {"ClosureConsumer"},
    "throwing_closure": {
        "callThrowingClosure", "callThrowingVoidClosure",
        "callThrowingStringClosure",
    },
    "throwing_closure_with_fallback": {"callThrowingClosureWithFallback"},
}


def resolve_declaration(item, module_name):
    """Extract the owning declaration name from a skipped binding item.

    For free functions (ContainingType == module), the declaration is the
    function name itself.  For type members, it is the type name.
    """
    containing = item.get("ContainingType", "")
    name = item.get("Name", "")

    if containing == module_name:
        return name  # free function
    elif "." in containing:
        return containing.rsplit(".", 1)[-1]  # type name
    return None


def match_skipped_to_features(skipped_items, decl_map, module_name):
    """Map each skipped binding item to the features it affects.

    Uses FEATURE_DECLARATIONS for fine-grained attribution when multiple
    features share a source file; falls back to file-level matching for
    features without explicit declaration lists.

    Returns feature_name -> [skipped items].
    """
    # Build file -> [skipped items] mapping
    file_skips = {}
    item_decls = {}  # item id -> declaration name
    for item in skipped_items:
        decl_name = resolve_declaration(item, module_name)
        if decl_name is None:
            continue
        source_file = decl_map.get(decl_name)
        if source_file:
            file_skips.setdefault(source_file, []).append(item)
            item_decls[id(item)] = decl_name

    # Build file -> set of feature names
    file_to_features = {}
    for rel_path, info in FEATURE_MAP.items():
        actual_path = rel_path.split("+")[0] if "+" in rel_path else rel_path
        file_to_features.setdefault(actual_path, set()).update(info["features"])

    # Attribute each skipped item to the correct feature(s)
    feature_skips = {}
    for source_file, items in file_skips.items():
        feature_names = file_to_features.get(source_file, set())
        for item in items:
            decl_name = item_decls.get(id(item))
            matched = False
            for feat in feature_names:
                owned = FEATURE_DECLARATIONS.get(feat)
                if owned is not None and decl_name in owned:
                    feature_skips.setdefault(feat, []).append(item)
                    matched = True
            if not matched:
                # No declaration-level mapping matched — check for features
                # without explicit ownership (file-level fallback).
                for feat in feature_names:
                    if feat not in FEATURE_DECLARATIONS:
                        feature_skips.setdefault(feat, []).append(item)

    return feature_skips


def build_feature_status(binding_report, source_files, module_name):
    """Build per-feature status from binding report and source file list.

    Cross-references binding report skipped items against feature categories
    to detect degraded features (test exists but bindings have skipped members).
    """
    features = []

    # Build declaration map to link skipped items to source files
    source_dir = get_source_dir()
    decl_map = build_declaration_map(source_dir, source_files)

    # Map skipped items to source files
    skipped_items = []
    if binding_report and "SkippedItems" in binding_report:
        skipped_items = binding_report["SkippedItems"]

    # Build feature -> skipped items mapping (declaration-level granularity)
    feature_skips = match_skipped_to_features(skipped_items, decl_map, module_name)

    must_pass_total = 0
    must_pass_passing = 0
    must_pass_degraded = 0
    known_unsupported_total = 0
    known_unsupported_with_test = 0

    for rel_path, info in FEATURE_MAP.items():
        # Strip suffixes like "+opaque" or "+throwing" used to add multiple
        # feature groups for the same source file.
        actual_path = rel_path.split("+")[0] if "+" in rel_path else rel_path
        file_exists = actual_path in source_files

        for feature_name in info["features"]:
            is_unsupported = feature_name in KNOWN_UNSUPPORTED_FEATURES
            skips = feature_skips.get(feature_name, [])

            if is_unsupported:
                status = "known_unsupported"
                known_unsupported_total += 1
                if file_exists:
                    known_unsupported_with_test += 1
                    test_status = "implemented"
                else:
                    test_status = "missing"
            else:
                status = "must_pass"
                must_pass_total += 1
                if file_exists:
                    if skips:
                        must_pass_degraded += 1
                        test_status = "degraded"
                    else:
                        must_pass_passing += 1
                        test_status = "passing"
                else:
                    test_status = "missing"

            entry = {
                "name": feature_name,
                "category": info["name"],
                "status": status,
                "test_file": actual_path,
                "test_exists": file_exists,
                "test_status": test_status,
            }
            if skips:
                entry["binding_skips"] = [
                    {
                        "name": s.get("Name", ""),
                        "kind": s.get("Kind", ""),
                        "reason": s.get("Reason", ""),
                        "details": s.get("Details", ""),
                    }
                    for s in skips
                ]

            features.append(entry)

    must_pass_missing = must_pass_total - must_pass_passing - must_pass_degraded

    return features, {
        "must_pass": {
            "total": must_pass_total,
            "passing": must_pass_passing,
            "degraded": must_pass_degraded,
            "missing": must_pass_missing,
        },
        "known_unsupported": {
            "total": known_unsupported_total,
            "with_test": known_unsupported_with_test,
            "without_test": known_unsupported_total - known_unsupported_with_test,
        }
    }


# --- Build report ---

source_files = get_source_files()

# ABI statistics
abi_stats = None
if abi_data:
    root = abi_data.get("ABIRoot", {})
    children = root.get("children", [])
    module_name = root.get("name", "Unknown")
    abi_stats = classify_abi_children(children, module_name)

# Binding report statistics
binding_stats = None
if binding_report:
    binding_stats = {
        "total_types": binding_report.get("TotalTypes", 0),
        "emitted_types": binding_report.get("EmittedTypes", 0),
        "skipped_types": binding_report.get("SkippedTypes", 0),
        "total_members": binding_report.get("TotalMembers", 0),
        "emitted_members": binding_report.get("EmittedMembers", 0),
        "skipped_members": binding_report.get("SkippedMembers", 0),
        "synthesized_members": binding_report.get("SynthesizedMembers", 0),
        "skipped_items": binding_report.get("SkippedItems", []),
    }

# Feature status
module = "SwiftBindingsTestLib"
features, summary = build_feature_status(binding_report, source_files, module)

# Read generator exit code if available
generator_exit_code = None
exit_code_path = os.path.join("output", "generator-exit-code")
if os.path.isfile(exit_code_path):
    try:
        with open(exit_code_path) as f:
            generator_exit_code = int(f.read().strip())
    except (ValueError, OSError):
        pass

# Assemble final report
report = {
    "generated": datetime.now(timezone.utc).isoformat(),
    "module": "SwiftBindingsTestLib",
    "generator_exit_code": generator_exit_code,
    "summary": summary,
    "source_files": {
        "total": len(source_files),
        "files": source_files
    },
    "features": features,
}

if abi_stats:
    report["abi_statistics"] = abi_stats

if binding_stats:
    report["binding_statistics"] = binding_stats

# Write output
output_path = os.path.join("output", "coverage-matrix.json")
os.makedirs("output", exist_ok=True)
with open(output_path, "w") as f:
    json.dump(report, f, indent=2)

# Print summary
mp = summary["must_pass"]
ku = summary["known_unsupported"]

print(f"\n=== Coverage Report ===")
print(f"Source files: {len(source_files)}")
if abi_stats:
    print(f"ABI: {abi_stats['structs']} structs, {abi_stats['classes']} classes, "
          f"{abi_stats['enums']} enums, {abi_stats['protocols']} protocols, "
          f"{abi_stats['free_functions']} free functions")
if binding_stats:
    print(f"Bindings: {binding_stats['emitted_types']}/{binding_stats['total_types']} types emitted, "
          f"{binding_stats['emitted_members']}/{binding_stats['total_members']} members emitted, "
          f"{binding_stats['skipped_members']} skipped")
print(f"\nMust-pass features: {mp['passing']}/{mp['total']} passing"
      f", {mp['degraded']} degraded, {mp['missing']} missing")
print(f"Known-unsupported features: {ku['with_test']}/{ku['total']} have tests")

if mp["degraded"] > 0:
    print(f"\n*** WARNING: {mp['degraded']} must-pass feature(s) have skipped binding members ***")
    degraded = [f for f in features if f.get("test_status") == "degraded"]
    for f in degraded:
        skips = f.get("binding_skips", [])
        print(f"  - {f['name']} ({f['category']}): {len(skips)} skipped member(s)")
        for s in skips[:3]:
            print(f"      {s['kind']} {s['name']}: {s['reason']}")
        if len(skips) > 3:
            print(f"      ... and {len(skips) - 3} more")

print(f"\nOutput: {output_path}")
PYTHON_SCRIPT

echo ""
echo "=== Done ==="
