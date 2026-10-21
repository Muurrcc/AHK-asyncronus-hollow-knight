# shellcheck shell=bash
# shellcheck disable=SC2012,SC1091,SC2028
# Shared functions for install.sh / uninstall.sh (Asyncronus, Linux). Sourced, not executed.
# Logic mirrors installer/src/Asyncronus.Setup/Core (ModInstaller.cs, GameLocator.cs); the backup folder and
# manifest format are the same as the Windows installer's.

APP_ID=367520
MOD_FOLDER=Asyncronus
BACKUP_NAME=Managed-vanilla-backup
MANIFEST_NAME=asyncronus-backup.txt

if [ -t 2 ]; then C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'; C_OFF=$'\033[0m'; else C_RED=; C_GRN=; C_YEL=; C_OFF=; fi
# ASY_LOG (optional): every message is also appended there, without colours (install log for bug reports).
_tolog() { if [ -n "${ASY_LOG:-}" ]; then printf '%s\n' "$*" >> "$ASY_LOG" 2>/dev/null; fi; return 0; }
log()  { printf '%s\n' "$*" >&2; _tolog "$*"; }
vlog() { if [ "${VERBOSE:-0}" -eq 1 ]; then printf '[v] %s\n' "$*" >&2; fi; _tolog "[v] $*"; return 0; }
ok()   { printf '%s%s%s\n' "$C_GRN" "$*" "$C_OFF" >&2; _tolog "$*"; }
warn() { printf '%sWARNING: %s%s\n' "$C_YEL" "$*" "$C_OFF" >&2; _tolog "WARNING: $*"; }
die()  {
    printf '%sERROR: %s%s\n' "$C_RED" "$*" "$C_OFF" >&2; _tolog "ERROR: $*"
    if [ -n "${ASY_LOG:-}" ]; then printf 'Log saved in: %s\nIf you need help, run "./install.sh --diagnose" and send its output together with that log.\n' "$ASY_LOG" >&2; fi
    exit 1
}

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
        "$HOME/snap/steam/common/.local/share/Steam" \
        "/home/deck/.local/share/Steam" \
        "${SUDO_USER:+$(getent passwd "$SUDO_USER" 2>/dev/null | cut -d: -f6)/.local/share/Steam}"; do
        if [ -n "$d" ] && [ -d "$d" ]; then printf '%s\n' "$d"; fi
    done
    return 0
}

