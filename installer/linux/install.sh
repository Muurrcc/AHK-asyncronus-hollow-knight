#!/usr/bin/env bash
# Asyncronus installer for Linux (Steam native or Proton). Counterpart of the Windows MSI.
#
#   ./install.sh [--game-dir DIR] [--status] [--force] [--keep-work]
#
#   --game-dir DIR  Hollow Knight folder (also accepts hollow_knight_Data or Managed). Env GAMEDIR does the same.
#   --status        Only report where the game is and the state of Assembly-CSharp.dll; changes nothing.
#   --force         Do not abort if a Hollow Knight process seems to be running.
#   --keep-work     Keep the temporary folder (debugging).
#
# What it does (same steps as the MSI): find the game, check sha256 of Assembly-CSharp.dll (original -> apply the
# delta; already patched -> only update the mod; anything else -> abort without touching anything), back up what it
# overwrites into hollow_knight_Data/Managed-vanilla-backup (+ asyncronus-backup.txt), generate MMHOOK_* with
# PrePatcher + MonoMod HookGen run by the .NET runtime, copy the API and the mod to Managed/Mods/Asyncronus.
# Requires: .NET runtime/SDK 8 or newer ("dotnet" in PATH, or in ~/.dotnet), sha256sum, bash 4+.
set -uo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
. "$SCRIPT_DIR/lib.sh"

GAME_ARG="${GAMEDIR:-}"
STATUS_ONLY=0
FORCE=0
KEEP_WORK=0
while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir) [ $# -ge 2 ] || die "--game-dir needs a value"; GAME_ARG=$2; shift 2 ;;
        --status) STATUS_ONLY=1; shift ;;
        --force) FORCE=1; shift ;;
        --keep-work) KEEP_WORK=1; shift ;;
        -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "Unknown option: $1 (see --help)" ;;
    esac
done

PAYLOAD="$SCRIPT_DIR/payload"
DELTA_DLL="$SCRIPT_DIR/tools/AsyncronusDelta.dll"
[ -f "$PAYLOAD/payload.ini" ] || die "payload/payload.ini not found. Use the release package (Asyncronus-linux-X.Y.Z.tar.gz) or run make-package.sh."

ini() { sed -n "s/^$1=//p" "$PAYLOAD/payload.ini" | head -n1 | tr -d '\r'; }
MOD_VERSION=$(ini ModVersion)
VANILLA_SHA=$(ini VanillaSha256)
PATCHED_SHA=$(ini PatchedSha256)
HOOK_NAMES=(MMHOOK_Assembly-CSharp.dll MMHOOK_PlayMaker.dll MMHOOK_TeamCherry.TK2D.dll MMHOOK_TeamCherry.Cinematics.dll)

OTHER_ASSEMBLY_MSG="Your Hollow Knight is not the version Asyncronus needs (build 1.5.12459, Unity 6), or it already has another Modding API or other changes in Assembly-CSharp.dll.
What to do: in Steam, right click Hollow Knight > Properties > Installed files > Verify integrity of local files, and in the Betas tab choose \"None\". When Steam finishes, run this installer again.
Native Linux build: its Assembly-CSharp.dll may differ from the Windows one, which the patch is made for. In that case force Proton: Properties > Compatibility > Force the use of a specific Steam Play compatibility tool (then Verify integrity so Steam downloads the Windows build).
Note: if you use Lumafly or another manager with its own Modding API, this installation is not compatible with it."

# ---------------------------------------------------------------- dotnet
[ -d "$HOME/.dotnet" ] && PATH="$PATH:$HOME/.dotnet"
export PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # no libicu needed (Steam Deck / minimal distros)
check_dotnet() {
    command -v dotnet >/dev/null 2>&1 || die ".NET not found. Install the .NET 8 (or newer) runtime or SDK (see docs/LINUX.md; on Steam Deck: https://dot.net/v1/dotnet-install.sh --channel 8.0 into ~/.dotnet)."
    local rt
    rt=$(dotnet --list-runtimes 2>/dev/null | sed -n 's/^Microsoft\.NETCore\.App \([0-9][0-9]*\)\..*/\1/p' | sort -n | tail -n1)
    [ -n "$rt" ] && [ "$rt" -ge 8 ] || die ".NET runtime 8 or newer is required (found: ${rt:-none}). Install dotnet-runtime-8.0 (or the SDK)."
}

