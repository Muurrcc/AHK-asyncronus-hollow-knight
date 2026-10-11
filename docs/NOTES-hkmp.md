# HKMP sobre HK 1.5.12459 (Unity 6000.0.61f1) - notas fase 1

Fecha: 2026-10-08. Fuente: `E:\HKMP-Enemies\src` (base f7c35d9, cambios sin commitear). Modding API: ver [[NOTES-modapi.md]].
Nota: el juego es HK1 (no Silksong) en Unity 6; lo dicho en ANALISIS-ENTIDADES.md sec. 5 sobre Silksong es falso.

## Estado
- HKMP.dll y HKMPServer.exe compilan (0 errores) contra `game1\hollow_knight_Data\Managed`.
- HKMP 3.0.0 instalado en game1 y game2 (`Managed\Mods\HKMP\`): carga sin excepciones en ModLog.
- HKMPServer local (UDP 26950, FullSynchronisation=true) + 2 instancias a la vez: ambas abren el menu HKMP,
  conectan solas, reciben la partida del servidor y entran en Tutorial_01 (nueva partida). Se ven entrar mutuamente
  ("Player N entered scene"), P1 es scene host, P2 recibe "already in scene" de ~24 entidades, save updates fluyen.
- No verificado visualmente: nombre/sprite del jugador remoto (ambos estaban en el spawn).

## Build
```bash
cd /e/HKMP-Enemies/src && dotnet build HKMP.sln -c Release
```
`HKMP/LocalBuildProperties.props` y `HKMPServer/LocalBuildProperties.props` (gitignored):
References=`E:\HKMP-Enemies\game1\hollow_knight_Data\Managed`, OutputDirectory=`E:\HKMP-Enemies\build\HKMP`.
Salidas: `build\HKMP\{HKMP.dll,pdb,xml,BouncyCastle.Cryptography.dll}`, `src\HKMPServer\bin\Release\net472\`.

Instalar:
```bash
cd /e/HKMP-Enemies
for g in game1 game2; do mkdir -p $g/hollow_knight_Data/Managed/Mods/HKMP; cp build/HKMP/* $g/hollow_knight_Data/Managed/Mods/HKMP/; done
cp src/HKMPServer/bin/Release/net472/* server/      # parar HKMPServer antes (dll bloqueada)
```

## Cambios por Unity 6 (en src)
| Problema | Arreglo |
|---|---|
| tk2d (`tk2dSpriteAnimator`, `On.tk2dSpriteAnimator`) movido a `TeamCherry.TK2D` / `MMHOOK_TeamCherry.TK2D` (CS1069/CS1070) | refs nuevas en HKMP.csproj |
| TMPro movido a `Unity.TextMeshPro.dll` | ref nueva |
| Newtonsoft.Json del juego es 13.0.0.0 (csproj pedia 11.0.0.0) | ref sin version |
| Modulos Unity 6 compilados contra netstandard 2.1 (CS1705) | target `UseGameCoreLibs` en HKMP.csproj: sustituye la fachada netstandard 2.0 del pack net472 por `Managed\netstandard.dll` antes de CoreCompile. Solo netstandard: cambiar tambien mscorlib/System rompe el server (.NET Framework) porque Queue<T> etc. quedarian referenciados en mscorlib. El HKMP.dll resultante solo referencia mscorlib/System/System.Core 4.0 -> vale para juego y server |
| `ImageConversion.LoadImage` tiene sobrecargas ReadOnlySpan (CS0518) | `Util/TextureUtil.cs` (delegado por reflexion a la sobrecarga byte[]); usado en SkinLoader y TextureManager |
| Clase global `FontManager` nueva en Assembly-CSharp tapa `Hkmp.Ui.Resources.FontManager` (CS0117) | renombrada a `HkmpFontManager` |
| IL hooks: `heroInPosition.Invoke(bool)` ahora es `HeroController::SendHeroInPosition(bool)`; `<EnterScene>d__469`->`d__487`, `<Respawn>d__473`->`d__491`; IL de EnterSceneDreamGate ahora en `DoEnterSceneDreamGate` | `CustomHooks.cs`: matcher elige patron segun exista SendHeroInPosition; tipos anidados buscados por prefijo; ILHook manual a DoEnterSceneDreamGate (fallback EnterSceneDreamGate) |
| Sin recurso "TMP Settings": `AddComponent<TextMeshPro>()` -> NRE en `TMP_Settings.get_autoSizeTextContainer` | `HkmpFontManager.EnsureTmpSettings()` crea/inyecta un TMP_Settings runtime (llamado en `PlayerManager.CreateUsername`) |
| Fuentes TMP no cargadas en el menu | `InGameNameFont` se resuelve perezosamente (busca "TrajanPro-Bold SDF" o cualquier "trajan") |
| `activeSceneChanged` da oldScene.name vacio/null (Opening_Sequence->Tutorial_01) -> `SetLeftScene(null)` -> ArgumentNullException en cada paquete UDP (spam infinito) | `ClientManager.OnSceneChange`: usa `_lastScene` si el nombre viene vacio y nunca envia null |
| Server: Newtonsoft.Json del juego falla "Strong name validation" en .NET Framework | HKMPServer.csproj usa PackageReference Newtonsoft.Json 13.0.3 |

## FullSynchronisation por defecto
- `HKMP/Game/Settings/ModSettings.cs`: `FullSynchronisation { get; set; } = true;`
- `HKMPServer/ConsoleSettings.cs`: `= true;` (si ya existe `consolesettings.json` manda el fichero; el de `E:\HKMP-Enemies\server` tiene true).
- Ojo: un `HKMP.GlobalSettings.json` ya guardado con false sigue mandando en el cliente (solo afecta a host desde el juego).

## Dos instancias sin pisarse (LocalLow)
- `app.info` NO cambia persistentDataPath (probado).
- Solucion aplicada: parchear companyName en `hollow_knight_Data\globalgamemanagers` (mismo largo, 11 bytes, offset 1560,
  precedido por int32 longitud 11): game1 -> `HKMP Test 1`, game2 -> `HKMP Test 2`. Backup: `globalgamemanagers.orig`.
  Script: `python E:/HKMP-Enemies/tools/patch-company.py <globalgamemanagers> "HKMP Test N"` (repetible: busca "Team Cherry" o "HKMP Test ").
- Efecto: ModLog, saves, ModSettings (`HKMP.GlobalSettings.json`) en
  `%USERPROFILE%\AppData\LocalLow\HKMP Test N\Hollow Knight\`, y PlayerPrefs en `HKCU\Software\HKMP Test N\Hollow Knight`.
  Ademas aisla de la carpeta real `Team Cherry\Hollow Knight` (con steam_autocloud.vdf) -> las pruebas no tocan el save real/Steam Cloud.
- Con PlayerPrefs vacios el juego se queda en la pantalla de primer arranque (idioma) y el API no carga mods:
  se copiaron con `reg copy "HKCU\Software\Team Cherry\Hollow Knight" "HKCU\Software\HKMP Test N\Hollow Knight" /s /f`.
- Player.log separado con `-logFile E:/HKMP-Enemies/gameN-player.log`. Ventana: `-screen-fullscreen 0 -screen-width 960 -screen-height 540`.
- Las instancias siguen corriendo sin foco (verificado).
- Si se reinstala/regenera globalgamemanagers (p.ej. copiar vanilla encima) hay que reaplicar el parche.

## Automatizacion (sin clicar)
`HKMP/Ui/DevAutoConnect.cs` (enganchado al final de `UiManager.Initialize`, no-op sin argumentos):
- `-hkmp-menu` (o env `HKMP_MENU=1`): abre el menu HKMP al llegar al menu principal.
- `-hkmp-connect host[:port]` (env `HKMP_CONNECT`): abre el menu y conecta (12 reintentos cada 5 s).
- `-hkmp-name NOMBRE` (env `HKMP_NAME`).
- Con FullSynchronisation en el servidor el cliente recibe el save del servidor y entra solo
  (`ClientManager.OnClientConnect` -> `EnterGameFromMultiplayerMenu`; nueva partida si `NewForPlayer`). No hace falta mod de test aparte.
- Lineas de log: `HKMP_AUTO: ... menu opened`, `HKMP_AUTO: connected`.
- Lanzador: `powershell -File E:\HKMP-Enemies\start-test.ps1 [-NoServer] [-MenuOnly]` (server + P1 + P2).
- Partida limpia: parar server y borrar `E:\HKMP-Enemies\server\save.json`.

## Servidor
- `E:\HKMP-Enemies\server\HKMPServer.exe` (copia de bin). Necesita consola real (Console.ReadKey): lanzarlo con
  `Start-Process` (ventana propia); redirigir stdin/stdout lo mata (InvalidOperationException).
- Log: `server\logs\server.log`. consolesettings.json: Port 26950, FullSynchronisation true.

## Pendiente / avisos
- player.log: 1 NRE en `GameManager.SetupHeroRefs` durante el preload de mods (escena sin Hero; parece ruido del preload del API, no de HKMP; sin confirmar).
- ~~"unknown state index: 255"~~ resuelto en fase 2 (ver abajo).
- No verificado: nombres sobre jugadores (fuente TMP resuelta?), skins, hechizos/animaciones remotas, hosting desde el juego.
- Cambios sin commitear en `src` (rama main local). Build final (HKMP.dll md5 8cf46219...) instalado en game1, game2 y server; las pasadas finales 1 y 2 se hicieron con ESTE build (re-verificacion 2026-10-08 tarde).

# Fase 2: sincronizacion de enemigos (2026-10-08)

## Estado: verificado con 2 instancias, en ambos sentidos, 0 FAIL
Prueba automatizada (`-hkmp-test`), dos pasadas completas con 5 escenas cada una
(Tutorial_01, Crossroads_01, Crossroads_10 = False Knight, Fungus1_01 = Greenpath, Fungus2_03 = Fungal Wastes):
| Pasada | Host de escena | Resultado (ModLog) | Evidencia |
|---|---|---|---|
| 1 | P1 (game1) | `[p1/H] SUMMARY pass=59 fail=0 info=19`, `[p2/C] SUMMARY pass=77 fail=0 info=11` | `E:\HKMP-Enemies\testrun-pass1\` |
| 2 | P2 (game2) | `[p2/H] SUMMARY pass=59 fail=0 info=19`, `[p1/C] SUMMARY pass=77 fail=0 info=11` | `E:\HKMP-Enemies\testrun-pass2\` |
| generica | P1 y P2 | incluida en ambas pasadas: Fungus1_01 `(e) generic id mapping 11` + `(e) generic enemy killed by C dies on host` / `dies on C` (Pigeon) | pasadas 1-2 y `testrun-generic-p*host\` |
| banco | ambas | incluido al final de cada pasada; server: `Player 1 rested at a bench, removed 4 stored semi-persistent enemy death(s)` | `testrun-pass*\server.log` (log acumulado de todas las ejecuciones) |
Cada carpeta: `p1-ModLog.txt`, `p2-ModLog.txt`, `server.log`, ficheros de senal, capturas `pN-<escena>-<paso>.png`
(48 capturas por pasada; el heroe se teletransporta junto al enemigo antes de capturar).
En las 4 ModLogs finales: 0 "unknown state index", 0 "entity IDs differ", 0 updates de cola descartados. Las ModLogs del rol C tienen 12 WARN "Apply method threw exception ... NullReferenceException" capturados en `EntityFsmActions` para la accion `AudioPlayRandom` del Pigeon generico (audio de la copia cliente; inofensivo, sin corregir).
(El unico `[ERROR]` es "Finished preloading in 2.00s" del API.)

Lineas de ejemplo (pasada 1, False Knight):
```
HKMP_TEST PASS [p1/H] Crossroads_10 (a) partial hit C->H applied on host: entity 11: hp 65 -> 33 (expected 33)
HKMP_TEST PASS [p2/C] Crossroads_10 (a) partial hit C->H, hp synced back to C: entity 11: host hp=33, C client hp=33
HKMP_TEST PASS [p1/H] Crossroads_10 (a) real nail swing C->H applied once: entity 8: C sent 1 hit(s) dmg 5; host hp 15 -> 10; applied 1, replica hits ignored 1
HKMP_TEST PASS [p2/C] Crossroads_10 (d) A hp kept on host transfer: entity 11 ... 'False Knight New' hostHp=33 hostActive=True
HKMP_TEST PASS [p1/H] Crossroads_10 (d) A hp after new host hit, seen by old host: entity 11 ... hostHp=17 clientHp=17
HKMP_TEST PASS [p2/C] Crossroads_10 (c) B dead on reload (client): entity 8 ... 'Zombie Barger' clientDead=True
```

## Que comprueba la prueba (por escena, H = rol host, C = rol cliente)
0. C sale a Town, H entra (host de escena vacia), C entra (cliente). Comprueba roles y que id->nombre coincide en ambos.
1. H se acerca al enemigo mas fuerte (despierta jefes/ocultos; en Crossroads_10 dispara la caida del False Knight),
   elige A (jefe si hay, si no el primero), B, D entre enemigos vivos, activos y no invencibles.
2. (a) C aplica un HitInstance sintetico (Nail, hp/2) a A en su copia -> H ve bajar la vida exacta -> C recibe la vida.
3. (a) Golpe REAL de C (`HeroController.Attack`) contra B: el host aplica el golpe reenviado una sola vez y descarta el
   golpe del slash replicado de C ("replica hits ignored"). Luego C mata B (9999) -> muere en H y en C.
4. (b) H mata D -> C lo ve morir. 4b (e) C mata un enemigo `GenericEnemy` (Pigeon en Fungus1_01) -> muere en ambos.
5. (c) C sale y vuelve con H dentro: B y D muertos (muerte guardada en servidor), A con la vida parcial del servidor.
6. (d) H sale -> C pasa a host: A conserva la vida (no vuelve al maximo), B/D siguen muertos; C (host) golpea A;
   H vuelve como cliente y ve la nueva vida.
7. (c) Ambos salen (escena vacia en el servidor) y vuelven (H primero = host, luego C): enemigos con PersistentBoolItem
   siguen muertos en ambos; los que no tienen (Crawlid, Mosscreep...) reaparecen = comportamiento vanilla (INFO).
Al final: C simula descanso en banco (`ResetSemiPersistentItems`) -> el servidor olvida muertes semipersistentes.
Ademas, al inicio: regresion de `CheckReceivedUpdates` con un update huerfano (id 60000): `returned in 0.0 ms`.

## Lanzar la prueba
```powershell
powershell -File E:\HKMP-Enemies\start-entity-test.ps1 -HostRole p1     # o p2; -Scenes "Escena:gate,..."; -NoInstall
```
Para server+instancias previas (solo E:\HKMP-Enemies), instala `build\HKMP` en game1/game2 y el server compilado, borra
`server\save.json` y `testrun\`, lanza server + P1 + P2 con `-hkmp-test pN -hkmp-test-host <rol> -hkmp-test-dir E:/HKMP-Enemies/testrun`.
Duracion ~9 min (5 escenas). Resultado: `grep HKMP_TEST` en `%USERPROFILE%\AppData\LocalLow\HKMP Test N\Hollow Knight\ModLog.txt`.
Gates validos vistos: Tutorial_01 right1, Town left1/bot1, Crossroads_01 right1/top1, Crossroads_10 left1/right1,
Fungus1_01 left1/right1, Fungus2_01 right1/left1-3, Fungus2_03 left1 (la prueba registra los gates de cada escena).
Codigo: `HKMP/Ui/DevEntityTest.cs` (solo activo con `-hkmp-test`; arrancado desde `DevAutoConnect.TryStart`).
Pone al heroe invencible y con vida maxima durante la prueba.

## Que fallaba (confirmado) y que se cambio
| Problema | Confirmacion | Arreglo |
|---|---|---|
| Golpe de un no-host no llegaba al host: solo el slash replicado del atacante en la maquina del host podia golpear (si coincidia la posicion) | lectura de codigo + prueba real: el slash replicado SI golpea la copia host ("replica hits ignored 1" en Crossroads_01/10) | `HealthManagerComponent`: hook `On.HealthManager.Hit`. Copia cliente golpeada por el jugador local -> envia `EntityComponentType.Hit` (AttackType, dano, direccion, multiplicadores, posicion origen); el host lo aplica con `HealthManager.Hit` sobre el objeto host (fuente ficticia "HKMP Remote Hit Root/../Source" -> sin alma ni recoil para el host). En el host se ignoran golpes de ataques replicados de jugadores remotos (ancestro "Player Container*" o componente Hkmp.*) para no contar doble. La vida local del cliente se mantiene la del host |
| Vida no sincronizada; al cambiar de host el enemigo volvia a vida maxima | TODO del codigo original; prueba (d) | host envia `EntityComponentType.Health` cuando cambia `hp`; servidor la guarda; cliente la aplica a copia cliente y objeto host; `Entity.MakeHost` -> `OnMakeHost()` restaura hp/muerte |
| Muerte no guardada en servidor: quien entraba despues lo veia vivo (bug del usuario) | `ServerManager` solo guardaba tipos `> Death` | se guarda `Death` (y `Health`); al entrar se aplica "muerte silenciosa" (sin efectos/drops) en cliente o en host |
| Al recargar, el no-asesino veia vivo al enemigo con PersistentBoolItem | la copia cliente es un clon "X(Clone)": su PersistentBoolItem guardaba el "killed" con id "X(Clone)" | `Entity.SyncPersistentBoolId()` copia id/escena del objeto host al clon; ademas al recibir muerte se escribe `SceneData.SaveMyState` con el id real |
| Escena vacia -> servidor borraba todo -> enemigos persistentes revivian para quien no los mato | `HandlePlayerLeaveScene` | se conservan las muertes de enemigos con PersistentBoolItem (flags al final del paquete Death); las semipersistentes se borran cuando alguien descansa en un banco (hook `GameManager.ResetSemiPersistentItems` -> dato `ResetSemiPersistent` con id 65535; solo escenas sin jugadores) |
| Enemigos con HealthManager sin entrada en `entity-registry.json` no se sincronizaban | Fungus1_01: 8 Pigeon; Crossroads_01: Mender Bug | `EntityManager.RegisterGenericEnemies`: tras el registro normal, todo HealthManager de la escena no cubierto (ni hijo/padre de entidad, ni copia cliente) se registra como `EntityType.GenericEnemy`, ordenado por ruta jerarquica+posicion para IDs deterministas. Solo en carga de escena (no en spawns) |
| `unknown state index: 255` | host enviaba `(byte)Array.IndexOf(...) = -1` cuando la FSM no tenia estado activo | host solo envia indices validos; cliente ignora 255 en silencio |
| `CheckReceivedUpdates` bucle infinito | `while (Count != 0) Peek()` sin desencolar si la entidad no existe -> cuelga el juego | cada update se mira una vez por llamada, se reencola hasta 8 veces y luego se descarta; cola max 4096; conserva el flag alreadyInScene; tambien se llama al spawnear y al pasar a host |
| Paquetes de la escena anterior lanzaban ArgumentOutOfRange en `Entity.UpdateData` | visto en pasada 2 (FSM index de otra escena) | comprobacion de indices FSM/estado/accion |
| Muerte con nombre distinto (IDs desalineados entre clientes) | defensivo | el paquete Death lleva el nombre del objeto; si no coincide se ignora con WARN "entity IDs differ" |
Servidor: `Hit` solo se reenvia al scene host y nunca se guarda.

## Limitaciones honestas
- Enemigos sin PersistentBoolItem (Crawlid, Mosscreep, Vengefly...) reaparecen al recargar una escena VACIA: es lo vanilla
  (HK los respawnea al reentrar). Con algun jugador dentro, quien entra los ve muertos (estado del servidor).
- Muertes semipersistentes: el banco solo limpia la copia del servidor para escenas vacias; el SceneData local de cada jugador
  sigue siendo por jugador (vanilla). No probado: banco real (se simulo con `ResetSemiPersistentItems`).
- Hechizos/charms del no-host: se reenvian si el origen cuelga del heroe local o esta en la capa 17 o es AttackType
  Spell/SharpShadow/NailBeam (no probado con hechizos reales, solo nail real y golpes sinteticos). Efectos remotos sin
  componente Hkmp ni contenedor "Player Container" (p. ej. flukes de Flukenest) podrian contar doble en el host.
- Jefes: False Knight verificado: golpe parcial C->H (65 -> 33), vida sincronizada, cambio de host conservando vida. MATAR AL FALSE KNIGHT HASTA EL FINAL NO FUNCIONA/NO VERIFICADO: fase `BossKill` (C golpea armadura y cabeza con dano = hp actual durante 120 s, reenviado al host): el host recibe los golpes (armadura 65->0 -> aturdido, cabeza 40->0, `Die was called on host entity (12)` 240 veces) y la vida se refleja en C, pero la FSM vuelve a subir la vida (armadura 65, cabeza 40) y nunca llega a `falseKnightDefeated=True` en ningun cliente (`HKMP_TEST INFO ... boss kill to the end ... defeated=False`, evidencia `testrun-fk\`). Causa sin diagnosticar: la muerte de la cabeza es especial (`hasSpecialDeath`, no se envia Death) y el avance de fase de la FSM parece no dispararse con golpes sinteticos de 1 frame (posible necesidad de golpes de nail reales/ventana de aturdimiento); falta probar matarlo con el host o a mano. No probado: Godhome/Pantheons, Coliseo.
  (FK tiene muerte especial por FSM; `hasSpecialDeath` no se envia como Death), Godhome/Pantheons, Coliseo.
- IDs de entidades: siguen dependiendo de que ambos clientes registren lo mismo en el mismo orden (en las pruebas coincidieron
  siempre). En Crossroads_10 el registro de la escena aditiva `Crossroads_10_boss` al SALIR sale en distinto orden en P1/P2
  (inofensivo, no se usa).
- La prueba usa teletransporte del heroe; la IA de los enemigos sigue corriendo (el FK aplasta a los husks al caer).
- SkipIntro: `SKIPINTRO_INIT_OK` y `SKIPINTRO invoked OpeningSequence.Skip` en ambas instancias; Pre_Menu_Intro no
  se ve al iniciar el mod (ya ha pasado durante el preload del API), asi que los logos no se saltan desde el mod.
- Protocolo cambiado (nuevos tipos Hit/Health/ResetSemiPersistent, Death con mas campos): clientes y servidor deben ser
  del mismo build; no se subio la version de protocolo.

## Ficheros tocados en fase 2 (src/HKMP)
`Game/Client/Entity/Component/HealthManagerComponent.cs` (reescrito), `Component/EntityComponent.cs` (tipos Hit/Health/
ResetSemiPersistent, `EntityId`), `Entity/Entity.cs`, `Entity/EntityManager.cs`, `Entity/EntityProcessor.cs`,
`Entity/EntityType.cs` (GenericEnemy), `Game/Server/ServerManager.cs`, `Game/Server/ServerEntityData.cs`,
`Ui/DevEntityTest.cs` (nuevo), `Ui/DevAutoConnect.cs`, `HKMP.csproj` (ref UnityEngine.ScreenCaptureModule).
Lanzador: `E:\HKMP-Enemies\start-entity-test.ps1`. Build/instalacion igual que fase 1 (el lanzador instala).

# Fase 2, ronda 3 (2026-10-08 noche): jefes, NRE de audio y hechizo real

Build final: HKMP.dll md5 `d73c9c21...`, instalado en game1, game2 y server. Instancias y server parados al terminar.

## Resultado (mismo build, logs en E:\HKMP-Enemies\testrun-*)
| Pasada | Host | Contenido | SUMMARY |
|---|---|---|---|
| `testrun-pass1` | P1 | 5 escenas completas + FK matado por C (no-host) | `[p1/H] pass=61 fail=0`, `[p2/C] pass=78 fail=0` |
| `testrun-pass1-bosses` | P1 | FK, Gruz Mother, Hornet 1 matados por H (host) | `[p1/H] pass=7 fail=0`, `[p2/C] pass=7 fail=0` |
| `testrun-pass2` | P2 | 5 escenas completas + FK matado por H (host) | `[p2/H] pass=60 fail=0`, `[p1/C] pass=78 fail=0` |
| `testrun-pass2-bosses` | P2 | FK, Gruz Mother, Hornet 1 matados por C (no-host) | `[p2/H] pass=7 fail=0`, `[p1/C] pass=7 fail=0` |
Es decir, cada jefe se mata hasta el final desde host y desde no-host, con cada instancia como host, y el flag
de derrota queda en True en las DOS instancias:
```
HKMP_TEST PASS [p2/H] Crossroads_10 (e) boss killed to the end (killer=c) seen as defeated: falseKnightDefeated=True
HKMP_TEST PASS [p1/C] Crossroads_04 (e) boss killed to the end (killer=c) seen as defeated: killedBigFly=True
HKMP_TEST PASS [p1/C] Fungus1_04 (e) boss killed to the end (killer=c) seen as defeated: hornet1Defeated=True
HKMP_TEST PASS [p1/H] Crossroads_10 (a) real fireball C->H applied once on host: C forwarded 1 spell hit(s) dmg 15; host applied 1; host hp 33 -> 18
```
Captura del final de FK vista por el no-host: `testrun-fk-clientkill\p2-Crossroads_10-boss-end.png`.

## Por que "no moria" el False Knight (diagnostico)
No era un bug de HKMP: con trazas de la FSM (`HKMP_FSM` en ModLog + `fsmdump-*.txt`) se vio que la prueba golpeaba la
armadura durante la secuencia de rabia/salto (`Recover -> Idle Pause -> Rage Jump Antic -> ... -> State 2 -> Stun In Air`):
la vida de la armadura volvia a 0 antes de `Rage Check`, asi que `Stunned Amount` nunca llegaba a la fase final y `Check Health`
(SetHP 65) / `Head/Health Check` (SetHP 40) reponian la vida en bucle. La prueba ahora no golpea la armadura entre `Recover`/
`Stun Fail` y el siguiente `To Phase*`/`JA *`/`Floor*` (lo que haria un jugador). Asi la FSM recorre
`... JA Hit 2 -> Floor Break -> Death Land -> Death Open -> Opened 2 -> Hit 2 ... -> Death Anim Start -> Steam -> ... -> Death Head Land`
y `falseKnightDefeated`/`killedFalseKnight` pasan a True en ambos clientes (en el no-host llega por el SaveManager).
Los golpes reenviados disparan bien los eventos de la FSM del jefe (HIT/STUN) en el host.

## Otros cambios ronda 3
- `EntityFsmActions` AudioPlayRandom: el FsmGameObject de la FSM cliente (nunca arrancada) puede ser null -> NRE en Pigeon
  generico (8-12 WARN por pasada). Ahora usa el dueno de la FSM y sale si no hay AudioSource. En las 8 ModLogs finales:
  0 excepciones. WARN/ERROR unicos que quedan: "No skins can be loaded" (sin skins, normal), "[API] Finished preloading"
  (del API) y 2x `Entity (N, Tiktik) could not update active, because client object is null` (Tiktik cuyo clon fue destruido;
  inofensivo, sin corregir). 0 "unknown state index".
- `Entity.HostStateHistory`/`GetHostFsmState` (para la prueba) y contadores de hechizos en `HealthManagerComponent`.
- Prueba: tabla de jefes (FK, Gruz Mother, Hornet, Vengefly King, Mawlek, Soul Master, Mantis Lords, Broken Vessel con su flag
  de PlayerData), fase `BossKill` (traza FSM, dump de FSM), modo `-BossOnly`, `-BossKiller h|c`, y bola de fuego real
  (`Spell Control` QUICK CAST) del no-host contra A cuando A tiene > 20 de vida.
  Lanzador: `start-entity-test.ps1 -HostRole p1|p2 [-BossKiller h|c] [-BossOnly] [-Scenes ...] [-NoInstall]`.
  Escenas de jefe: Crossroads_10:left1 (FK), Crossroads_04:left1 (Gruz Mother), Fungus1_04:right1 (Hornet 1).

## No verificado (ronda 3)
- Vengefly King, Brooding Mawlek, Soul Master, Mantis Lords, Broken Vessel: en la tabla de la prueba pero NO ejecutados.
- La bola de fuego solo conecto contra el False Knight (en Tutorial/Crossroads_01 los Crawlid tienen <=20 hp y se omite);
  otros hechizos (Desolate Dive, Howling Wraiths), nail arts y charms no probados.
- Matar jefes con golpes de nail reales (solo HitInstance sinteticos + 1 golpe real de nail por escena).

# Fase 3 (2026-10-09): estado individual por jugador + jefes/hechizos/nail arts/amuletos

## Vida/alma independiente: que se compartia y que no (investigado en codigo + prueba real)
- Mecanismo: `Resource/save-data.json` -> cada campo de PlayerData tiene `SyncType` `Server` (global: se guarda en
  `global_save_data` y se reenvia a todos) o `Player` (se guarda en `playerSaveData[authKey]` del servidor, NO se reenvia a
  otros; se devuelve al mismo jugador al reconectar). Campos que no estan en el json no se sincronizan nunca.
- Resultado: `health`, `maxHealth`, `healthBlue`, `joniHealthBlue`, `MPCharge`, `MPReserve`, `prevHealth`, `damagedBlue`,
  `hazardRespawn*`, `isInvincible`, `atBench`... NO estan en el json -> la vida/alma en curso NUNCA se compartio (ni en el
  build anterior). Individuales y guardados por jugador (`Player`, 543 campos): `maxHealthBase`, `heartPieces`,
  `MPReserveMax`, `vesselFragments`, `geo`, `geoPool`, `nailDamage`, amuletos (`equippedCharm_N`, `charmSlots`, `equippedCharms`,
  `overcharmed`), `respawnScene/MarkerName/Type`, sombra (`shade*`), dreamgate, hechizos/habilidades (`hasDash`, `fireballLevel`...),
  mapas, conversaciones NPC. Compartidos (`Server`, 858): jefes (`falseKnightDefeated`, `killedMageLord`...), diario (`kills*`
  aditivo), puertas/palancas/ascensores, `scenesVisited`, estado de NPC del mundo, `permadeathMode`.
  Ojo: habilidades y mapas son por jugador en HKMP (no "progreso compartido"); no se cambio (decision de diseno de HKMP).
- Lo que se cambio (blindaje, no habia fuga): `SaveDataMapping.LocalOnlyPlayerDataFields` (16 campos de combate: health,
  maxHealth, prevHealth, healthBlue, joniHealthBlue, damagedBlue, blockerHits, MPCharge, MPReserve, maxMP, MPReserveCap,
  focusMP_amount, isInvincible, atBench, hazardRespawnLocation, hazardRespawnFacingRight): si alguna vez aparecen en el json
  se fuerzan a Sync=false (sin mover indices); el cliente ignora save updates recibidos para ellos y el servidor no guarda ni
  reenvia campos con Sync=false. `PlayerSpecificDataFields` (73 campos persistentes individuales: mascaras, vasijas, geo, amuletos
  equipados, respawn, sombra, dreamgate) se fuerzan a `SyncType.Player` si alguien los pone en `Server`. Log al cargar:
  `Save sync: 858 PlayerData fields shared (Server), 543 per player (Player), 16 local-only combat fields never synced (forced local: 0, forced per player: 0)`.
- maxHealth al entrar: no se sincroniza; el juego lo recalcula (CharmUpdate/MaxHealth en Respawn) desde `maxHealthBase` y los
  amuletos de cada jugador, que si vienen de su `playerSaveData`. PvP: sin cambios (serversettings `IsPvpEnabled=false`).

## Prueba de vida (`-hkmp-test-health 1|only`, `Ui/DevPlayerHealthTest.cs`)
Ambos en Town (H = host). En cada paso un rol actua con metodos reales del juego y el otro sondea su PlayerData cada 0.1 s
(vida minima/maxima vista, muerte): damage-H/C (`HeroController.TakeDamage`), soul-H (`AddMPCharge`), focus-heal-H (estado
`Focus Heal` de Spell Control; en este build cae a fallback `TakeMP(33)+AddHealth(1)`, INFO), bench-C (`SetBenchRespawn` +
`MaxHealth`, NO banco real), lifeblood-H (healthBlue absorbe el golpe), hazard-C (TakeDamage hazardType 2 -> hazard respawn),
geo-H/C, death-H/C (muerte real -> respawn en banco, sombra con el geo, geo 0), mask-H (`AddToMaxHealth`), reconnect-H/C
(desconecta, vuelve al menu, borra la vida local y reconecta: recupera su maxHealthBase/geo/geoPool/sombra/respawn del servidor).
Evidencia (`testrun-health-only-p1host\`, y dentro de las pasadas finales):
```
PASS [p1/H] player-health damage-H (actor p1/H): ok; before [health=5 ...] after [health=3 ...]
PASS [p2/C] player-health damage-H (observer p2/C) own state unchanged: no change, health min/max during step 5/5, everDead=False
PASS [p1/H] player-health death-C (observer p1/H) own state unchanged: ... actor [health=5 ... geo=0 geoPool=30 shadeScene=Town ...]
PASS [p1/H] player-health reconnect-H (actor p1/H): ok; ... after [health=6 maxHealth=6 maxHealthBase=6 ... geoPool=50 shadeScene=Town ...]
```

## Ataques reenviados (`-hkmp-test-attacks Escena:gate`, `Ui/DevAttackTest.cs`)
C (no host) ataca al enemigo mas fuerte (FK en Crossroads_10) con ataques reales; H comprueba que lo aplicado en el host ==
lo reenviado (n y dano) y 0 golpes no reenviados al objeto host (= nada cuenta doble). Contadores nuevos en
`HealthManagerComponent`: `AppliedByEntity`, `OtherHostHitsByEntity`; log `HKMP_SYNC client hit not forwarded` (diagnostico).
Verificados: nail x4, Vengeful Spirit, Desolate Dive, Howling Wraiths, Great Slash, Dash Slash, Cyclone Slash, Grubberfly
(haz), Flukenest, Shaman Stone + fireball, Thorns of Agony. Los nail arts y Dive/Wraiths se lanzan poniendo el estado de la FSM
del heroe (`Nail Arts`/`Spell Control`), no con botones.
BUG ARREGLADO: Flukenest no llegaba al host (0 reenviados) porque `SpellFluke.DoDamage` usa `SubtractHealth/Die` y no
`HealthManager.Hit`. Nuevo hook `On.SpellFluke.DoDamage` en `HealthManagerComponent`: fluke local sobre copia cliente -> se
reenvia como Hit Spell; sobre el objeto host se ignoran las flukes replicadas del jugador remoto (marcadas con
`RemoteEffectMarker`, anadido en `FireballBase.StartFluke`). Tras el arreglo: `flukenest fireball C->H applied exactly once:
C forwarded 9 hit(s) dmg 36; host applied 9 dmg 36; other hits on host object 0`.

## Jefes nuevos (BossOnly)
Vengefly King (Fungus1_20_v02:right1), Brooding Mawlek (Crossroads_09:left1), Soul Master (Ruins1_24:left1), Mantis Lords
(Fungus2_15:top3), Broken Vessel (Abyss_19:right1), ademas de FK/Gruz/Hornet. Para arrancar combates, la prueba envia a las
FSM activas los eventos de inicio (`BATTLE START`, `MLORD CHALLENGE`...) y vuelca `scenefsm-*.txt`.
Soul Master: la fase 2 la arranca el orbe falso de Desolate Dive (`Quake Fake`), un trigger de escena que solo simula el
scene host: si el que mata es el no-host, el host tiene que tocar el orbe (en partida real: el jugador host debe estar en la
sala; si solo el no-host esta dentro, ese es host y vale). No es bug de HKMP: el trigger es de escena, no de entidad.
El heroe no se debe mover mientras esta `Knight Get Quake Fake` (espera el aterrizaje).

## Resultado final (build HKMP.dll md5 8774a111..., igual en game1, game2 y server)
Lanzador: `bash E:/HKMP-Enemies/tools/run-final.sh` (4 ejecuciones seguidas, archivadas; salida en `run-final.out`).
| Carpeta | Host | Contenido | SUMMARY |
|---|---|---|---|
| `testrun-r4-pass1` | P1 | vida (16 pasos) + ataques C->H (11) + 5 escenas regresion enemigos + banco simulado | `[p1/H] pass=90 fail=0`, `[p2/C] pass=96 fail=0` |
| `testrun-r4-pass1-bosses` | P1 | 8 jefes matados por H (host) | `[p1/H] pass=17 fail=0`, `[p2/C] pass=17 fail=0` |
| `testrun-r4-pass2` | P2 | igual que pass1 con P2 host | `[p2/H] pass=90 fail=0`, `[p1/C] pass=96 fail=0` |
| `testrun-r4-pass2-bosses` | P2 | 8 jefes matados por C (no host) | `[p2/H] pass=17 fail=0`, `[p1/C] pass=17 fail=0` |
Cada jefe (FK, Gruz Mother, Hornet 1, Vengefly King, Brooding Mawlek, Soul Master, Mantis Lords, Broken Vessel) queda derrotado
(flag de PlayerData True) en las DOS instancias, matado por host (P1 host) y por no-host (P2 host).
En las 4 ModLogs: 0 "unknown state index", 0 "entity IDs differ". Las de jefes tienen 4 WARN capturados por pasada
("Apply method threw ... NullReferenceException" en `EntityFsmActions` para `SetFsmBool`/`SetVelocity2d` de la copia cliente,
inofensivos, sin corregir). Sondeos intermedios: `testrun-attacks-probe1`, `testrun-bosses2-probe1..4`, `testrun-health-*`.
Instancias y server parados al terminar.

## Ficheros tocados fase 3 (src/HKMP)
`Game/Client/Save/SaveDataMapping.cs` (listas + EnforceIndividualPlayerState), `Game/Client/Save/SaveManager.cs` (ignora
updates de combate), `Game/Server/ServerManager.cs` (no guarda/reenvia Sync=false), `Game/Client/Entity/Component/HealthManagerComponent.cs`
(hook SpellFluke.DoDamage, contadores, log de golpes no reenviados), `Animation/Effects/FireballBase.cs` (RemoteEffectMarker en
flukes remotas), `Ui/DevPlayerHealthTest.cs` y `Ui/DevAttackTest.cs` (nuevos, partial de DevEntityTest), `Ui/DevEntityTest.cs`
(partial, `-hkmp-test-health`, `-hkmp-test-attacks`, `-hkmp-test-scenes none`, paso de fase Soul Master, despertar jefes),
`Ui/DevAutoConnect.cs` (DisconnectToMenu/Reconnect), `Ui/UiManager.cs` (DevDisconnectToMainMenu).
Lanzador: `start-entity-test.ps1 [-Health 1|only] [-Attacks "Escena:gate"]`; secuencia completa `tools/run-final.sh`.

## No verificado (fase 3)
- Banco REAL (sentarse en RestBench con su FSM): solo simulado (SetBenchRespawn+MaxHealth y ResetSemiPersistentItems).
- Focus real (mantener boton): el estado `Focus Heal` puesto a mano no curo; se uso el fallback TakeMP+AddHealth (INFO en log).
- PvP con IsPvpEnabled=true (dano entre jugadores): no probado; el servidor de pruebas tiene PvP desactivado.
- Hechizos nivel 2 (Shade Soul, Descending Dark, Abyss Shriek), Sharp Shadow, Dung/Defender's Crest, Spore Shroom, Glowing Womb,
  Weaversong, Grimmchild, Dreamshield: no probados. Nail arts y Dive/Wraiths lanzados por estado de FSM, no por input.
- Jefes matados con golpes sinteticos de 13 (HitInstance) + teletransporte, no con combate real; los ataques de los jefes
  contra el heroe no se evaluan (el heroe es invencible en esas fases).
- Lifeblood de amuleto (Joni/Lifeblood Heart) real: solo healthBlue puesto a mano.