# Libraries on external/SD drives that are not always listed in libraryfolders.vdf (best effort, globs may match nothing).
extra_libraries() {
    local d
    for d in /run/media/*/* /run/media/mmcblk0p1 /media/*/* /mnt/*; do
        if [ -d "$d/steamapps" ]; then printf '%s\n' "$d"; fi
        if [ -d "$d/SteamLibrary/steamapps" ]; then printf '%s\n' "$d/SteamLibrary"; fi
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
                # old format (before 2021): "1"  "/path/to/library"
                sed -n 's/^[[:space:]]*"[0-9][0-9]*"[[:space:]]*"\(\/.*\)"[[:space:]]*$/\1/p' "$vdf"
            done
            printf '%s\n' "$root"
        done < <(steam_roots)
        extra_libraries
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
        # fallback: any folder in common/ that looks like Hollow Knight (renamed folder, wrong case...)
        for inst in "$lib"/steamapps/common/*[Hh]ollow*[Kk]night*; do
            if [ -d "$inst" ] && g=$(normalize_game "$inst"); then printf '%s\n' "$g"; return 0; fi
        done
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
        if [ "$pid" != "$$" ] && [ "$pid" != "$PPID" ]; then RUNNING_PID=$pid; return 0; fi
    done
    return 1
}

# Where ModLog.txt / saves live for this game folder (Proton prefix next to the library, or native Unity folder).
modlog_candidates() { # game_dir
    local lib
    lib=$(dirname -- "$(dirname -- "$1")")   # <library>/steamapps
    printf '%s
' "$lib/compatdata/$APP_ID/pfx/drive_c/users/steamuser/AppData/LocalLow/Team Cherry/Hollow Knight/ModLog.txt"
    printf '%s
' "${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Team Cherry/Hollow Knight/ModLog.txt"
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

# ---------------------------------------------------------------- diagnostics
# Prints everything needed to understand a failed install on someone else's machine. Never changes anything and never
# dies. Needs SCRIPT_DIR (folder of install.sh) and optionally GAME_ARG. Output goes to stdout.
diagnose_report() {
    local ini="$SCRIPT_DIR/payload/payload.ini" f n lib root game="" have h van pat managed backup pkgmnt
    _ini() { sed -n "s/^$1=//p" "$ini" 2>/dev/null | head -n1 | tr -d '\r'; }
    _sec() { printf '\n== %s ==\n' "$1"; }

    _sec "Asyncronus diagnose ($(date '+%Y-%m-%d %H:%M:%S %z'))"
    echo "package folder: $SCRIPT_DIR"
    echo "mod version:    $(_ini ModVersion)   game build expected: $(_ini GameBuild)"

    _sec "System"
    echo "uname:      $(uname -a)"
    if [ -r /etc/os-release ]; then echo "distro:     $(. /etc/os-release && echo "${PRETTY_NAME:-${NAME:-?}} (ID=${ID:-?} VARIANT_ID=${VARIANT_ID:-})")"; fi
    echo "user:       $(id -un 2>/dev/null) uid=$(id -u 2>/dev/null) HOME=$HOME SUDO_USER=${SUDO_USER:-}"
    echo "shell:      bash ${BASH_VERSION:-?}   SHELL=${SHELL:-}   LANG=${LANG:-}"
    echo "steam deck: $([ -d /home/deck ] && echo 'yes (/home/deck exists)' || echo no)"
    echo "session:    XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-} DISPLAY=${DISPLAY:-} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-}"
    echo "TMPDIR:     ${TMPDIR:-/tmp}   free: $(df -Pk "${TMPDIR:-/tmp}" 2>/dev/null | awk 'NR==2{printf "%d MB", $4/1024}')"
    pkgmnt=$(df -P "$SCRIPT_DIR" 2>/dev/null | awk 'NR==2{print $6}')
    echo "package on: ${pkgmnt:-?}   mount options: $(awk -v m="$pkgmnt" '$2==m{print $3" "$4}' /proc/mounts 2>/dev/null | tail -n1)"

    _sec "Tools"
    for n in sha256sum shasum tar sed awk mktemp readlink pgrep getent df cp mv find; do
        printf '%-10s %s\n' "$n" "$(command -v "$n" 2>/dev/null || echo 'NOT FOUND')"
    done

    _sec ".NET"
    if [ -d "$HOME/.dotnet" ]; then PATH="$PATH:$HOME/.dotnet"; fi
    echo "dotnet:     $(command -v dotnet 2>/dev/null || echo 'NOT FOUND')   DOTNET_ROOT=${DOTNET_ROOT:-}"
    if command -v dotnet >/dev/null 2>&1; then
        dotnet --list-runtimes 2>&1 | sed 's/^/  runtime: /'
        dotnet --list-sdks 2>&1 | sed 's/^/  sdk:     /'
        echo "  (the installer needs a Microsoft.NETCore.App runtime >= 8)"
    fi

    _sec "Package integrity"
    for f in install.sh lib.sh uninstall.sh tools/AsyncronusDelta.dll tools/AsyncronusDelta.runtimeconfig.json payload/payload.ini \
             payload/Assembly-CSharp.patch payload/tools/PrePatcher.exe payload/tools/MonoMod.RuntimeDetour.HookGen.exe \
             payload/tools/Mono.Cecil.dll payload/api payload/mod/HKMP.dll; do
        if [ -e "$SCRIPT_DIR/$f" ]; then printf 'ok       %s\n' "$f"; else printf 'MISSING  %s\n' "$f"; fi
    done
    for f in install.sh lib.sh uninstall.sh; do
        if [ -f "$SCRIPT_DIR/$f" ] && [ "$(tr -cd '\r' < "$SCRIPT_DIR/$f" | wc -c)" -gt 0 ]; then
            echo "PROBLEM  $f has Windows line endings (CRLF). Fix: sed -i 's/\\r\$//' *.sh   (or download the package again)"
        fi
    done
    if [ ! -x "$SCRIPT_DIR/install.sh" ]; then echo "note     install.sh is not executable: run it with 'bash install.sh' (or chmod +x install.sh)"; fi
    echo "payload.ini:"; sed 's/\r$//;s/^/  /' "$ini" 2>/dev/null

    _sec "Steam"
    while IFS= read -r root; do echo "steam root: $root"; done < <(steam_roots)
    echo "libraries:"
    while IFS= read -r lib; do
        have=""
        if [ -f "$lib/steamapps/appmanifest_$APP_ID.acf" ]; then
            have="Hollow Knight manifest present (installdir=$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$lib/steamapps/appmanifest_$APP_ID.acf" | head -n1))"
        fi
        echo "  $lib: ${have:-no Hollow Knight manifest}"
        if [ -d "$lib/steamapps/compatdata/$APP_ID" ]; then echo "    compatdata/$APP_ID exists (Proton prefix)"; fi
    done < <(steam_libraries)
    for f in "$HOME/.local/share/Steam/config/config.vdf" "$HOME/.steam/steam/config/config.vdf" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/config/config.vdf"; do
        if [ -f "$f" ]; then echo "compat tool entry for $APP_ID in $f:"; grep -A6 "\"$APP_ID\"" "$f" 2>/dev/null | grep -E '"name"' | head -n1 | sed 's/^/  /'; fi
    done

    _sec "Game"
    if [ -n "${GAME_ARG:-}" ]; then
        game=$(normalize_game "$GAME_ARG") || echo "--game-dir '$GAME_ARG' is not a Hollow Knight folder (hollow_knight_Data/Managed/Assembly-CSharp.dll not found)"
    else
        game=$(autodetect_game) || echo "autodetect: Hollow Knight NOT found (use --game-dir)"
    fi
    if [ -n "$game" ]; then
        managed="$game/hollow_knight_Data/Managed"; backup="$game/hollow_knight_Data/$BACKUP_NAME"
        echo "game dir:   $game"
        echo "flavour:    $(game_flavour "$game")"
        ls -ld "$game" "$managed" 2>&1 | sed 's/^/  /'
        if [ -w "$managed" ]; then echo "writable:   yes"; else echo "writable:   NO (fix permissions/ownership of the game folder)"; fi
        echo "disk free:  $(df -Pk "$game" 2>/dev/null | awk 'NR==2{printf "%d MB on %s", $4/1024, $1}')"
        if [ -f "$managed/Assembly-CSharp.dll" ]; then
            h=$(sha256_of "$managed/Assembly-CSharp.dll"); van=$(_ini VanillaSha256); pat=$(_ini PatchedSha256)
            echo "Assembly-CSharp.dll sha256: $h   ($(wc -c < "$managed/Assembly-CSharp.dll" | tr -d ' ') bytes)"
            echo "  expected original (Windows build): $van"
            echo "  expected patched:                  $pat"
            if [ "$h" = "$van" ]; then echo "  => ORIGINAL, ready to install"
            elif [ "$h" = "$pat" ]; then echo "  => already PATCHED"
            else echo "  => DIFFERENT (other build / native Linux build / other Modding API)"; fi
        fi
        for n in MMHOOK_Assembly-CSharp.dll MMHOOK_PlayMaker.dll MMHOOK_TeamCherry.TK2D.dll MMHOOK_TeamCherry.Cinematics.dll Mono.Cecil.dll MonoMod.Utils.dll MonoMod.RuntimeDetour.dll unityscenerepacker.dll; do
            if [ -f "$managed/$n" ]; then printf '  %-42s present (%s bytes)\n' "$n" "$(wc -c < "$managed/$n" | tr -d ' ')"; else printf '  %-42s absent\n' "$n"; fi
        done
        echo "Managed/Mods:"; ls -la "$managed/Mods" 2>&1 | sed 's/^/  /'
        echo "Managed/Mods/$MOD_FOLDER:"; ls -la "$managed/Mods/$MOD_FOLDER" 2>&1 | sed 's/^/  /'
        echo "backup folder: $backup"; ls -la "$backup" 2>&1 | sed 's/^/  /'
        if [ -f "$backup/$MANIFEST_NAME" ]; then echo "manifest:"; tr -d '\r' < "$backup/$MANIFEST_NAME" | sed 's/^/  /'; fi
        echo "damaged (0-byte) dlls in Managed: $(find "$managed" -maxdepth 1 -name '*.dll' -size 0 2>/dev/null | wc -l)"
        if game_running; then echo "game process running: yes (pid ${RUNNING_PID:-?})"; else echo "game process running: no"; fi

        _sec "ModLog"
        while IFS= read -r f; do
            if [ -f "$f" ]; then
                echo "ModLog: $f ($(wc -c < "$f" | tr -d ' ') bytes, modified $(date -r "$f" '+%Y-%m-%d %H:%M' 2>/dev/null))"
                echo "  lines mentioning Asyncronus/HKMP: $(grep -ciE 'asyncronus|hkmp' "$f" 2>/dev/null)   lines with error/exception: $(grep -ciE 'exception|error' "$f" 2>/dev/null)"
                echo "  --- last 40 lines ---"; tail -n 40 "$f" | sed 's/^/  | /'
            else
                echo "ModLog not found: $f"
            fi
        done < <(modlog_candidates "$game")
    fi
    _sec "Installer logs"
    for f in "${XDG_STATE_HOME:-$HOME/.local/state}/asyncronus-install.log" "${XDG_STATE_HOME:-$HOME/.local/state}/asyncronus-error-log.txt"; do
        if [ -f "$f" ]; then echo "--- $f (last 40 lines) ---"; tail -n 40 "$f" | sed 's/^/  | /'; else echo "not found: $f"; fi
    done
    echo; echo "== end of diagnose =="
}
