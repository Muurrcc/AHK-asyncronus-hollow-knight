#!/usr/bin/env bash
# Asyncronus uninstaller for Linux (Steam native or Proton).
# Restores the original files from hollow_knight_Data/Managed-vanilla-backup, removes what the installer added and
# Managed/Mods/Asyncronus. Never touches other mods or saves.
#
#   ./uninstall.sh [--game-dir DIR] [--force]
set -uo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
. "$SCRIPT_DIR/lib.sh"

GAME_ARG="${GAMEDIR:-}"
FORCE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir) [ $# -ge 2 ] || die "--game-dir needs a value"; GAME_ARG=$2; shift 2 ;;
        --force) FORCE=1; shift ;;
        -h|--help) sed -n '2,7p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "Unknown option: $1 (see --help)" ;;
    esac
done

resolve_game "$GAME_ARG"
log "Game: $GAME ($(game_flavour "$GAME"))"
if [ "$FORCE" -ne 1 ] && game_running; then die "Hollow Knight is running. Close it completely and try again (or use --force)."; fi

do_uninstall
ok "Uninstall finished. To be 100% sure the game is vanilla, you can also use Steam > Properties > Installed files > Verify integrity."
