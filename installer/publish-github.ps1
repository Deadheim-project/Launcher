<#
.SYNOPSIS
    Publica o manifest do launcher em Deadheim-project/Launcher.

.DESCRIPTION
    Este repositorio e so o launcher. Ele NAO compila nem publica mod nenhum:
    cada mod (npcs, Deadheim, RaidSystem, Hearthstone, donationshop) mora no seu
    proprio repositorio e publica o proprio release de la.

    O que este script faz:

      1. envia o manifest.json para Deadheim-project/Launcher
      2. anexa o instalador ao release do launcher, se ele ja tiver sido gerado
      3. relata quais repositorios de mod ainda estao sem release
      4. roda o self-test para confirmar o resultado

    Requer o gh autenticado - passo seu, a autorizacao e no navegador:

        gh auth login

.EXAMPLE
    ./installer/publish-github.ps1
    ./installer/publish-github.ps1 -Version v1.0.1 -Yes
#>
[CmdletBinding()]
param(
    [string]$Org = "Deadheim-project",
    [string]$LauncherRepo = "Launcher",
    [string]$Version = "v1.2.2",
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# Mods proprios do servidor. Aqui SO para relatar o estado dos releases -
# nenhum deles e compilado ou publicado por este repositorio.
$ModsProprios = @("npcs", "Deadheim", "RaidSystem", "Hearthstone", "donationshop")

function Fail($msg) { Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- pre-requisitos -------------------------------------------------------
Step "Verificando o gh"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "gh nao encontrado. Instale o GitHub CLI: https://cli.github.com"
}

gh auth status 2>&1 | Out-Null
if (-not $?) {
    Fail "gh nao esta autenticado. Rode 'gh auth login' primeiro - esse passo e seu, precisa autorizar no navegador."
}

$usuario = (gh api user --jq .login)
Write-Host "    autenticado como $usuario"

# --- o que vai acontecer --------------------------------------------------
Write-Host "`nO script vai enviar o manifest.json para https://github.com/$Org/$LauncherRepo" -ForegroundColor Yellow
if (-not $Yes) {
    $resposta = Read-Host "Continuar? (s/N)"
    if ($resposta -ne "s" -and $resposta -ne "S") { Write-Host "Cancelado."; exit 0 }
}

# --- 1. manifest ----------------------------------------------------------
Step "Enviando o manifest para $Org/$LauncherRepo"

$manifestPath = Join-Path $repoRoot "DeadheimLauncher\manifest.sample.json"
if (-not (Test-Path $manifestPath)) {
    Fail "manifest.sample.json nao encontrado - rode: python installer/generate-manifest.py"
}

$visibilidade = gh repo view "$Org/$LauncherRepo" --json visibility --jq .visibility 2>$null
if (-not $visibilidade) {
    Fail "nao consegui ver $Org/$LauncherRepo - confira o nome e suas permissoes na org."
}
if ($visibilidade -ne "PUBLIC") {
    # O launcher busca o manifest sem autenticacao na maquina do jogador:
    # repositorio privado responde 404 igual a um que nao existe.
    Fail "$Org/$LauncherRepo esta $visibilidade. O launcher busca sem credencial, entao precisa ser publico."
}

$conteudo = [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath))

# Substituir um arquivo existente exige o sha do blob atual.
$sha = gh api "repos/$Org/$LauncherRepo/contents/manifest.json" --jq .sha 2>$null

$apiArgs = @(
    "repos/$Org/$LauncherRepo/contents/manifest.json",
    "-X", "PUT",
    "-f", "message=Atualiza manifest do modpack Deadheim",
    "-f", "content=$conteudo"
)
if ($sha) { $apiArgs += @("-f", "sha=$sha") }

gh api @apiArgs | Out-Null
if (-not $?) { Fail "nao consegui enviar o manifest.json" }
Write-Host "    manifest.json publicado"

# --- 2. instalador, se existir --------------------------------------------
$instalador = Join-Path $repoRoot "installer\Output\DeadheimLauncherSetup.exe"
if (Test-Path $instalador) {
    Step "Anexando o instalador ao release $Version"
    gh release view $Version -R "$Org/$LauncherRepo" 2>&1 | Out-Null
    if ($?) {
        gh release upload $Version $instalador -R "$Org/$LauncherRepo" --clobber | Out-Null
    } else {
        gh release create $Version $instalador -R "$Org/$LauncherRepo" --title "Deadheim Launcher $Version" --notes "Instalador do launcher do Deadheim." | Out-Null
    }
    if ($?) { Write-Host "    instalador anexado" } else { Write-Host "    falha ao anexar o instalador" -ForegroundColor Yellow }
} else {
    Write-Host "`n    (instalador ainda nao gerado - rode installer/build.ps1)" -ForegroundColor DarkGray
}

# --- 3. estado dos mods (somente leitura) ---------------------------------
Step "Estado dos releases dos mods (publicados de fora deste repositorio)"
foreach ($m in $ModsProprios) {
    $vis = gh repo view "$Org/$m" --json visibility --jq .visibility 2>$null
    if (-not $vis) {
        Write-Host ("  {0,-14} repositorio nao encontrado" -f $m) -ForegroundColor Red
        continue
    }
    $rel = gh release list -R "$Org/$m" --limit 1 2>$null
    if (-not $rel) {
        Write-Host ("  {0,-14} sem release  ->  gh release create v1.0.0 NOME.zip -R $Org/$m" -f $m) -ForegroundColor Yellow
    } elseif ($vis -ne "PUBLIC") {
        Write-Host ("  {0,-14} tem release, mas o repositorio esta $vis" -f $m) -ForegroundColor Yellow
    } else {
        Write-Host ("  {0,-14} OK" -f $m) -ForegroundColor Green
    }
}

# --- 4. confirma ----------------------------------------------------------
Step "Rodando o self-test do launcher"
$launcher = Join-Path $repoRoot "DeadheimLauncher\bin\Debug\net8.0-windows\DeadheimLauncher.exe"
if (Test-Path $launcher) {
    & $launcher --selftest
} else {
    Write-Host "    launcher nao compilado; rode 'dotnet build DeadheimLauncher.sln'"
}

Write-Host "`nURL do manifest:" -ForegroundColor Cyan
Write-Host "  https://raw.githubusercontent.com/$Org/$LauncherRepo/main/manifest.json"
