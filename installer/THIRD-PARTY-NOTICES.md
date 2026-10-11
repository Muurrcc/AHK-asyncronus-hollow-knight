# Third-party components of the installer

All MIT-licensed unless noted otherwise. No Team Cherry file is included.

| Component | Source | Use |
|---|---|---|
| Hollow Knight Modding API (PrePatcher, and the API code that ends up in the delta) | github.com/leesiuuuu/HK-Modding-API (MIT, © 2017 seanpr96, iamwyza, firzen) | Patch for `Assembly-CSharp.dll`, PrePatcher |
| MonoMod, MonoMod.Utils, MonoMod.RuntimeDetour, MonoMod.RuntimeDetour.HookGen 21.4.29.1 | github.com/MonoMod (MIT, 0x0ade) | Runtime hooks; HookGen creates `MMHOOK_*` on the user's PC |
| Mono.Cecil 0.11.3 | github.com/jbevain/cecil (MIT, Jb Evain) | Assembly manipulation |
| unityscenerepacker 2.4.0 | github.com/jakobhellermann/unity-scene-repacker (MIT) | Dependency of the Modding API |
| BouncyCastle.Cryptography | github.com/bcgit/bc-csharp (MIT) | Dependency of HKMP (DTLS) |
| HKMP | github.com/Extremelyd1/HKMP (LGPL-2.1, see `src/LICENSE`) | The mod itself |
| bsdiff (algorithm) | Colin Percival (BSD-2); own implementation in `src/Asyncronus.Setup/Core/Delta.cs` | Create/apply the binary delta |
| WiX Toolset v6 (build time only) | wixtoolset.org (MS-RL) | Builds the MSI |

The Modding API MIT license is reproduced in `src/PrePatcher/LICENSE-ModdingAPI.txt`.
