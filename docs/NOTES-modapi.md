# Hollow Knight Modding API (Unity 6) - notas

Fecha: 2026-10-08. Juego: HK 1.5.12459, Unity 6000.0.61f1, vanilla (origen X:\SteamLibrary\steamapps\common\Hollow Knight, NO tocado).

## Fuente
- Repo: https://github.com/leesiuuuu/HK-Modding-API rama `unity6-support` (PR hk-modding/api#174, abierto)
- Commit usado: c6d6079684dd79ed60924b10a3902d74c7fd9f6b
- Clon en `E:\HKMP-Enemies\modapi`
- Version reportada en ModLog: `Modding API: 0.0.0.0-77`
- No se uso PR #170 (KunalGehlot, cerrado).

## Build (exacto)
```bash
cd /e/HKMP-Enemies
git clone -b unity6-support https://github.com/leesiuuuu/HK-Modding-API.git modapi
cp -r game1/hollow_knight_Data/Managed modapi/Vanilla          # Managed vanilla -> modapi/Vanilla
cd modapi
dotnet build PrePatcher -c Release
# 1a pasada falla (MSB3073 / 9009): PrePatcher/Output queda vacio porque el item
# TargetDirFiles se evalua antes de compilar. Basta repetir el build:
dotnet build Assembly-CSharp -c Release --runtime win-x64 -p:CopyDir=
dotnet build Assembly-CSharp -c Release --runtime win-x64 -p:CopyDir=
```
- `-p:CopyDir=` es OBLIGATORIO: por defecto el csproj copia OutputFinal a
  `C:/Program Files (x86)/Steam/steamapps/common/Hollow Knight/hollow_knight_Data/Managed`.
- `--runtime win-x64` para que copie `unityscenerepacker.dll` nativo.
- Resultado en `modapi\OutputFinal\`: Assembly-CSharp.dll (parcheado por PrePatcher + MonoMod),
  Assembly-CSharp.xml, MMHOOK_Assembly-CSharp.dll (con forwarders a MMHOOK_TeamCherry.*),
  MMHOOK_PlayMaker.dll, MMHOOK_TeamCherry.TK2D.dll, MMHOOK_TeamCherry.Cinematics.dll,
  Mono.Cecil.dll, MonoMod.RuntimeDetour.dll, MonoMod.Utils.dll, Newtonsoft.Json.dll
  (identico al vanilla del juego), unityscenerepacker.dll, README.md.
- mscorlib.dll del directorio `override` NO se instala (solo referencia de compilacion).

## Instalacion (game1 y game2)
```bash
cd /e/HKMP-Enemies
for g in game1 game2; do
  cp modapi/OutputFinal/*.dll modapi/OutputFinal/Assembly-CSharp.xml $g/hollow_knight_Data/Managed/
  echo 367520 > $g/steam_appid.txt
done
```
- Backup de Managed vanilla: `E:\HKMP-Enemies\vanilla-managed\` (restaurar = copiar encima).
- `steam_appid.txt` (367520) en la raiz de cada copia: evita que SteamAPI_RestartAppIfNecessary
  relance via Steam (que abriria la instalacion de X:). Steam cliente debe estar abierto (log:
  "Steam logged in as ...").

## Mod de prueba
- Fuente: `E:\HKMP-Enemies\testmod\` (TestMod.csproj net472, referencias a Managed de game1, Private=false).
- `dotnet build -c Release` en testmod -> `bin\Release\net472\TestMod.dll`
- Instalar en `<game>\hollow_knight_Data\Managed\Mods\TestMod\TestMod.dll`.

## Verificacion
Arrancar: `<game>\hollow_knight.exe -logFile E:/HKMP-Enemies/<game>-player.log`
ModLog: `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight\ModLog.txt`
Resultado en game1 y game2 (proceso verificado con ruta E:\HKMP-Enemies\gameN\hollow_knight.exe):
```
[INFO]:[TestMod] - TESTMOD_INIT_OK Unity=6000.0.61f1
[INFO]:[API] - Finished loading mods:
[INFO]:[API] - Modding API: 0.0.0.0-77
[INFO]:[API] - TestMod : 0.0.1
```
Sin excepciones en player log ni ModLog tras ~30 s en menu principal. No se entro en partida.

## Pendiente / avisos
- ModLog.txt, saves y ModSettings estan en LocalLow comun a ambas copias (y a la instalacion Steam):
  dos instancias simultaneas compartiran/sobrescribiran ModLog y saves. Backup de saves en
  `E:\HKMP-Enemies\save-backup\` (user1.dat intacto tras las pruebas).
- No probado: cargar partida / guardar con mods (PR #174 cambia LoadGame), menu de mods in-game,
  dos instancias a la vez.
- PR #174 no esta mergeado: mods publicados para v77 podrian depender de tipos movidos a
  TeamCherry.* (cubierto por forwarders, sin probar con mods reales como HKMP).
