#!/usr/bin/env bash
# Turns a release tag into the NuGet package version, for .github/workflows/publish.yml.
#
# Prints the version (the tag without its leading "v") on stdout and exits 0, or prints why
# the tag was rejected on stderr and exits 1. A tag is accepted only if it is "v" followed by
# a SemVer 2.0.0 version: MAJOR.MINOR.PATCH with an optional pre-release part (v1.2.3,
# v1.2.3-rc.1). Build metadata (v1.2.3+build) is rejected because nuget.org ignores it when
# telling versions apart.
#
# Usage: scripts/release-version.sh <tag>
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <tag>" >&2
  exit 1
fi

tag="$1"
number='(0|[1-9][0-9]*)'
identifier='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^${number}\.${number}\.${number}(-${identifier}(\.${identifier})*)?\$"

if [[ "$tag" != v* ]]; then
  echo "error: tag '$tag' does not start with 'v' (expected something like v1.2.3)" >&2
  exit 1
fi

version="${tag#v}"
if [[ ! "$version" =~ $semver ]]; then
  echo "error: tag '$tag' is not 'v' + a SemVer version (expected something like v1.2.3 or v1.2.3-rc.1)" >&2
  exit 1
fi

printf '%s\n' "$version"
