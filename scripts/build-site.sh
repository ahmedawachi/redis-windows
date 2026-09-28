#!/usr/bin/env bash
# Assemble the project site into a directory ready for GitHub Pages.
#
# Usage: scripts/build-site.sh <out-dir> [--bench-dir <dir>] [--force]
#
#   <out-dir>            created (or emptied) and filled with:
#                          site/*                      the site itself
#                          assets/*                    .github/assets/*
#                          docs/OPERATIONS.md, ROADMAP.md, LICENSING.md
#                          docs/patches.md             patches/redis/README.md
#                          data/bench.json             every dataset, merged
#                          .nojekyll
#   --bench-dir <dir>    benchmark results downloaded from "Verify" runs, one
#                        sub-directory per run:
#                          <dir>/<run>/meta.json       run_id, run_url, head_sha,
#                                                      created_at, runner, ...
#                          <dir>/<run>/prepare.log     optional job log (version,
#                                                      optimization level)
#                          <dir>/<run>/**/bench-<toolchain>.csv
#                        Each CSV becomes one windows-ci dataset. A missing or
#                        empty directory, a run without CSVs and a CSV that
#                        cannot be converted are all reported and skipped.
#   --force              empty <out-dir> even when it holds something other than
#                        a previous build of this script. Without it, only an
#                        empty directory or an earlier site build (.nojekyll plus
#                        data/bench.json) is emptied.
#
# data/bench.json merges site/data/datasets/*.json (committed datasets) with the
# converted runs. Needs bash 3.2+ and python3.

set -eu

die() { echo "build-site: error: $*" >&2; exit 1; }
note() { echo "build-site: $*" >&2; }

usage() { echo "Usage: $0 <out-dir> [--bench-dir <dir>] [--force]" >&2; exit 2; }

out=""
bench_dir=""
force=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --force) force=1; shift ;;
        --bench-dir) [ "$#" -ge 2 ] || usage; bench_dir=$2; shift 2 ;;
        --bench-dir=*) bench_dir=${1#--bench-dir=}; shift ;;
        -h|--help) usage ;;
        -*) echo "build-site: unknown option $1" >&2; usage ;;
        *) [ -z "$out" ] || usage; out=$1; shift ;;
    esac
done
[ -n "$out" ] || usage

repo=$(cd "$(dirname "$0")/.." && pwd)
py=${PYTHON:-python3}
command -v "$py" >/dev/null 2>&1 || die "python3 is required"
converter="$repo/ci/bench-to-json.py"
[ -f "$converter" ] || die "missing $converter"

case "$out" in
    /|"$repo"|"$repo/"|"$repo/site"|"$repo/site/") die "refusing to use $out as the output directory" ;;
esac
mkdir -p "$out"
out=$(cd "$out" && pwd)
[ "$out" != "$repo" ] || die "refusing to use the repository root as the output directory"
case "$repo/" in "$out/"*) die "refusing to build into a parent of the repository" ;; esac
case "$out/" in "$repo/site/"*|"$repo/.github/"*|"$repo/docs/"*) die "refusing to build inside a source directory: $out" ;; esac
# Empty the output directory (including dotfiles) without removing it, but only when
# it is empty already or is an earlier build of this script, unless --force.
if [ "$force" != 1 ] && [ -n "$(find "$out" -mindepth 1 -maxdepth 1 -print 2>/dev/null | head -n 1)" ]; then
    if [ ! -f "$out/.nojekyll" ] || [ ! -f "$out/data/bench.json" ]; then
        die "$out is not empty and does not look like an earlier site build; refusing to empty it (use --force to override)"
    fi
fi
find "$out" -mindepth 1 -maxdepth 1 -exec rm -rf {} +

work=$(mktemp -d "${TMPDIR:-/tmp}/build-site.XXXXXX")
trap 'rm -rf "$work"' EXIT

# ------------------------------------------------------------------ static content
if [ -d "$repo/site" ]; then
    cp -R "$repo/site/." "$out/"
else
    note "no site/ directory; publishing data and docs only"
fi

mkdir -p "$out/assets" "$out/docs" "$out/data"
if [ -d "$repo/.github/assets" ]; then
    cp -R "$repo/.github/assets/." "$out/assets/"
fi
for f in OPERATIONS.md ROADMAP.md LICENSING.md; do
    if [ -f "$repo/docs/$f" ]; then cp "$repo/docs/$f" "$out/docs/$f"; else note "docs/$f not found; skipped"; fi
done
if [ -f "$repo/patches/redis/README.md" ]; then
    cp "$repo/patches/redis/README.md" "$out/docs/patches.md"
else
    note "patches/redis/README.md not found; skipped"
fi
: >"$out/.nojekyll"

# ------------------------------------------------------------------ datasets
list="$work/datasets.txt"
: >"$list"
for f in "$repo"/site/data/datasets/*.json; do
    [ -f "$f" ] && printf '%s\n' "$f" >>"$list"
done

converted=0
if [ -n "$bench_dir" ]; then
    if [ ! -d "$bench_dir" ]; then
        note "bench directory $bench_dir does not exist; no CI datasets"
    else
        i=0
        for run in "$bench_dir"/*/; do
            [ -d "$run" ] || continue
            run=${run%/}
            meta="$run/meta.json"
            log="$run/prepare.log"
            find "$run" -type f -name 'bench-*.csv' | LC_ALL=C sort >"$work/csvs.txt"
            if [ ! -s "$work/csvs.txt" ]; then
                note "$(basename "$run"): no bench-*.csv; skipped"
                continue
            fi
            while IFS= read -r csv; do
                [ -s "$csv" ] || { note "$csv is empty; skipped"; continue; }
                # The toolchain is in the artifact name (bench-<toolchain>), which is
                # the CSV's directory after "gh run download"; else the file name.
                name=$(basename "$(dirname "$csv")")
                case "$name" in bench-*) ;; *) name=$(basename "$csv" .csv) ;; esac
                toolchain=${name#bench-}
                case "$toolchain" in
                    msys2|cygwin) ;;
                    *) note "$csv: unknown toolchain '$toolchain'; skipped"; continue ;;
                esac
                i=$((i + 1))
                dest="$work/ci-$i.json"
                set -- convert "$csv" -o "$dest" --kind windows-ci --toolchain "$toolchain" \
                    --default-redis-version-from "$repo/.github/workflows/verify.yml"
                if [ -f "$meta" ]; then set -- "$@" --meta "$meta"; fi
                if [ -f "$log" ]; then set -- "$@" --job-log "$log"; fi
                if [ ! -f "$meta" ]; then set -- "$@" --id "windows-ci-$toolchain-$(basename "$run")"; fi
                if "$py" "$converter" "$@"; then
                    printf '%s\n' "$dest" >>"$list"
                    converted=$((converted + 1))
                else
                    note "$csv could not be converted; skipped"
                fi
            done <"$work/csvs.txt"
        done
    fi
fi
note "$converted CI dataset(s) converted"

set --
while IFS= read -r f; do set -- "$@" "$f"; done <"$list"
"$py" "$converter" merge -o "$out/data/bench.json" ${1+"$@"} || die "could not write data/bench.json"

note "site written to $out"
