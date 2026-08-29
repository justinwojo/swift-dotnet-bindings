#!/bin/sh
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Rewrites shallow framework bundles inside a built macOS / Mac Catalyst app into Apple's
# deep (versioned) layout, in place.
#
# WHY
#   iOS and tvOS frameworks are required to be SHALLOW: the Mach-O and Info.plist sit at the
#   bundle root. macOS and Mac Catalyst frameworks are the opposite — App Store validation for
#   those platforms expects a versioned bundle whose root contains only `Versions/` plus
#   symlinks, with the executable at `Versions/A/<Name>` and the plist at
#   `Versions/A/Resources/Info.plist`. A framework built once in the iOS shape and embedded on
#   every platform therefore reads as malformed to Mac App Store validation even though dyld
#   loads it happily, which is why nothing short of an upload notices.
#
# WHY HERE, AND WHY BEFORE SIGNING
#   The required `Versions/Current` and top-level entries have to be real symbolic links, and a
#   NuGet package is a zip that does not round-trip symlinks — so the shape cannot survive the
#   package boundary and has to be materialised on the consumer's Mac. The app bundle is also
#   the thing Apple validates, so rewriting the embedded copy covers frameworks from any origin,
#   including one whose links were flattened somewhere in transit. It must happen BEFORE the
#   bundle is signed: editing a sealed bundle afterwards invalidates the seal ("unsealed
#   contents"), which is exactly what a post-build repair runs into.
#
# IDEMPOTENT BY DESIGN, AND RE-ENTRANT AFTER A FAILED BUILD
#   Builds re-run, and an incremental build may re-enter this with the work already done, so a
#   bundle that already has the deep shape is left byte-for-byte untouched. "Already deep" is
#   judged against the whole bundle root, not a few landmarks: a root that still holds real
#   content, or a link pointing somewhere other than through `Versions/Current`, is a bundle
#   that has not been converted yet however finished it looks.
#
#   A build that fails partway through leaves the bundle mid-move, so the rewrite deliberately
#   creates `Versions/` FIRST: from that moment on every interrupted state is recognisable as
#   versioned, and the repair path — which finishes the move, re-points the links and drops a
#   stale root seal — takes it the rest of the way. The same path also handles a bundle whose
#   links were flattened into copies somewhere in transit, and one carrying an old corruption
#   this step never produced. Anything with no executable to place is left untouched rather
#   than restructured on a guess.
#
# Every link created here is RELATIVE, so the bundle stays relocatable.
#
# Usage: sh deepen-mac-framework.sh [--exclude <Name>]... <frameworks-directory>
#   <Name> may be given with or without the trailing `.framework`.

set -e

# A framework may carry frameworks of its own (an umbrella or vendor bundle with a Frameworks/
# directory), and those need the versioned shape just as much as their parent. Once the parent is
# deep they sit under Versions/Current/Frameworks, and this script re-enters itself on that
# directory as a child process rather than recursing in-shell: the functions below share the
# shell's single variable namespace, so an in-shell recursion would overwrite the loop state of the
# caller mid-iteration. The exclusion list rides along in the environment so `--exclude` reaches
# the nested pass unchanged, and the nested pass keeps quiet when it finds nothing to do.
EXCLUDES=${SWIFTBINDINGS_DEEPEN_EXCLUDES:-}
NESTED_PASS=${SWIFTBINDINGS_DEEPEN_NESTED:-}

while [ $# -gt 0 ]; do
    case $1 in
        --exclude)
            [ $# -ge 2 ] || { echo "deepen-mac-framework: --exclude needs a value" >&2; exit 2; }
            name=${2%.framework}
            EXCLUDES="$EXCLUDES $name"
            shift 2
            ;;
        --)
            shift
            break
            ;;
        *)
            break
            ;;
    esac
done

FRAMEWORKS_DIR=${1:-}
[ -n "$FRAMEWORKS_DIR" ] || { echo "deepen-mac-framework: no frameworks directory given" >&2; exit 2; }

# Nothing embedded yet (or nothing to embed) is a legitimate no-op, not an error.
[ -d "$FRAMEWORKS_DIR" ] || exit 0

is_excluded() {
    for e in $EXCLUDES; do
        if [ "$e" = "$1" ]; then
            return 0
        fi
    done
    return 1
}

