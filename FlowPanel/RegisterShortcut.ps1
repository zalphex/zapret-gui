param([Parameter(Mandatory=$true)][string]$Shortcut)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'TaskbarIdentity.cs')
[ZapretTaskbarIdentity]::SetShortcut($Shortcut)
