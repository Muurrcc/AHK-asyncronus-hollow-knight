<#
 Builds the Asyncronus MSI installer.

 Requirements: .NET SDK 8+ and WiX v4+ (dotnet tool install --global wix --version 6.0.2; see README).
 No game file is ever copied into the repository: the input DLLs are passed as parameters.

 Example:
   .\build.ps1 -VanillaAssembly <path>\Assembly-CSharp.dll `
               -ApiDir <path>\api-compiled `
               -ModDir ..\dist -Version 1.1.0

 Parameters:
   -VanillaAssembly  ORIGINAL Assembly-CSharp.dll of the supported game build (hash is checked).
   -ApiDir           Folder with the already compiled Modding API (patched Assembly-CSharp.dll, Mono.Cecil, MonoMod.*,
                     unityscenerepacker, MMHOOK_*). Only used for: Assembly-CSharp.dll (to generate the delta),
                     4 third-party libraries and the SIZE of the MMHOOK files (sanity check).
   -ModDir           Folder with HKMP.dll and its dependencies (*.dll). Defaults to ..\dist (only .dll files are taken).
   -Version          MSI version (x.y.z).
   -OutDir           Where the MSI is written. Defaults to ..\dist.
   -SkipDelta        Do not regenerate the .patch (reuse the one in patches\).
#>
param(
    [Parameter(Mandatory)][string]$VanillaAssembly,
    [Parameter(Mandatory)][string]$ApiDir,
    [string]$ModDir,
    [string]$Version = '1.0.0',
    [string]$OutDir,
    [string]$ExpectedVanillaSha256 = 'e9048ef6a633970f735e01ec166d3959f610eaea7a88d827d48d67b1e5fb87bd',
    [string]$GameBuild = '1.5.12459',
    [switch]$SkipDelta
)
$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Repo = Split-Path $Root -Parent
if (-not $ModDir) { $ModDir = Join-Path $Repo 'dist' }
if (-not $OutDir) { $OutDir = Join-Path $Repo 'dist' }
$Build = Join-Path $Root 'build'
$Payload = Join-Path $Build 'payload'
$PatchFile = Join-Path $Root 'patches\Assembly-CSharp.vanilla-to-api.patch'
$MonoModVersion = '21.4.29.1'

function Hash($f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower() }
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Run($exe, [string[]]$a) { & $exe @a; if ($LASTEXITCODE -ne 0) { throw "$exe fallo ($LASTEXITCODE)" } }

$env:PATH += ";$env:USERPROFILE\.dotnet\tools"
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw "Falta WiX: dotnet tool install --global wix --version 6.0.2" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version debe ser x.y.z" }

$patchedAsm = Join-Path $ApiDir 'Assembly-CSharp.dll'
foreach ($f in @($VanillaAssembly, $patchedAsm)) { if (-not (Test-Path $f)) { throw "No existe $f" } }
$vanillaSha = Hash $VanillaAssembly
if ($vanillaSha -ne $ExpectedVanillaSha256) { throw "El Assembly-CSharp.dll original no es el esperado ($vanillaSha)." }
$patchedSha = Hash $patchedAsm

Remove-Item $Build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Payload, "$Payload\mod", "$Payload\api", "$Payload\tools", $OutDir, (Split-Path $PatchFile) | Out-Null

# --- 1. Herramienta (CLI) -> sirve para crear/verificar el delta
Step "Compilando AsyncronusSetup (CLI)"
Run dotnet @('build', "$Root\src\Asyncronus.Setup", '-c', 'Release', '-v', 'q', '-nologo')
$cli = "$Root\src\Asyncronus.Setup\bin\Release\net48\AsyncronusSetup.exe"

# --- 2. Delta vanilla -> parcheado
Step "Delta binario"
if (-not $SkipDelta) { Run $cli @('diff', $VanillaAssembly, $patchedAsm, $PatchFile) }
$verify = Join-Path $Build 'verify-Assembly-CSharp.dll'
Run $cli @('apply', $VanillaAssembly, $PatchFile, $verify)
if ((Hash $verify) -ne $patchedSha) { throw "El delta NO reproduce el DLL parcheado." }
Remove-Item $verify
$patchSize = (Get-Item $PatchFile).Length
$origSize = (Get-Item $patchedAsm).Length
if ($patchSize -ge $origSize) { throw "El delta ($patchSize) no es menor que el DLL parcheado ($origSize)." }
# El delta no debe contener el DLL original: ninguna ristra larga de bytes del vanilla aparece literal en el parche
$pb = [IO.File]::ReadAllBytes($PatchFile); $vb = [IO.File]::ReadAllBytes($VanillaAssembly)
$latin = [Text.Encoding]::GetEncoding(28591)
$ps = $latin.GetString($pb)
$rnd = New-Object Random 42; $leak = 0
1..200 | ForEach-Object { $o = $rnd.Next(0, $vb.Length - 64); if ($ps.Contains($latin.GetString($vb, $o, 64))) { $leak++ } }
Write-Host ("Delta: {0:N0} bytes (DLL parcheado {1:N0}, original {2:N0}); sha256 reproducido OK; muestras de 64 bytes del original halladas en el parche: {3}/200" -f $patchSize, $origSize, $vb.Length, $leak)
if ($leak -gt 5) { throw "El parche parece contener trozos literales del DLL original." }