# The bundle's executable is normally named after the bundle, but CFBundleExecutable is the
# authority; read it when a plist is readable so a bundle whose binary is named differently is
# still recognised rather than silently skipped.
executable_name() {
    fw=$1
    fallback=$2
    # Shallow bundles keep the plist at the root; versioned ones keep it under a version
    # directory. A half-converted bundle may have moved the plist without having a Current link
    # yet, so every version directory is consulted too before falling back to the bundle name.
    for plist in "$fw/Info.plist" "$fw/Versions/Current/Resources/Info.plist" \
                 "$fw"/Versions/*/Resources/Info.plist; do
        [ -f "$plist" ] || continue
        # The exit status is the only thing that separates a value from a diagnostic: PlistBuddy
        # reports an unreadable file on stdout, so a plain capture would hand back its error text
        # as if it were the executable's name.
        if exec_name=$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$plist" 2>/dev/null) \
            && [ -n "$exec_name" ]; then
            echo "$exec_name"
            return 0
        fi
    done
    echo "$fallback"
}

# Echo the version directory Versions/Current names, but only when that link is genuinely usable:
# a plain sibling name that resolves to a directory sitting in Versions/. A link left over from a
# version that is no longer there, or one reaching outside the bundle, is treated as absent so the
# caller replaces it rather than inheriting a dangling layout.
current_version() {
    fw=$1
    [ -L "$fw/Versions/Current" ] || return 1
    target=$(readlink "$fw/Versions/Current")
    case $target in
        ""|*/*) return 1 ;;
    esac
    [ -d "$fw/Versions/$target" ] || return 1
    echo "$target"
    return 0
}

