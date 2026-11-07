# Schedule I em Portugues + S1 Co-op Talk: one-command installer.
#
#   irm https://github.com/CoolFreeze23/schedule1-ptbr-coop/releases/latest/download/install.ps1 | iex
#
# Finds the game through Steam, installs the .NET 6 runtime if needed, downloads the latest release, checks it, installs
# it and starts the game. Saves are never touched. Safe to run again to update.
#
# To remove everything:
#   & ([scriptblock]::Create((irm https://github.com/CoolFreeze23/schedule1-ptbr-coop/releases/latest/download/install.ps1))) -Uninstall
#
# This file stays ASCII: Windows PowerShell can misread other characters when a script is run straight from the web.

param(
    [switch]$Uninstall,
    [switch]$NoLaunch,
    [string]$GamePath = $env:S1_GAME_PATH,
    [string]$KitZip = $env:S1_KIT_ZIP
)

if ($env:S1_NO_LAUNCH) { $NoLaunch = $true }

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $appId = '3164500'
    $repo = 'CoolFreeze23/schedule1-ptbr-coop'
    $kitName = 'Schedule1-PTBR-CoopTalk-Kit.zip'
    $releaseUrl = "https://github.com/$repo/releases/latest/download"
    $testedBuild = '25796331'
    $dotnetUrl = 'https://aka.ms/dotnet/6.0/dotnet-runtime-win-x64.exe'

    function Say([string]$pt, [string]$en) {
        Write-Host ''
        Write-Host "  $pt" -ForegroundColor Cyan
        if ($en) { Write-Host "  $en" -ForegroundColor DarkGray }
    }

    function Good([string]$pt, [string]$en) {
        Write-Host ''
        Write-Host "  $pt" -ForegroundColor Green
        if ($en) { Write-Host "  $en" -ForegroundColor DarkGray }
    }

    function Stop-WithHelp([string]$pt, [string]$en) {
        Write-Host ''
        Write-Host "  $pt" -ForegroundColor Red
        if ($en) { Write-Host "  $en" -ForegroundColor DarkGray }
        Write-Host ''
        throw 'Installation stopped.'
    }

    function Find-Game {
        if ($GamePath) {
            if (Test-Path -LiteralPath (Join-Path $GamePath 'Schedule I.exe')) { return $GamePath }
            Stop-WithHelp "Nao achei Schedule I.exe em: $GamePath" "Schedule I.exe not found in: $GamePath"
        }
        $steam = $null
        foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
            $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($item.SteamPath) { $steam = $item.SteamPath; break }
            if ($item.InstallPath) { $steam = $item.InstallPath; break }
        }
        if (-not $steam) {
            Stop-WithHelp 'Nao achei a Steam neste PC. Instale a Steam e o Schedule I primeiro.' 'Steam was not found. Install Steam and Schedule I first.'
        }
        $libraries = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value.Replace('\\', '\')
            }
        }
        foreach ($library in ($libraries | Select-Object -Unique)) {
            $manifest = Join-Path $library "steamapps\appmanifest_$appId.acf"
            if (-not (Test-Path -LiteralPath $manifest)) { continue }
            $text = Get-Content -LiteralPath $manifest -Raw
            $dir = [regex]::Match($text, '"installdir"\s+"([^"]+)"').Groups[1].Value
            $path = Join-Path $library "steamapps\common\$dir"
            if (Test-Path -LiteralPath (Join-Path $path 'Schedule I.exe')) {
                $build = [regex]::Match($text, '"buildid"\s+"(\d+)"').Groups[1].Value
                if ($build -and $build -ne $testedBuild) {
                    Write-Host ''
                    Write-Host '  Aviso: esta versao do jogo e diferente da testada. Algum texto pode ficar em ingles.' -ForegroundColor Yellow
                    Write-Host '  Warning: this game version differs from the tested one. Some text may stay in English.' -ForegroundColor DarkGray
                }
                return $path
            }
        }
        Stop-WithHelp 'Nao achei o Schedule I na Steam. Instale o jogo e rode este comando de novo.' 'Schedule I is not installed in Steam. Install it and run this again.'
    }

    function Wait-GameClosed {
        $warned = $false
        while (Get-Process -Name 'Schedule I' -ErrorAction SilentlyContinue) {
            if (-not $warned) {
                Say 'Feche o Schedule I para continuar. Estou esperando...' 'Close Schedule I to continue. Waiting...'
                $warned = $true
            }
            Start-Sleep -Seconds 2
        }
    }

    function Download([string]$url, [string]$file) {
        $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
        if (Test-Path -LiteralPath $curl) {
            & $curl -L --fail --silent --show-error --retry 3 -o $file $url
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        } else {
            Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
        }
    }

    function Ensure-Dotnet6 {
        $roots = @()
        if ($env:ProgramFiles) { $roots += Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App' }
        foreach ($root in $roots) {
            if ((Test-Path -LiteralPath $root) -and (Get-ChildItem -LiteralPath $root -Directory -Filter '6.*' -ErrorAction SilentlyContinue)) { return }
        }
        Say 'Instalando o .NET 6 da Microsoft, que o mod precisa. O Windows pode pedir permissao: clique em Sim.' 'Installing Microsoft .NET 6, which the mod needs. Windows may ask for permission: click Yes.'
        $setup = Join-Path $env:TEMP 'dotnet6-runtime-setup.exe'
        Download $dotnetUrl $setup
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
            Remove-Item -LiteralPath $setup -Force
            Stop-WithHelp 'O instalador do .NET nao tem assinatura valida da Microsoft. Parei por seguranca.' 'The .NET installer has no valid Microsoft signature. Stopped for safety.'
        }
        $process = Start-Process -FilePath $setup -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait -PassThru
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
        if ($process.ExitCode -notin 0, 1638, 3010) {
            Stop-WithHelp "Nao consegui instalar o .NET 6 (codigo $($process.ExitCode)). Tente de novo e clique em Sim." ".NET 6 setup failed (code $($process.ExitCode)). Try again and click Yes."
        }
    }

    function Install-Kit([string]$zipPath, [string]$target) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $root = [IO.Path]::GetFullPath($target).TrimEnd('\') + '\'
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($entry in $zip.Entries) {
                if (-not $entry.Name) { continue }
                $destination = [IO.Path]::GetFullPath((Join-Path $target $entry.FullName))
                if (-not $destination.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in kit: $($entry.FullName)" }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
            }
        } finally {
            $zip.Dispose()
        }
        Get-ChildItem -LiteralPath (Join-Path $target 'Mods') -Filter 'S1*.dll' | Unblock-File
        Unblock-File -LiteralPath (Join-Path $target 'version.dll')
    }

    function Remove-Mod([string]$target) {
        Wait-GameClosed
        foreach ($file in 'Mods\S1PortuguesBR.dll', 'Mods\S1CoopTalk.dll', 'UserData\S1PortuguesBR', 'UserData\S1CoopTalk_sync.log', 'LEIA-ME.txt') {
            $path = Join-Path $target $file
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
        $mods = Join-Path $target 'Mods'
        $others = Get-ChildItem -LiteralPath $mods -Filter '*.dll' -ErrorAction SilentlyContinue
        if ($others) {
            Good 'Mods removidos. Mantive o MelonLoader porque ha outros mods na pasta Mods.' 'Mods removed. MelonLoader stays because other mods are installed.'
            return
        }
        foreach ($file in 'version.dll', 'dobby.dll', 'MelonLoader', 'Mods', 'Plugins', 'UserData', 'UserLibs') {
            $path = Join-Path $target $file
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
        Good 'Tudo removido. Seus saves continuam intactos.' 'Everything removed. Your saves are untouched.'
    }

    function Start-AndWatch([string]$target) {
        $log = Join-Path $target 'MelonLoader\Latest.log'
        $firstRun = -not (Test-Path -LiteralPath (Join-Path $target 'MelonLoader\Il2CppAssemblies'))
        $started = Get-Date
        Start-Process "steam://rungameid/$appId"
        if ($firstRun) {
            Say 'Abrindo o jogo. A PRIMEIRA vez demora alguns minutos e o jogo parece travado: e normal, nao feche!' 'Starting the game. The FIRST launch takes a few minutes and looks frozen: this is normal, do not close it!'
        } else {
            Say 'Abrindo o jogo...' 'Starting the game...'
        }
        $deadline = $started.AddMinutes(20)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).LastWriteTime -gt $started) {
                $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite,Delete')
                $reader = New-Object IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
                $reader.Dispose()
                if ($text -match 'S1 Co-op Talk v' -and $text -match 'strings, \d+ templates') {
                    Good 'Pronto! Jogo em portugues, com vozes e co-op melhorado. Bom jogo!' 'Done! The game is in Portuguese with voices and improved co-op. Have fun!'
                    return
                }
            }
            if ((Get-Date) -gt $started.AddMinutes(2) -and -not (Get-Process -Name 'Schedule I' -ErrorAction SilentlyContinue)) {
                Stop-WithHelp 'O jogo fechou antes de terminar. Abra o Schedule I pela Steam e veja se ele carrega em portugues.' 'The game closed before finishing. Open Schedule I from Steam and check that it loads in Portuguese.'
            }
        }
        Say 'Ainda carregando depois de 20 minutos. Confira a internet e abra o jogo de novo pela Steam.' 'Still loading after 20 minutes. Check the internet connection and start the game from Steam again.'
    }

    Write-Host ''
    Write-Host '  ==================================================' -ForegroundColor White
    Write-Host '   Schedule I em Portugues (Brasil) + S1 Co-op Talk' -ForegroundColor White
    Write-Host '  ==================================================' -ForegroundColor White

    $game = Find-Game
    Say "Jogo encontrado: $game" "Game found: $game"

    if ($Uninstall) {
        Remove-Mod $game
        return
    }

    Wait-GameClosed
    Ensure-Dotnet6

    $work = Join-Path $env:TEMP "s1-ptbr-$PID"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        if ($KitZip) {
            $zipPath = $KitZip
        } else {
            Say 'Baixando o pacote (uns 60 MB)...' 'Downloading the package (about 60 MB)...'
            $zipPath = Join-Path $work $kitName
            Download "$releaseUrl/$kitName" $zipPath
            Download "$releaseUrl/$kitName.sha256" "$zipPath.sha256"
            $expected = ((Get-Content -LiteralPath "$zipPath.sha256" -Raw).Trim() -split '\s+')[0]
            $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
            if ($actual -ne $expected) {
                Stop-WithHelp 'O download veio corrompido. Rode o comando de novo.' 'The download is corrupted. Run the command again.'
            }
        }
        Say 'Instalando...' 'Installing...'
        Install-Kit $zipPath $game
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    Good 'Instalado. Seus saves nao foram alterados.' 'Installed. Your saves were not changed.'
    Write-Host ''
    Write-Host '  Y chat | F7 marcar | Shift+F7 pedir ajuda | F8 historico | /grana 500 dar dinheiro' -ForegroundColor Gray

    if (-not $NoLaunch) { Start-AndWatch $game }
}
