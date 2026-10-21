# shellcheck shell=bash
# Shared functions for install.sh / uninstall.sh (Asyncronus, Linux). Sourced, not executed.
# Logic mirrors installer/src/Asyncronus.Setup/Core (ModInstaller.cs, GameLocator.cs); the backup folder and
# manifest format are the same as the Windows installer's.

APP_ID=367520
MOD_FOLDER=Asyncronus
BACKUP_NAME=Managed-vanilla-backup
MANIFEST_NAME=asyncronus-backup.txt

if [ -t 2 ]; then C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'; C_OFF=$'\033[0m'; else C_RED=; C_GRN=; C_YEL=; C_OFF=; fi
log()  { printf '%s\n' "$*" >&2; }
ok()   { printf '%s%s%s\n' "$C_GRN" "$*" "$C_OFF" >&2; }
warn() { printf '%sWARNING: %s%s\n' "$C_YEL" "$*" "$C_OFF" >&2; }
die()  { printf '%sERROR: %s%s\n' "$C_RED" "$*" "$C_OFF" >&2; exit 1; }

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
    else die "Neither sha256sum nor shasum found."; fi
}

# Copy to a temp name and rename, so a half-written file never replaces a good one.
atomic_copy() {
    cp -f -- "$1" "$2.asy-tmp" && mv -f -- "$2.asy-tmp" "$2"
}

# ---------------------------------------------------------------- game location
# Prints candidate Steam roots (existing directories), one per line.
steam_roots() {
    local d
    for d in \
        "${XDG_DATA_HOME:-$HOME/.local/share}/Steam" \
        "$HOME/.steam/steam" \
        "$HOME/.steam/root" \
        "$HOME/.steam/debian-installation" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
        "$HOME/.var/app/com.valvesoftware.Steam/data/Steam" \
        "$HOME/.var/app/com.valvesoftware.Steam/.steam/steam" \
        "$HOME/snap/steam/common/.local/share/Steam"; do
        if [ -d "$d" ]; then printf '%s\n' "$d"; fi
    done
    return 0
}

# Prints every Steam library (paths from libraryfolders.vdf + the roots), de-duplicated by real path.
steam_libraries() {
    local root vdf p real seen=""
    {
        while IFS= read -r root; do
            for vdf in "$root/steamapps/libraryfolders.vdf" "$root/config/libraryfolders.vdf"; do
                [ -f "$vdf" ] || continue
                sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf" | sed 's/\\\\/\\/g'
            done
            printf '%s\n' "$root"
        done < <(steam_roots)
    } | while IFS= read -r p; do
        [ -n "$p" ] || continue
        printf '%s\n' "$p"
    done | {
        while IFS= read -r p; do
            real=$(readlink -f -- "$p" 2>/dev/null || printf '%s' "$p")
            case "$seen" in *"|$real|"*) continue ;; esac
            seen="$seen|$real|"
            if [ -d "$p/steamapps" ]; then printf '%s\n' "$p"; fi
        done
    }
    return 0
}

# Accepts the game folder, hollow_knight_Data or Managed; prints the game folder or returns 1.
normalize_game() {
    local d="${1:-}"
    [ -n "$d" ] || return 1
    d=$(readlink -f -- "$d" 2>/dev/null) || return 1
    for _ in 1 2 3; do
        if [ -f "$d/hollow_knight_Data/Managed/Assembly-CSharp.dll" ]; then printf '%s\n' "$d"; return 0; fi
        [ "$d" = / ] && return 1
        d=$(dirname -- "$d")
    done
    return 1
}

autodetect_game() {
    local lib acf inst g
    while IFS= read -r lib; do
        inst="Hollow Knight"
        acf="$lib/steamapps/appmanifest_$APP_ID.acf"
        if [ -f "$acf" ]; then
            g=$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$acf" | head -n1)
            if [ -n "$g" ]; then inst="$g"; fi
        fi
        if g=$(normalize_game "$lib/steamapps/common/$inst"); then printf '%s\n' "$g"; return 0; fi
    done < <(steam_libraries)
    return 1
}

# Windows build (runs under Proton) or native Linux build?
game_flavour() {
    if [ -f "$1/hollow_knight.exe" ]; then echo "Windows build (Proton)"
    elif [ -f "$1/hollow_knight.x86_64" ] || [ -f "$1/hollow_knight" ]; then echo "native Linux build"
    else echo "unknown"; fi
}