# ---------------------------------------------------------------- locate game and state
resolve_game "$GAME_ARG"
log "Game: $GAME ($(game_flavour "$GAME"))"

asm_state() {
    local f="$MANAGED/Assembly-CSharp.dll" h
    [ -f "$f" ] || { echo Missing; return; }
    h=$(sha256_of "$f")
    if [ "$h" = "$VANILLA_SHA" ]; then echo Vanilla
    elif [ "$h" = "$PATCHED_SHA" ]; then echo Patched
    else echo Other; fi
}
STATE=$(asm_state)
log "State of Assembly-CSharp.dll: $STATE"
if [ "$STATUS_ONLY" -eq 1 ]; then
    [ -f "$MANIFEST" ] && log "Asyncronus manifest present: $MANIFEST"
    [ -f "$MANAGED/Mods/$MOD_FOLDER/HKMP.dll" ] && log "Mod folder present: Mods/$MOD_FOLDER"
    exit 0
fi
[ "$STATE" = Missing ] && die "Not a Hollow Knight folder (missing $MANAGED/Assembly-CSharp.dll)."
[ "$STATE" = Other ] && die "$OTHER_ASSEMBLY_MSG"
if [ "$FORCE" -ne 1 ] && game_running; then die "Hollow Knight is running. Close it completely and try again (or use --force)."; fi
[ -w "$MANAGED" ] || die "No write permission on $MANAGED."
check_dotnet
[ -f "$DELTA_DLL" ] || die "Missing tools/AsyncronusDelta.dll (incomplete package)."

WORK=$(mktemp -d "${TMPDIR:-/tmp}/asyncronus.XXXXXXXX") || die "Could not create a temporary folder."
cleanup() { if [ "$KEEP_WORK" -eq 1 ]; then log "Work folder kept: $WORK"; else rm -rf -- "$WORK"; fi; }
trap cleanup EXIT

# ---------------------------------------------------------------- prepare (game untouched so far)
PLAN_NAMES=()   # file name inside Managed
PLAN_SRCS=()    # where it comes from
plan_add() { PLAN_NAMES+=("$1"); PLAN_SRCS+=("$2"); }

if [ "$STATE" = Vanilla ]; then
    log "Applying the patch to Assembly-CSharp.dll..."
    dotnet "$DELTA_DLL" apply "$MANAGED/Assembly-CSharp.dll" "$PAYLOAD/Assembly-CSharp.patch" "$WORK/patched-Assembly-CSharp.dll" >/dev/null \
        || die "Could not apply the patch (is the game file the original?)."
    [ "$(sha256_of "$WORK/patched-Assembly-CSharp.dll")" = "$PATCHED_SHA" ] || die "The patched Assembly-CSharp.dll has an unexpected hash."
    plan_add Assembly-CSharp.dll "$WORK/patched-Assembly-CSharp.dll"
fi

