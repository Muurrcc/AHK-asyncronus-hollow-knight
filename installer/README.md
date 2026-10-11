# Instalador de Asyncronus (MSI)

Instala **y actualiza** el mod multijugador (HKMP + Modding API para Unity 6) en Hollow Knight de Steam (build 1.5.12459).
Para el usuario: doble clic en `Asyncronus-X.Y.Z.msi`, "Instalar". Para actualizar: ejecutar el MSI nuevo. Para quitarlo: *Configuracion > Aplicaciones > Asyncronus (Hollow Knight multijugador)*.

## Que hace
1. Busca el juego (registro de Steam + `libraryfolders.vdf` + `appmanifest_367520.acf`); si no lo encuentra, pide la carpeta. `GAMEDIR="ruta"` en `msiexec` lo fuerza.
2. Comprueba que Hollow Knight esta cerrado.
3. Mira el hash de `Assembly-CSharp.dll`: original -> aplica el delta; ya parcheado -> solo actualiza el mod; otro -> explica como "Verificar integridad" en Steam y aborta sin tocar nada.
4. Copia de seguridad solo de lo que sobrescribe en `hollow_knight_Data\Managed-vanilla-backup` (+ `asyncronus-backup.txt`, el registro de lo cambiado).
5. Copia la API y el mod a `Managed\Mods\Asyncronus` (HKMP.dll + dependencias). Si hay una `Mods\HKMP` antigua del mismo mod, la aparta a `Managed-vanilla-backup\Mods-HKMP-anterior`.
6. Desinstalar/actualizar: restaura el backup, borra lo que anadio y su carpeta de mod. **Nunca** toca otros mods ni partidas.

## Decisiones
* **Por usuario, sin UAC.** Las carpetas de Steam son escribibles por el usuario. Si el juego esta en una carpeta protegida, el instalador lo dice y hay que lanzarlo desde una consola de administrador (`msiexec /i ...`).
* **MMHOOK_\* se generan en el PC del usuario** con MonoMod HookGen 21.4.29.1 (mismos pasos que el build de la Modding API: PrePatcher + HookGen + `--mmhook`). No se distribuyen: derivan del codigo del juego y asi ni el repo ni el MSI llevan nada de el. Se comprobo que el resultado es identico al compilado de referencia salvo 18 bytes (marca de tiempo/MVID); coste ~5 s.
* **Assembly-CSharp.dll**: solo se distribuye un delta (bsdiff propio, `patches/Assembly-CSharp.vanilla-to-api.patch`). El delta exige el hash del original y verifica el hash del resultado.
* No se incluye acceso directo al servidor dedicado (aun no hay binario del servidor).

## Compilar
Requisitos: .NET SDK 8+, WiX v4+ (`dotnet tool install --global wix --version 6.0.2`; WiX 7 exige aceptar la licencia OSMF, 6.x no), acceso a nuget.org la primera vez.

```powershell
.\build.ps1 -VanillaAssembly <Assembly-CSharp.dll original> `
            -ApiDir <carpeta con la Modding API ya compilada> `
            -ModDir <carpeta con HKMP.dll y dependencias; por defecto ..\dist> `
            -Version 1.0.0            # -> ..\dist\Asyncronus-1.0.0.msi
```
`-SkipDelta` reutiliza el `.patch` existente (no hacen falta los DLL para solo recompilar el MSI... salvo `-ApiDir` para las 4 librerias de terceros y los tamanos esperados).

## Estructura
* `src/Asyncronus.Setup` - logica (`Core/`) + CLI `AsyncronusSetup.exe install|uninstall|status|diff|apply` (pruebas y build).
* `src/Asyncronus.CA` - acciones personalizadas del MSI (WiX DTF, .NET Framework 4.8; comparte el codigo de `Core/`).
* `src/PrePatcher` - PrePatcher de la Modding API (MIT), sin cambios.
* `wix/` - `Asyncronus.wxs`, aviso (`license.rtf`, generado con `make_license.py`).
* `THIRD-PARTY-NOTICES.md` - licencias de terceros.
* `wix/Asyncronus.es-ES.wxl` - textos de las pantallas (sobreescriben WixUI; `build.ps1` lo pasa con `-loc`). Imagenes e icono: `assets/` (variantes claras; ver `assets/THEME.md`).
