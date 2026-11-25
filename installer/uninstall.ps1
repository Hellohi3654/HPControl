# HP Control uninstaller (runs elevated). Restores Smart Sense and any CPU power settings first.
$ErrorActionPreference = 'Continue'
$dst = Join-Path $env:ProgramFiles 'HP Control'
$svc = 'HPControlSvc'
Write-Host 'Removing HP Control...' -ForegroundColor Cyan
Get-Process HPControl -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    # the service restores power-plan values on stop
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $svc | Out-Null
    Write-Host ' - service removed'
}
# make sure the fan is back with the fan controller (HP Control may have been holding it off)
try {
    $bi = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
    $in = ([wmiclass]'root\wmi:hpqBDataIn').CreateInstance()
    $d = New-Object byte[] 28; $d[0] = 0x11; $d[1] = 0x96; $d[4] = 0x2B; $d[5] = 0x10
    $in.Sign = [byte[]](0x53, 0x45, 0x43, 0x55); $in.Command = 3; $in.CommandType = 1; $in.Size = 28; $in.hpqBData = $d
    $null = $bi.hpqBIOSInt128($in)
    Write-Host ' - fan handed back to the fan controller'
} catch { }
# make sure HP firmware is back on Smart Sense
try {
    $bi = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
    $in = ([wmiclass]'root\wmi:hpqBDataIn').CreateInstance()
    $in.Sign = [byte[]](0x53, 0x45, 0x43, 0x55); $in.Command = 2; $in.CommandType = 0x4C; $in.Size = 4; $in.hpqBData = [byte[]](5, 0, 0, 0)
    $null = $bi.hpqBIOSInt0($in)
    Write-Host ' - HP mode set back to Smart sense'
} catch { }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HPControlAgent' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\HP Control.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue
$keep = Read-Host 'Delete your HP Control settings too? (y/N)'
if ($keep -eq 'y') { Remove-Item (Join-Path $env:ProgramData 'HPControl') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'HP Control removed.' -ForegroundColor Green
Start-Sleep -Seconds 2
