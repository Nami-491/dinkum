param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [Parameter(Mandatory=$true)][string]$LegacyLoaderPath
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GamePath).Path
$legacy = (Resolve-Path -LiteralPath $LegacyLoaderPath).Path
$native = Join-Path $game 'Dinkum_Data/Managed/Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne '5106DA5D624AAEDABB16159A9BDAA9AB3CE4D09EB992BD4759D881DD9D9BEA24') { throw 'Unsupported Dinkum game assembly. Do not bypass the runtime hash guard.' }
$src = Join-Path $PSScriptRoot 'src'
function Build-Project([string]$Project, [string]$Output, [string[]]$Properties) {
    & dotnet build (Join-Path $src $Project) -c Release --nologo "-p:GamePath=$game" -o $Output @Properties
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project" }
}
foreach ($variant in @('modern','legacy')) {
    $properties = if ($variant -eq 'legacy') { @('-p:Legacy=true', "-p:LoaderPath=$legacy") } else { @() }
    Build-Project 'Makeshift-Fixed/Makeshift.csproj' (Join-Path $src "makeshift-build/$variant") $properties
    foreach ($mod in @('FancyFeast','FishInABarrel','InventoryManagement','SalePrices','NpcMap')) {
        Build-Project 'Modpack-Mods/Mods.csproj' (Join-Path $src "modpack-build/$variant/$mod") ($properties + @("-p:Mod=$mod"))
    }
    Build-Project 'Modpack-Mods/Mods.csproj' (Join-Path $src "pack42-build/$variant/EquipmentEnhancement") ($properties + @('-p:Mod=EquipmentEnhancement'))
    Build-Project 'EquipmentRecovery/Recovery.csproj' (Join-Path $src "pack42-build/$variant/EquipmentRecovery") $properties
}
Build-Project 'Modpack-Installer/Modpack.csproj' (Join-Path $PSScriptRoot 'build') @()
