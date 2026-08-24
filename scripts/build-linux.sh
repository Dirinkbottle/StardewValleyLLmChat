#!/usr/bin/env bash

set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
game_path="${STARDEW_GAME_PATH:-${GAME_PATH:-}}"
output_dir="${OUTPUT_DIR:-${repo_root}/artifacts/linux}"

usage() {
    printf '%s\n' \
        "Usage: ./scripts/build-linux.sh [options]" \
        "" \
        "Options:" \
        "  --game-path PATH       Stardew Valley directory containing game and SMAPI DLLs." \
        "  --configuration NAME   Build configuration (Debug or Release; default: Release)." \
        "  --output PATH          Directory for verified release zip files." \
        "  -h, --help             Show this help message." \
        "" \
        "Environment fallbacks:" \
        "  STARDEW_GAME_PATH, GAME_PATH, CONFIGURATION, OUTPUT_DIR"
}

while (($# > 0)); do
    case "$1" in
        --game-path)
            [[ $# -ge 2 ]] || { printf 'Missing value for --game-path.\n' >&2; exit 2; }
            game_path="$2"
            shift 2
            ;;
        --configuration)
            [[ $# -ge 2 ]] || { printf 'Missing value for --configuration.\n' >&2; exit 2; }
            configuration="$2"
            shift 2
            ;;
        --output)
            [[ $# -ge 2 ]] || { printf 'Missing value for --output.\n' >&2; exit 2; }
            output_dir="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown option: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "$configuration" != "Debug" && "$configuration" != "Release" ]]; then
    printf 'Unsupported configuration: %s (expected Debug or Release).\n' "$configuration" >&2
    exit 2
fi

detect_game_path() {
    local candidate
    local -a candidates=(
        "${HOME:-}/.local/share/Steam/steamapps/common/Stardew Valley"
        "${HOME:-}/.steam/steam/steamapps/common/Stardew Valley"
        "${HOME:-}/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Stardew Valley"
        "${HOME:-}/GOGGames/StardewValley/game"
    )

    for candidate in "${candidates[@]}"; do
        if [[ -f "${candidate}/Stardew Valley.dll" && -f "${candidate}/StardewModdingAPI.dll" ]]; then
            printf '%s' "$candidate"
            return 0
        fi
    done

    return 1
}

if [[ -z "$game_path" ]]; then
    game_path="$(detect_game_path || true)"
fi

if [[ -z "$game_path" ]]; then
    printf '%s\n' \
        "Unable to find Stardew Valley with SMAPI installed." \
        "Set STARDEW_GAME_PATH or pass --game-path '/path/to/Stardew Valley'." >&2
    exit 1
fi

if [[ ! -f "${game_path}/Stardew Valley.dll" ]]; then
    printf 'Missing game assembly: %s\n' "${game_path}/Stardew Valley.dll" >&2
    exit 1
fi

if [[ ! -f "${game_path}/StardewModdingAPI.dll" ]]; then
    printf 'Missing SMAPI assembly: %s\n' "${game_path}/StardewModdingAPI.dll" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    printf 'dotnet was not found. Install the .NET 6 SDK first.\n' >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    printf 'python3 was not found; it is required to verify the release zip.\n' >&2
    exit 1
fi

ci_build=false
if [[ "${CI:-false}" == "true" ]]; then
    ci_build=true
fi

printf 'Building StardewMod on Linux\n'
printf '  configuration: %s\n' "$configuration"
printf '  game path:     %s\n' "$game_path"
printf '  output:        %s\n' "$output_dir"

cd -- "$repo_root"
dotnet restore StardewMod.sln
dotnet build StardewMod.sln \
    --configuration "$configuration" \
    --no-restore \
    -p:GamePath="$game_path" \
    -p:EnableModDeploy=false \
    -p:EnableModZip=true \
    -p:ContinuousIntegrationBuild="$ci_build"

build_dir="${repo_root}/bin/${configuration}/net6.0"
mapfile -d '' zip_files < <(find "$build_dir" -maxdepth 1 -type f -name '*.zip' -print0 | sort -z)
if ((${#zip_files[@]} == 0)); then
    printf 'No release zip was produced under %s.\n' "$build_dir" >&2
    exit 1
fi

mkdir -p -- "$output_dir"
for zip_file in "${zip_files[@]}"; do
    destination="${output_dir}/$(basename -- "$zip_file")"
    install -m 0644 -- "$zip_file" "$destination"
    python3 "${script_dir}/verify-mod-package.py" "$destination"
    printf 'Verified artifact: %s\n' "$destination"
done