# Point every top-level entry at Versions/Current, which is what the deep layout requires of the
# bundle root. A link is only left alone when it already names exactly the entry it stands for:
# being a link says nothing about where it goes, and a link that bypasses Current (or names
# something else entirely) is the layout being wrong, not the layout being done.
link_top_level() {
    fw=$1
    for entry in "$fw"/Versions/Current/*; do
        if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
            continue
        fi
        base=$(basename "$entry")
        # The signature belongs to the version directory, never to the bundle root.
        if [ "$base" = "_CodeSignature" ]; then
            continue
        fi
        want="Versions/Current/$base"
        if [ -L "$fw/$base" ] && [ "$(readlink "$fw/$base")" = "$want" ]; then
            continue
        fi
        rm -rf "$fw/$base"
        ln -s "$want" "$fw/$base"
    done

    # A link standing for something the version directory no longer holds would dangle; the root
    # of a valid bundle carries no such entry.
    for entry in "$fw"/*; do
        if [ ! -L "$entry" ]; then
            continue
        fi
        if [ -e "$entry" ]; then
            continue
        fi
        rm -f "$entry"
    done
}

# Move whatever real content is still sitting at the bundle root into the version directory, using
# the placement the versioned layout calls for: the executable at the version root, directories
# (Modules, Headers, PrivateHeaders, nested Frameworks, …) keeping their name, and loose files
# (Info.plist, PrivacyInfo.xcprivacy, …) under Resources. Symlinks are left for link_top_level.
#
# Where an entry exists on both sides, the ROOT copy wins and replaces the version directory's: real
# content at the root of a versioned tree is content that was delivered after the tree was made — a
# copier writing a newer package's files over the links — while the version directory holds what
# was there before. Preferring the root is what keeps the embedded payload current; a bundle whose
# links were merely flattened into identical copies repairs the same way either round, without
# doubling.
migrate_root_entries() {
    fw=$1
    version=$2
    exec_name=$3

    # An existing root Resources directory travels whole when the version has none, so its
    # contents are never rearranged; otherwise the two are merged entry by entry.
    if [ -d "$fw/Resources" ] && [ ! -L "$fw/Resources" ]; then
        if [ ! -e "$fw/Versions/$version/Resources" ]; then
            mkdir -p "$fw/Versions/$version"
            mv "$fw/Resources" "$fw/Versions/$version/Resources"
        else
            for entry in "$fw"/Resources/*; do
                if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
                    continue
                fi
                base=$(basename "$entry")
                rm -rf "$fw/Versions/$version/Resources/$base"
                mv "$entry" "$fw/Versions/$version/Resources/$base"
            done
            rm -rf "$fw/Resources"
        fi
    fi
    mkdir -p "$fw/Versions/$version/Resources"

    for entry in "$fw"/*; do
        if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
            continue
        fi
        base=$(basename "$entry")
        if [ "$base" = "Versions" ] || [ -L "$entry" ]; then
            continue
        fi
        # A root-level seal describes the shallow layout and would be read as unsealed content.
        if [ "$base" = "_CodeSignature" ]; then
            rm -rf "$entry"
            continue
        fi

        if [ "$base" = "$exec_name" ] || [ -d "$entry" ]; then
            dest="$fw/Versions/$version/$base"
        else
            dest="$fw/Versions/$version/Resources/$base"
        fi

        rm -rf "$dest"
        mv "$entry" "$dest"
    done
}

# True when the executable is somewhere this step can place correctly — still at the root, or
# already under a version directory. A bundle with neither is an unfamiliar shape.
has_executable() {
    fw=$1
    exec_name=$2
    if [ -f "$fw/$exec_name" ] && [ ! -L "$fw/$exec_name" ]; then
        return 0
    fi
    for dir in "$fw"/Versions/*; do
        if [ -d "$dir" ] && [ ! -L "$dir" ] && [ -f "$dir/$exec_name" ]; then
            return 0
        fi
    done
    return 1
}

# Every part of the versioned contract, checked over the whole bundle root. Landmarks alone would
# accept a bundle that still carries real content beside them.
is_valid_deep() {
    fw=$1
    exec_name=$2

    version=$(current_version "$fw") || return 1
    [ -f "$fw/Versions/$version/Resources/Info.plist" ] || return 1
    [ -L "$fw/$exec_name" ] || return 1
    [ "$(readlink "$fw/$exec_name")" = "Versions/Current/$exec_name" ] || return 1

    for entry in "$fw"/*; do
        if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
            continue
        fi
        base=$(basename "$entry")
        if [ "$base" = "Versions" ]; then
            continue
        fi
        # Real content at the root, a link that bypasses Current, or one that no longer resolves.
        [ -L "$entry" ] || return 1
        [ "$(readlink "$entry")" = "Versions/Current/$base" ] || return 1
        [ -e "$entry" ] || return 1
    done
    return 0
}

# Finish the conversion of any bundle that already has a Versions/ tree, whatever state it is in:
# a flattened copy of a good bundle, an interrupted rewrite, or a layout corrupted before this
# step ever saw it. The steps are ordered so each one only depends on what the previous produced.
converge_versioned() {
    fw=$1
    exec_name=$2

    version=$(current_version "$fw" || true)
    if [ -z "$version" ]; then
        # A copier that followed links can leave Current as a real directory holding the payload;
        # when it is the only thing in Versions/ it becomes the version directory rather than
        # being discarded along with the broken link.
        if [ -d "$fw/Versions/Current" ] && [ ! -L "$fw/Versions/Current" ] && [ ! -d "$fw/Versions/A" ]; then
            others=$(ls "$fw/Versions" 2>/dev/null | grep -v '^Current$' | head -1 || true)
            if [ -z "$others" ]; then
                mv "$fw/Versions/Current" "$fw/Versions/A"
            fi
        fi

        # No usable Current link. Keep an existing version directory if there is one, otherwise A
        # is the version this bundle is being given.
        if [ -d "$fw/Versions/A" ]; then
            version=A
        else
            version=$(ls "$fw/Versions" 2>/dev/null | grep -v '^Current$' | head -1 || true)
        fi
        if [ -z "$version" ]; then
            version=A
        fi
        mkdir -p "$fw/Versions/$version"
        rm -rf "$fw/Versions/Current"
        ln -s "$version" "$fw/Versions/Current"
    fi

    migrate_root_entries "$fw" "$version" "$exec_name"
    link_top_level "$fw"
}

# Give any frameworks nested inside a (now versioned) bundle the same treatment, through a child
# process for the reason given at the top of the file.
deepen_nested() {
    nested="$1/Versions/Current/Frameworks"
    [ -d "$nested" ] || return 0
    SWIFTBINDINGS_DEEPEN_EXCLUDES="$EXCLUDES" SWIFTBINDINGS_DEEPEN_NESTED=1 /bin/sh "$0" "$nested"
}

changed=0

for fw in "$FRAMEWORKS_DIR"/*.framework; do
    # An unmatched glob expands to the literal pattern.
    [ -d "$fw" ] || continue

    bundle=$(basename "$fw")
    name=${bundle%.framework}

    if is_excluded "$name"; then
        echo "  framework anatomy: skipping $bundle (excluded)"
        continue
    fi

    exec_name=$(executable_name "$fw" "$name")

    # Already the deep shape — leave it exactly as it is, apart from anything nested in it.
    if is_valid_deep "$fw" "$exec_name"; then
        deepen_nested "$fw"
        continue
    fi

    if ! has_executable "$fw" "$exec_name"; then
        # Nothing to place. Restructuring this would be a guess about a shape we do not recognise.
        echo "  framework anatomy: leaving $bundle alone (no executable at the bundle root or under Versions/)"
        continue
    fi

    if [ -d "$fw/Versions" ]; then
        converge_versioned "$fw" "$exec_name"
        echo "  framework anatomy: repaired the versioned layout of $bundle"
    else
        # Versions/ is created before anything moves, so an interrupted rewrite is recognisable as
        # versioned on the next build and finishes through the path above rather than starting over.
        mkdir -p "$fw/Versions/A"
        converge_versioned "$fw" "$exec_name"
        echo "  framework anatomy: rewrote $bundle into a versioned bundle"
    fi
    changed=$((changed + 1))
    deepen_nested "$fw"
done

# Silence on a rebuild is the expected case; say so once rather than per bundle.
if [ "$changed" -eq 0 ] && [ -z "$NESTED_PASS" ]; then
    echo "  framework anatomy: no changes needed in $FRAMEWORKS_DIR"
fi

exit 0
