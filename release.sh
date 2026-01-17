#!/bin/bash

# ModelingEvolution.Mjpeg Release Script
# Usage: ./release.sh [--patch|--minor|--major|--version X.X.X]

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

print_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }

get_latest_version() {
    local latest_tag=$(git tag -l "v*" | sort -V | tail -1)
    if [ -n "$latest_tag" ]; then
        echo "${latest_tag#v}"
    else
        echo "0.0.0"
    fi
}

calculate_next_version() {
    local current_version=$1
    local bump_type=$2

    IFS='.' read -ra VERSION_PARTS <<< "$current_version"
    local major="${VERSION_PARTS[0]:-0}"
    local minor="${VERSION_PARTS[1]:-0}"
    local patch="${VERSION_PARTS[2]:-0}"

    case "$bump_type" in
        major)
            major=$((major + 1))
            minor=0
            patch=0
            ;;
        minor)
            minor=$((minor + 1))
            patch=0
            ;;
        patch)
            patch=$((patch + 1))
            ;;
    esac

    echo "$major.$minor.$patch"
}

main() {
    local bump_type="patch"
    local custom_version=""

    while [[ $# -gt 0 ]]; do
        case $1 in
            --patch) bump_type="patch"; shift ;;
            --minor) bump_type="minor"; shift ;;
            --major) bump_type="major"; shift ;;
            --version) custom_version="$2"; shift 2 ;;
            -h|--help)
                echo "Usage: $0 [--patch|--minor|--major|--version X.X.X]"
                exit 0
                ;;
            *) print_error "Unknown option: $1"; exit 1 ;;
        esac
    done

    # Must be in a git repo
    if ! git rev-parse --git-dir > /dev/null 2>&1; then
        print_error "Not in a git repository"
        exit 1
    fi

    # Abort if uncommitted changes
    if ! git diff-index --quiet HEAD --; then
        print_error "Uncommitted changes. Commit first."
        exit 1
    fi

    local current_version=$(get_latest_version)
    print_info "Current version: $current_version"

    local next_version
    if [ -n "$custom_version" ]; then
        next_version="$custom_version"
    else
        next_version=$(calculate_next_version "$current_version" "$bump_type")
    fi

    local tag_name="v$next_version"

    # Check tag doesn't exist
    if git tag -l "$tag_name" | grep -q "$tag_name"; then
        print_error "Tag $tag_name already exists"
        exit 1
    fi

    print_info "Creating tag: $tag_name"
    git tag -a "$tag_name" -m "Release $next_version"

    print_info "Pushing tag to origin..."
    git push origin "$tag_name"

    print_info "Done! Monitor: https://github.com/modelingevolution/mjpeg/actions"
}

main "$@"