for f in "$PAYLOAD"/api/*; do [ -f "$f" ] && plan_add "$(basename -- "$f")" "$f"; done

ALL_HOOKS=1
for h in "${HOOK_NAMES[@]}"; do [ -f "$MANAGED/$h" ] || ALL_HOOKS=0; done
VANILLA_FOR_HOOKS=""
if [ "$STATE" = Vanilla ]; then VANILLA_FOR_HOOKS="$MANAGED/Assembly-CSharp.dll"
elif [ -f "$BACKUP/Assembly-CSharp.dll" ] && [ "$(sha256_of "$BACKUP/Assembly-CSharp.dll")" = "$VANILLA_SHA" ]; then
    VANILLA_FOR_HOOKS="$BACKUP/Assembly-CSharp.dll"
fi

# Run a .NET Framework tool (PrePatcher / HookGen / MonoMod) with the .NET runtime. They are plain IL-only
# assemblies, so "dotnet tool.exe" works once a runtimeconfig.json sits next to them.
run_tool() { # dir exe args...
    local wd=$1 exe=$2; shift 2
    log "> $exe $*"
    ( cd -- "$wd" && dotnet "$exe" "$@" ) > "$WORK/tool.log" 2>&1
    local rc=$?
    if [ $rc -ne 0 ]; then
        local keep="${XDG_STATE_HOME:-$HOME/.local/state}/asyncronus-error-log.txt"
        mkdir -p -- "$(dirname -- "$keep")" 2>/dev/null
        { echo "Asyncronus installer error"; echo "Command: $exe $*"; echo "Exit code: $rc"; uname -a; dotnet --list-runtimes; echo; cat "$WORK/tool.log"; } > "$keep" 2>&1
        die "$exe failed (code $rc): $(grep -m1 -i exception "$WORK/tool.log" || head -c 300 "$WORK/tool.log"). Full details saved in: $keep"
    fi
}

generate_hooks() {
    local wd="$WORK/hooks" f n
    mkdir -p -- "$wd"
    # zero-length DLLs would kill HookGen with an unhelpful exception: tell the user which one is damaged
    local damaged=""
    for f in "$MANAGED"/*.dll; do [ -e "$f" ] && [ ! -s "$f" ] && damaged="$damaged $(basename -- "$f")"; done
    [ -z "$damaged" ] || die "These game files are damaged or empty:$damaged. In Steam: Properties > Installed files > Verify integrity, then run the installer again."

    log "Preparing hook generation..."
    for f in "$MANAGED"/*.dll; do
        [ -e "$f" ] || continue
        case "$(basename -- "$f")" in MMHOOK_*) continue ;; esac
        cp -f -- "$f" "$wd/"
    done
    cp -f -- "$VANILLA_FOR_HOOKS" "$wd/Assembly-CSharp.dll"                  # always the original
    cp -f -- "$PAYLOAD"/tools/* "$wd/"
    for f in "$PAYLOAD"/api/Mono*; do [ -f "$f" ] && cp -f -- "$f" "$wd/"; done   # Mono.Cecil / MonoMod.* for HookGen
    for n in PrePatcher MonoMod.RuntimeDetour.HookGen MonoMod; do
        [ -f "$wd/$n.exe" ] && printf '%s\n' '{"runtimeOptions":{"tfm":"net8.0","rollForward":"LatestMajor","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' > "$wd/$n.runtimeconfig.json"
    done

    run_tool "$wd" PrePatcher.exe Assembly-CSharp.dll Assembly-CSharp-prepatched.dll
    mv -f -- "$wd/Assembly-CSharp-prepatched.dll" "$wd/Assembly-CSharp.dll"
    for n in Assembly-CSharp PlayMaker TeamCherry.TK2D TeamCherry.Cinematics; do
        run_tool "$wd" MonoMod.RuntimeDetour.HookGen.exe --private "$n.dll"
    done
    run_tool "$wd" PrePatcher.exe --mmhook MMHOOK_Assembly-CSharp.dll MMHOOK_TeamCherry.TK2D.dll
    run_tool "$wd" PrePatcher.exe --mmhook MMHOOK_Assembly-CSharp.dll MMHOOK_TeamCherry.Cinematics.dll

    local expected len h
    for h in "${HOOK_NAMES[@]}"; do
        [ -f "$wd/$h" ] || die "Could not generate $h."
        expected=$(ini "HookSize.$h"); len=$(wc -c < "$wd/$h" | tr -d ' ')
        if [ -n "$expected" ]; then
            local diff=$(( len > expected ? len - expected : expected - len ))
            [ "$diff" -le $(( expected / 20 )) ] || die "Generated $h has an unexpected size ($len bytes). Verify game integrity in Steam."
        fi
        plan_add "$h" "$wd/$h"
    done
}

if [ "$STATE" = Vanilla ] || [ "$ALL_HOOKS" -eq 0 ]; then
    [ -n "$VANILLA_FOR_HOOKS" ] || die "Modding API files are missing and there is no copy of the original Assembly-CSharp.dll to regenerate them. In Steam: Verify integrity of local files, then run the installer again."
    generate_hooks
fi

# ---------------------------------------------------------------- from here on the game is modified
register_backup() { # name
    local name=$1 target="$MANAGED/$1" bfile="$BACKUP/$1"
    if man_has_file "$name"; then return 0; fi          # already registered by a previous install: keep the ORIGINAL state
    if [ ! -f "$target" ]; then man_add A "$name" ""; return 0; fi
    if [ "$name" = Assembly-CSharp.dll ]; then
        if [ -f "$bfile" ] && [ "$(sha256_of "$bfile")" = "$VANILLA_SHA" ]; then man_add R "$name" old
        elif [ "$STATE" = Vanilla ]; then cp -f -- "$target" "$bfile" && man_add R "$name" new
        else man_add K "$name" ""; fi                    # already patched and no original copy: cannot restore
        return 0
    fi
    if [ -f "$bfile" ]; then man_add R "$name" old
    else cp -f -- "$target" "$bfile" && man_add R "$name" new; fi
}

install_mod_folder() {
    local mods="$MANAGED/Mods" old dst target f
    mkdir -p -- "$mods" || return 1
    old="$mods/HKMP"
    if [ -f "$old/HKMP.dll" ]; then   # old Mods/HKMP of this same mod is set aside (other mods are never touched)
        dst="$BACKUP/Mods-HKMP-anterior"
        [ -e "$dst" ] && dst="$dst-$(date +%Y%m%d-%H%M%S)"
        log "Moving the old HKMP mod to $dst"
        mv -- "$old" "$dst" || return 1
    fi
    target="$mods/$MOD_FOLDER"
    mkdir -p -- "$target" || return 1
    find "$target" -maxdepth 1 -type f -exec rm -f -- {} +    # replace the contents of OUR folder only
    for f in "$PAYLOAD"/mod/*; do
        [ -f "$f" ] || continue
        log "Mod: $(basename -- "$f")"
        atomic_copy "$f" "$target/$(basename -- "$f")" || return 1
    done
}

apply_changes() {
    local i created_mods=0
    mkdir -p -- "$BACKUP" || return 1
    [ -d "$MANAGED/Mods" ] || created_mods=1
    register_backup Assembly-CSharp.dll || return 1      # always: it is the critical file
    for i in "${!PLAN_NAMES[@]}"; do register_backup "${PLAN_NAMES[$i]}" || return 1; done
    if [ "$created_mods" -eq 1 ] && ! man_has C Mods; then man_add C Mods ""; fi
    # the manifest is complete BEFORE touching anything: if something fails, do_uninstall can revert
    for i in "${!PLAN_NAMES[@]}"; do
        log "Copying ${PLAN_NAMES[$i]}"
        atomic_copy "${PLAN_SRCS[$i]}" "$MANAGED/${PLAN_NAMES[$i]}" || return 1
    done
    install_mod_folder || return 1
}

if ! apply_changes; then
    warn "Error: reverting changes..."
    do_uninstall
    die "Installation failed and was reverted."
fi

[ "$(sha256_of "$MANAGED/Assembly-CSharp.dll")" = "$PATCHED_SHA" ] || { do_uninstall; die "Final check of Assembly-CSharp.dll failed; changes reverted."; }
ok "Installation finished (Asyncronus ${MOD_VERSION:-?}). Start Hollow Knight from Steam."
log "To remove it: $SCRIPT_DIR/uninstall.sh"
