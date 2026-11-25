# HP Control installer (runs elevated). Safe to re-run: upgrades in place.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'bin'
$dst = Join-Path $env:ProgramFiles 'HP Control'
$svc = 'HPControlSvc'
$log = Join-Path $env:TEMP 'hpcontrol-install.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    Write-Host 'Installing HP Control...' -ForegroundColor Cyan

    # files from a downloaded zip carry the "from the internet" mark, which makes Windows warn every time the app starts
    Get-ChildItem $PSScriptRoot -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

    # checks: HP firmware interface, laptop model, .NET Framework 4.8
    if (-not (Test-Path (Join-Path $src 'HPControl.Service.exe'))) { throw "bin\HPControl.Service.exe not found. Extract the whole zip first, then run install.cmd from the extracted folder." }
    $hpWmi = Get-CimInstance -Namespace root\wmi -ClassName hpqBIntM -ErrorAction SilentlyContinue
    if (-not $hpWmi) { throw 'HP BIOS interface (hpqBIntM) not found. HP Control only works on HP laptops.' }
    $model = (Get-CimInstance Win32_ComputerSystem).Model
    Write-Host " - laptop: $model"
    if ($model -notmatch 'OmniBook X Flip') {
        Write-Host "   HP Control was made and tested on the HP OmniBook X Flip 16. On other HP models the fan modes may behave differently." -ForegroundColor Yellow
        if ((Read-Host '   Install anyway? (y/N)') -ne 'y') { throw 'Cancelled.' }
    }
    $net = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
    if ($net -lt 528040) { throw '.NET Framework 4.8 is required (it comes with Windows 10 and 11; run Windows Update).' }

    # stop running copies
    if (Get-Service $svc -ErrorAction SilentlyContinue) {
        Write-Host ' - stopping existing service'
        Stop-Service $svc -Force -ErrorAction SilentlyContinue
        (Get-Service $svc).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    Get-Process HPControl -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    foreach ($f in 'HPControl.exe', 'HPControl.exe.config', 'HPControl.Service.exe', 'HPControl.Service.exe.config') {
        $p = Join-Path $src $f
        if (Test-Path $p) { Copy-Item $p -Destination $dst -Force; Write-Host " - copied $f" }
    }
    Get-ChildItem $dst -File | Unblock-File -ErrorAction SilentlyContinue
    foreach ($f in 'uninstall.cmd', 'uninstall.ps1', 'README.md') {
        $p = Join-Path $PSScriptRoot $f
        if (Test-Path $p) { Copy-Item $p -Destination $dst -Force }
    }

    # bundle HP's own audio-effect components (DTS presets/EQ, Fortemedia mic) + the UWP C++ runtime they need
    try {
        $fx = Join-Path $dst 'fx'
        New-Item -ItemType Directory -Force -Path $fx | Out-Null
        $hp = Get-AppxPackage -AllUsers -Name 'AD2F1837.myHP' | Sort-Object Version -Descending | Select-Object -First 1
        $vc = Get-AppxPackage -AllUsers -Name 'Microsoft.VCLibs.140.00' | Where-Object { $_.Architecture -eq 'X64' } | Sort-Object Version -Descending | Select-Object -First 1
        if ($hp) { foreach ($f in 'DTSApo4xWinRTComponent.dll', 'FMAPOCTL.dll', 'RtkAudioCore.dll') { $p = Join-Path $hp.InstallLocation $f; if (Test-Path $p) { Copy-Item $p $fx -Force } } }
        if ($vc) { Get-ChildItem $vc.InstallLocation -Filter '*_app.dll' | ForEach-Object { Copy-Item $_.FullName $fx -Force } }
        # keep a copy in HP Control's data folder (and next to the installer), so HP Control can be reinstalled or
        # updated even after the HP app is uninstalled. These are HP's files: they are never uploaded or shared.
        $saved = Join-Path $env:ProgramData 'HPControl\fx-saved'
        $keep = Join-Path $PSScriptRoot 'fx'
        if ($hp -and (Get-ChildItem $fx -Filter 'DTSApo4xWinRTComponent.dll' -ErrorAction SilentlyContinue)) {
            foreach ($k in $saved, $keep) {
                try { New-Item -ItemType Directory -Force -Path $k | Out-Null; Copy-Item (Join-Path $fx '*') $k -Force } catch { }
            }
            Write-Host " - saved a copy of the audio components to $saved"
        }
        elseif (-not $hp) {
            foreach ($k in $saved, $keep) {
                if (Test-Path (Join-Path $k 'DTSApo4xWinRTComponent.dll')) {
                    Copy-Item (Join-Path $k '*') $fx -Force
                    Write-Host " - HP app not installed: using the saved audio components ($k)"
                    break
                }
            }
        }
        Write-Host " - audio components bundled ($((Get-ChildItem $fx).Count) files)"
        if (-not (Get-ChildItem $fx -Filter 'DTSApo4xWinRTComponent.dll' -ErrorAction SilentlyContinue)) {
            Write-Host ' - the HP app (myHP) is not installed, so the DTS/Realtek audio controls will be unavailable.' -ForegroundColor Yellow
            Write-Host '   Install "HP" from the Microsoft Store, re-run install.cmd, then you can uninstall the HP app again.' -ForegroundColor Yellow
        }
    } catch { Write-Host " - audio components not bundled: $($_.Exception.Message)" -ForegroundColor Yellow }

    $exe = Join-Path $dst 'HPControl.Service.exe'
    if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
        Write-Host ' - registering service'
        New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'HP Control Service' -StartupType Automatic `
            -Description 'Fan, thermal mode and battery control for HP OmniBook (HP Control).' | Out-Null
    }
    & sc.exe failure $svc reset= 86400 actions= restart/5000/restart/15000/none/0 | Out-Null
    & sc.exe config $svc start= auto | Out-Null
    Start-Service $svc
    Write-Host ' - service started' -ForegroundColor Green

    # Start menu shortcut (all users)
    $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\HP Control.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $dst 'HPControl.exe'
    $s.WorkingDirectory = $dst
    $s.Description = 'HP Control - fans, battery and audio'
    $s.Save()
    Write-Host ' - Start menu shortcut created'

    # tray helper at sign-in (current user)
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HPControlAgent' -Value "`"$(Join-Path $dst 'HPControl.exe')`" --agent"
    Write-Host ' - tray helper set to start at sign-in'
    Write-Host ''
    Write-Host 'Done. HP Control will open now.' -ForegroundColor Green
}
catch {
    Write-Host "INSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Log: $log"
    Read-Host 'Press Enter to close'
    exit 1
}
finally { Stop-Transcript | Out-Null }
Start-Sleep -Seconds 2
