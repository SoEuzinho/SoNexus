# Gera a versão portátil e o instalador do SoNexus.
#   .\build.ps1            -> publish\<versão>\, installer\Output\SoNexus-Setup-<versão>.exe e o .sha256
# Requer: .NET 8 SDK e Inno Setup 6 (winget install JRSoftware.InnoSetup).

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$proj = Get-Content "$root\SoNexus\SoNexus.csproj" -Encoding UTF8
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

Write-Host "SoNexus $version"
# Uma pasta por versão: uma cópia aberta da versão anterior não trava o build.
$publish = Join-Path $root "publish\$version"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\SoNexus\SoNexus.csproj" -c Release -p:PublishReadyToRun=true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup)' }
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" "$root\installer\SoNexus.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC falhou' }

$setup = Join-Path $root "installer\Output\SoNexus-Setup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash  SoNexus-Setup-$version.exe" | Out-File -Encoding ascii "$setup.sha256"
Write-Host "Instalador: $setup"
Write-Host "SHA-256:    $hash"
