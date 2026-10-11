# Asyncronus — Hollow Knight Co-op

**A**lliance of **S**ouls, **Y**ours and **N**eighbours **C**onnecting **R**emotely, **O**nline, **N**ail-bearers **U**nited, **S**eamlessly.

Multijugador para **Hollow Knight** (Steam, build **1.5.12459 / Unity 6**) basado en [HKMP](https://github.com/Extremelyd1/HKMP), con:

- **Enemigos sincronizados**: si un jugador golpea o mata a un enemigo, el otro lo ve. Vida, alma y amuletos son por jugador; el progreso del mundo es compartido.
- **Jugar por los servidores de Steam con un código**: el anfitrión comparte un código corto (`XXXX-XXXX`) y los amigos se unen sin abrir puertos ni usar VPN. Contraseña opcional.
- **Instalador `.msi`** que instala y actualiza, y se desinstala desde *Agregar o quitar programas* o desde el menú Inicio.
- **Convive con otros mods**: solo añade `Managed\Mods\Asyncronus`; no toca ni mueve otros mods ni tus partidas.

> Proyecto de fans. No está afiliado ni respaldado por Team Cherry. Necesitas tu propia copia de Hollow Knight.

## Instalar (jugadores)

1. Descarga `Asyncronus-<versión>.msi` desde [Releases](../../releases).
2. Cierra Hollow Knight y ejecútalo. No pide administrador.
3. Abre el juego desde Steam: en el menú principal aparece **Start Multiplayer**.

Para actualizar, ejecuta el MSI nuevo encima. Para quitarlo: *Desinstalar Asyncronus* en el menú Inicio, o desde Configuración → Aplicaciones. Se restauran los ficheros originales del juego.

Todos los jugadores deben usar la **misma versión**. Si el instalador dice que tu juego no es la versión esperada: Steam → clic derecho en Hollow Knight → Propiedades → Archivos instalados → *Verificar integridad* (y sin betas).

### Windows SmartScreen
El MSI no está firmado digitalmente (un certificado de firma es de pago), por lo que Windows puede avisar: *Más información → Ejecutar de todas formas*. Todo el código está en este repositorio.

### Si usas Lumafly u otros mods
Esta versión del juego necesita una Modding API distinta a la de Lumafly. El instalador **no borra ni mueve** tus mods, pero que funcionen o no con la API de Unity 6 depende de cada mod y no se ha comprobado. No uses "Instalar API" / "Actualizar todo" de Lumafly después: sobrescribiría esta API.

## Jugar por Steam con código

1. **Anfitrión**: entra en Start Multiplayer → *Start Hosting*. Verás tu código (también se copia al portapapeles y sale en el chat).
2. **Amigos**: Start Multiplayer → escriben el código y pulsan *Unirse con código* (y la contraseña, si el anfitrión puso una).
3. Alternativa clásica: IP y puerto (UDP 26950).

> Estado: la lógica del puente está probada con 66 tests unitarios, con sockets de Steam en local y dentro del juego, pero **la conexión real entre dos cuentas de Steam distintas a través de los relés todavía no se ha probado** (`ConnectP2P` entre cuentas distintas). Issues y reportes bienvenidos.

## Cómo funciona el instalador (y por qué no lleva archivos del juego)
La Modding API para Unity 6 ([PR hk-modding/api #174](https://github.com/hk-modding/api/pull/174)) modifica `Assembly-CSharp.dll`. Este repositorio **no contiene código de Team Cherry**: el MSI incluye un *parche binario* (`installer/patches`) que se aplica en tu PC sobre tu `Assembly-CSharp.dll` original (se comprueba su hash), y genera los `MMHOOK_*` con HookGen localmente. Hace copia de seguridad de lo que sobrescribe. Detalles en [installer/README.md](installer/README.md).

## Compilar
- Mod: copia `src/HKMP/LocalBuildProperties_example.props` a `LocalBuildProperties.props`, apunta `References` a un `Managed` con la API instalada y `dotnet build src/HKMP.sln -c Release`.
- Tests: `dotnet test tests/HKMP.SteamBridge.Tests`.
- MSI: ver `installer/README.md` (WiX 6).

## Licencias
HKMP es de Extremelyd1 bajo **LGPL-2.1** (`src/LICENSE`); este fork la mantiene. Los cambios nuevos se publican bajo la misma licencia. La Modding API es de hk-modding (MIT). Hollow Knight es de Team Cherry. Avisos de terceros en `installer/THIRD-PARTY-NOTICES.md`.
