#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$serviceName = 'LdmsOutletCameraHelper'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "$serviceName is not installed."
    return
}

sc.exe stop $serviceName | Out-Null
Start-Sleep -Seconds 2
sc.exe delete $serviceName | Out-Null
Write-Host "$serviceName removed. The config at $env:ProgramData\LdmsOutletCameraHelper was left in place."
