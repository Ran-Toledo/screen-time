<#
.SYNOPSIS
    Runs the tests, then builds ScreenTime.Tracker.exe and ScreenTime.Dashboard.exe into one folder.

.DESCRIPTION
    Framework-dependent, single-file, win-x64 Release builds (requires the .NET 9 Desktop Runtime).
    Native libraries (SQLite, SkiaSharp) sit next to the two exes, so the folder is the install unit.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Output C:\Temp\ScreenTime -SkipTests
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'publish'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

if (-not $SkipTests) {
    dotnet test (Join-Path $PSScriptRoot 'ScreenTime.sln') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was published.' }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

foreach ($project in 'ScreenTime.Tracker', 'ScreenTime.Dashboard') {
    dotnet publish (Join-Path $PSScriptRoot "src\$project\$project.csproj") `
        --nologo -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Write-Host "`nPublished to $Output"
Get-ChildItem $Output | Format-Table Name, @{ Name = 'Size (KB)'; Expression = { [math]::Round($_.Length / 1KB) }; Align = 'Right' }
