#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$serviceName = 'LdmsOutletCameraHelper'
$exePath = Join-Path $PSScriptRoot 'LdmsOutletCameraHelper.exe'
$configDir = Join-Path $env:ProgramData 'LdmsOutletCameraHelper'
$configPath = Join-Path $configDir 'appsettings.json'

if (-not (Test-Path $exePath)) {
    throw "LdmsOutletCameraHelper.exe was not found next to this script. Publish the project first."
}

New-Item -ItemType Directory -Force -Path $configDir | Out-Null
if (-not (Test-Path $configPath)) {
    Copy-Item (Join-Path $PSScriptRoot 'appsettings.json') $configPath
    Write-Host "Created $configPath"
    Write-Host "Edit it with this outlet's camera SnapshotUrl, Username and Password, then re-run this script (or restart the service)."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing the existing $serviceName service..."
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "LDMS Outlet Camera Helper" | Out-Null
sc.exe description $serviceName "Fetches item photos from the outlet camera for the LDMS collection-order screen." | Out-Null
sc.exe failure $serviceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe start $serviceName | Out-Null

Write-Host "$serviceName installed and started (listening on http://127.0.0.1 - see ListenPort in $configPath)."