# --- 3. PrePatcher (de la Modding API, MIT) y HookGen/MonoMod (NuGet, MIT)
Step "PrePatcher"
Run dotnet @('build', "$Root\src\PrePatcher", '-c', 'Release', '-v', 'q', '-nologo')
$pp = "$Root\src\PrePatcher\bin\Release\net472"
Get-ChildItem $pp -File | Where-Object { $_.Name -notlike '*.pdb' } | Copy-Item -Destination "$Payload\tools"

Step "HookGen $MonoModVersion"
$cache = Join-Path $Root 'build-cache'; New-Item -ItemType Directory -Force $cache | Out-Null
foreach ($id in 'MonoMod.RuntimeDetour.HookGen', 'MonoMod') {
    $nupkg = Join-Path $cache "$id.$MonoModVersion.zip"
    if (-not (Test-Path $nupkg)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/$id/$MonoModVersion" -OutFile $nupkg
    }
    $x = Join-Path $cache "$id.$MonoModVersion"
    if (-not (Test-Path $x)) { Expand-Archive $nupkg $x }
    Copy-Item (Get-ChildItem "$x\lib\net40" -Filter *.exe).FullName "$Payload\tools"
}

# --- 4. Payload
Step "Preparando payload"
$apiThirdParty = 'Mono.Cecil.dll', 'MonoMod.RuntimeDetour.dll', 'MonoMod.Utils.dll', 'unityscenerepacker.dll'
foreach ($n in $apiThirdParty) { Copy-Item (Join-Path $ApiDir $n) "$Payload\api" }
$modDlls = Get-ChildItem $ModDir -Filter *.dll -File -ErrorAction SilentlyContinue
if (-not ($modDlls | Where-Object Name -eq 'HKMP.dll')) { throw "No hay HKMP.dll en $ModDir (usa -ModDir)." }
$modDlls | Copy-Item -Destination "$Payload\mod"
Copy-Item $PatchFile "$Payload\Assembly-CSharp.patch"
$ini = @("ModVersion=$Version", "GameBuild=$GameBuild", "VanillaSha256=$vanillaSha", "PatchedSha256=$patchedSha")
foreach ($h in 'MMHOOK_Assembly-CSharp.dll', 'MMHOOK_PlayMaker.dll', 'MMHOOK_TeamCherry.TK2D.dll', 'MMHOOK_TeamCherry.Cinematics.dll') {
    $ini += "HookSize.$h=" + (Get-Item (Join-Path $ApiDir $h)).Length   # solo el tamano esperado; el fichero NO se distribuye
}
Set-Content "$Payload\payload.ini" $ini -Encoding ASCII
Copy-Item "$Root\THIRD-PARTY-NOTICES.md" $Payload -ErrorAction SilentlyContinue

# --- 5. Acciones personalizadas
Step "Compilando acciones personalizadas (DTF)"
Run dotnet @('build', "$Root\src\Asyncronus.CA", '-c', 'Release', '-v', 'q', '-nologo')
$ca = "$Root\src\Asyncronus.CA\bin\Release\net48\Asyncronus.CA.CA.dll"
if (-not (Test-Path $ca)) { throw "No se genero $ca" }

# --- 6. MSI
Step "wix build"
$msi = Join-Path $OutDir "Asyncronus-$Version.msi"
Push-Location "$Root\wix"
try {
    Run wix @('build', 'Asyncronus.wxs', '-arch', 'x86', '-culture', 'es-ES', '-loc', 'Asyncronus.es-ES.wxl', '-ext', 'WixToolset.UI.wixext',
        '-d', "ProductVersion=$Version", '-d', "PayloadDir=$Payload", '-d', "CaDll=$ca",
        '-d', "VanillaSha256=$vanillaSha", '-d', "PatchedSha256=$patchedSha",
        '-pdbtype', 'none', '-o', $msi)
} finally { Pop-Location }
Write-Host ("`nMSI: {0} ({1:N0} bytes)" -f $msi, (Get-Item $msi).Length) -ForegroundColor Green
