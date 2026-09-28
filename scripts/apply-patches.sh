#!/usr/bin/env bash
(set -o igncr) 2>/dev/null && set -o igncr # tolerate a CRLF checkout (Cygwin/MSYS2 bash)
#
# Apply the fork's Redis patches (patches/redis/NNNN-*.patch) to a Redis
# source tree, in lexical order.
#
# Usage: scripts/apply-patches.sh <redis-source-dir>
#
# Uses "patch" when it is installed, otherwise "git apply". Set
# APPLY_TOOL=patch or APPLY_TOOL=git to choose. PATCH_DIR overrides the patch
# directory. Each patch is checked (no fuzz) before it is applied, so a patch
# that does not apply leaves no partial hunks behind. The script stops at the
# first such patch and exits 1, and it checks that every file a patch names
# really changed.
#
# Works with bash 3.2+ on MSYS2, Cygwin, Linux and macOS.

set -eu
export LC_ALL=C   # lexical (byte) order for the patch glob

die() { echo "apply-patches: error: $*" >&2; exit 1; }

if [ "$#" -ne 1 ] || [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
    echo "Usage: $0 <redis-source-dir>" >&2
    exit 2
fi

script_dir=$(cd "$(dirname "$0")" && pwd)
patch_dir=${PATCH_DIR:-$script_dir/../patches/redis}
[ -d "$patch_dir" ] || die "patch directory not found: $patch_dir"
patch_dir=$(cd "$patch_dir" && pwd)

[ -d "$1" ] || die "not a directory: $1"
src=$(cd "$1" && pwd)
if [ ! -f "$src/src/server.c" ] || [ ! -f "$src/src/version.h" ]; then
    die "$src does not look like a Redis source tree (src/server.c, src/version.h missing)"
fi

# "patch" first: under Cygwin the only git on PATH may be Git for Windows,
# which does not understand POSIX paths.
tool=${APPLY_TOOL:-}
if [ -z "$tool" ]; then
    if command -v patch >/dev/null 2>&1; then tool="patch"
    elif command -v git >/dev/null 2>&1; then tool="git"
    else die "neither patch nor git is installed (MSYS2: pacman -S patch; Cygwin: the patch package)"
    fi
fi
case "$tool" in
    patch) command -v patch >/dev/null 2>&1 || die "APPLY_TOOL=patch but patch is not installed" ;;
    git)   command -v git >/dev/null 2>&1 || die "APPLY_TOOL=git but git is not installed" ;;
    *)     die "APPLY_TOOL must be patch or git, not '$tool'" ;;
esac

# Inside a git repository (such as the CI checkout the tarball was extracted
# into), "git apply" resolves paths from that repository's root and skips
# ours while reporting success. Stop the repository search at the source
# tree; the changed-files check below catches it if this is not honoured.
GIT_CEILING_DIRECTORIES=$(dirname "$src")
export GIT_CEILING_DIRECTORIES

gnu_patch_opts=""
if [ "$tool" = patch ] && patch --version 2>/dev/null | grep -q GNU; then
    gnu_patch_opts="--no-backup-if-mismatch"
fi

# $1 = forward|reverse, $2 = patch file. Checks only; changes nothing. The
# patch is read from stdin so no path has to be translated for the tool.
check_patch() {
    if [ "$tool" = git ]; then
        if [ "$1" = reverse ]; then
            (cd "$src" && git apply --check -R -p1 < "$2") >/dev/null 2>&1
        else
            (cd "$src" && git apply --check -p1 < "$2")
        fi
    else
        if [ "$1" = reverse ]; then
            (cd "$src" && patch -p1 -R -t -F0 -s --dry-run < "$2") >/dev/null 2>&1
        else
            (cd "$src" && patch -p1 -N -t -F0 -s --dry-run < "$2")
        fi
    fi
}

# Files a patch creates or modifies, relative to the source root.
patched_files() {
    sed -n 's|^+++ b/||p' "$1" | sed 's/[[:space:]]*$//'
}

file_sum() {
    if [ -e "$src/$1" ]; then cksum < "$src/$1"; else echo missing; fi
}

apply_patch() {
    if [ "$tool" = git ]; then
        (cd "$src" && git apply -p1 --whitespace=nowarn < "$1")
    else
        # BSD patch (macOS) keeps a .orig copy when a hunk applies at an
        # offset and has no option to stop it; remove only the ones it made.
        had_orig=""
        for f in $(patched_files "$1"); do
            if [ -e "$src/$f.orig" ]; then had_orig="$had_orig $f"; fi
        done
        # shellcheck disable=SC2086 # gnu_patch_opts is empty or one word
        (cd "$src" && patch -p1 -N -t -F0 -s $gnu_patch_opts < "$1") || return 1
        for f in $(patched_files "$1"); do
            case " $had_orig " in *" $f "*) continue ;; esac
            rm -f "$src/$f.orig"
        done
    fi
}

# Validate the whole series before touching the tree.
for p in "$patch_dir"/[0-9][0-9][0-9][0-9]-*.patch; do
    [ -e "$p" ] || die "no NNNN-*.patch files in $patch_dir"
    name=$(basename "$p")
    # Count CR bytes with tr: "$(printf '\r')" is an empty pattern under Cygwin/MSYS2 bash with igncr set
    # (it strips CRs from command substitution), and an empty grep pattern matches every file.
    cr_bytes=$(LC_ALL=C tr -dc '\r' <"$p" | wc -c)
    if [ "$((cr_bytes + 0))" -gt 0 ]; then
        die "$name has CRLF line endings; it was checked out with line-ending conversion (patches/redis/.gitattributes marks *.patch -text)"
    fi
    [ -n "$(patched_files "$p")" ] || die "$name names no files (not a unified diff with b/ paths?)"
done

count=0
for p in "$patch_dir"/[0-9][0-9][0-9][0-9]-*.patch; do
    name=$(basename "$p")
    files=$(patched_files "$p")
    if ! check_patch forward "$p"; then
        if check_patch reverse "$p"; then
            die "$name is already applied to $src. Start again from a pristine source tree."
        fi
        die "$name does not apply cleanly to $src (rejected hunks above). Nothing from this patch was applied; earlier patches were."
    fi
    before=""
    for f in $files; do before="$before $f=$(file_sum "$f" | tr ' ' _)"; done
    apply_patch "$p" || die "$name failed while applying after a clean check"
    for f in $files; do
        case "$before " in
            *" $f=$(file_sum "$f" | tr ' ' _) "*) die "$name reported success but did not change $f" ;;
        esac
    done
    count=$((count + 1))
    echo "applied $name"
done

echo "apply-patches: $count patch(es) applied to $src with $tool"
