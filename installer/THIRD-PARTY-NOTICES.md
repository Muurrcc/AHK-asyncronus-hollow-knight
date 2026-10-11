# Componentes de terceros del instalador

Todos con licencia MIT salvo indicacion contraria. No se incluye ningun fichero de Team Cherry.

| Componente | Origen | Uso |
|---|---|---|
| Modding API de Hollow Knight (PrePatcher, y el codigo de la API que acaba en el delta) | github.com/leesiuuuu/HK-Modding-API (MIT, (c) 2017 seanpr96, iamwyza, firzen) | Parche del Assembly-CSharp.dll y PrePatcher |
| MonoMod, MonoMod.Utils, MonoMod.RuntimeDetour, MonoMod.RuntimeDetour.HookGen 21.4.29.1 | github.com/MonoMod (MIT, 0x0ade) | Hooks en tiempo de ejecucion; HookGen genera los MMHOOK_* en el PC del usuario |
| Mono.Cecil 0.11.3 | github.com/jbevain/cecil (MIT, Jb Evain) | Manipulacion de ensamblados |
| unityscenerepacker 2.4.0 | github.com/jakobhellermann/unity-scene-repacker (MIT) | Dependencia de la Modding API |
| BouncyCastle.Cryptography | github.com/bcgit/bc-csharp (MIT) | Dependencia de HKMP |
| bsdiff (algoritmo) | Colin Percival (BSD-2); reimplementado en `src/Asyncronus.Setup/Core/Delta.cs` | Aplicar/crear el delta |
| WiX Toolset v6 (solo para compilar) | wixtoolset.org (MS-RL) | Genera el MSI |

El texto de la licencia MIT de la Modding API esta en `src/PrePatcher/LICENSE-ModdingAPI.txt`.