game_running() {
    # any process whose command line mentions the HK executable (native or Proton), except ourselves
    local pid
    command -v pgrep >/dev/null 2>&1 || return 1
    for pid in $(pgrep -f 'hollow_knight(\.x86_64|\.exe)' 2>/dev/null); do
        [ "$pid" = "$$" ] || return 0
    done
    return 1
}

# ---------------------------------------------------------------- manifest
# Lines: KIND<TAB>NAME<TAB>EXTRA. R=restore from backup, A=added by us, K=no original copy, C=folder we created.
man_has_file() { # name
    [ -f "$MANIFEST" ] || return 1
    tr -d '\r' < "$MANIFEST" | awk -F'\t' -v n="$1" '$1 ~ /^[RAK]$/ && tolower($2)==tolower(n){f=1} END{exit f?0:1}'
}
man_has() { # kind name
    [ -f "$MANIFEST" ] || return 1
    tr -d '\r' < "$MANIFEST" | awk -F'\t' -v k="$1" -v n="$2" '$1==k && tolower($2)==tolower(n){f=1} END{exit f?0:1}'
}
man_add() { # kind name extra
    if [ ! -f "$MANIFEST" ]; then
        printf '# Asyncronus: registro de ficheros modificados (R=restaurar de la copia, A=anadido por nosotros, K=sin copia, C=carpeta creada)\n' > "$MANIFEST"
    fi
    printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$MANIFEST"
}

# ---------------------------------------------------------------- uninstall / restore
# Needs GAME, MANAGED, BACKUP, MANIFEST. Never touches other mods or saves.
do_uninstall() {
    [ -d "$MANAGED" ] || { log "The game folder no longer exists; nothing to restore."; return 0; }
    local kind name extra src target
    if [ -f "$MANIFEST" ]; then
        while IFS=$'\t' read -r kind name extra; do
            case "$kind" in R|A) ;; *) continue ;; esac
            target="$MANAGED/$name"; src="$BACKUP/$name"
            if [ "$kind" = R ]; then
                if [ -f "$src" ]; then
                    log "Restoring $name"; atomic_copy "$src" "$target" || die "Could not restore $name"
                else
                    warn "backup copy of $name is missing; left as is. Use 'Verify integrity of local files' in Steam."
                fi
            elif [ -f "$target" ]; then
                log "Removing $name"; rm -f -- "$target"
            fi
        done < <(tr -d '\r' < "$MANIFEST")
        while IFS=$'\t' read -r kind name extra; do
            if [ "$kind" = K ]; then warn "$name could not be restored (there was no original copy). Use 'Verify integrity of local files' in Steam."; fi
        done < <(tr -d '\r' < "$MANIFEST")
    else
        log "No manifest found ($MANIFEST): nothing to restore."
    fi

    if [ -d "$MANAGED/Mods/$MOD_FOLDER" ]; then log "Removing Mods/$MOD_FOLDER"; rm -rf -- "${MANAGED:?}/Mods/$MOD_FOLDER"; fi
    if man_has C Mods && [ -d "$MANAGED/Mods" ] && [ -z "$(ls -A "$MANAGED/Mods")" ]; then rmdir "$MANAGED/Mods"; fi

    if [ -f "$MANIFEST" ]; then
        while IFS=$'\t' read -r kind name extra; do
            if [ "$kind" = R ] && [ "$extra" = new ]; then rm -f -- "$BACKUP/$name"; fi
        done < <(tr -d '\r' < "$MANIFEST")
        rm -f -- "$MANIFEST"
    fi
    if [ -d "$BACKUP" ] && [ -z "$(ls -A "$BACKUP")" ]; then rmdir "$BACKUP"; fi
    return 0
}

# Resolve GAME (arg or autodetect) and set the derived paths. Sets GAME, MANAGED, BACKUP, MANIFEST.
resolve_game() {
    local arg="${1:-}"
    if [ -n "$arg" ]; then
        GAME=$(normalize_game "$arg") || die "Not a Hollow Knight folder: $arg (hollow_knight_Data/Managed/Assembly-CSharp.dll not found)."
    else
        GAME=$(autodetect_game) || die "Hollow Knight not found in any Steam library. Pass the folder: --game-dir \"/path/to/Hollow Knight\""
    fi
    MANAGED="$GAME/hollow_knight_Data/Managed"
    BACKUP="$GAME/hollow_knight_Data/$BACKUP_NAME"
    MANIFEST="$BACKUP/$MANIFEST_NAME"
}
