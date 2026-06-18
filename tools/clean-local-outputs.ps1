<#
.SYNOPSIS
Removes generated OpenPlanTrace local output folders that should not be committed.

.DESCRIPTION
Deletes ignored scan/test output folders such as artifacts, real-pdf-output,
TestResults, and coverage. The script verifies it is running from the
OpenPlanTrace repository root before removing anything.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'OpenPlanTrace.sln'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "This script must run from the OpenPlanTrace repository. Missing: $solutionPath"
}

$targets = @(
    'artifacts',
    'real-pdf-output',
    'TestResults',
    'coverage'
)

function Get-DirectorySizeBytes {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sum = Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum
    if ($null -eq $sum.Sum) {
        return 0
    }

    return [int64]$sum.Sum
}

$totalBytes = 0
foreach ($target in $targets) {
    $path = Join-Path $repoRoot $target
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        continue
    }

    $resolved = Resolve-Path -LiteralPath $path
    if (-not $resolved.Path.StartsWith($repoRoot.Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the repository: $($resolved.Path)"
    }

    $bytes = Get-DirectorySizeBytes -Path $resolved.Path
    $totalBytes += $bytes
    $mb = [math]::Round($bytes / 1MB, 2)
    if ($PSCmdlet.ShouldProcess($resolved.Path, "Remove generated local output folder ($mb MB)")) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
        Write-Host "Removed $target ($mb MB)"
    }
}

$totalMb = [math]::Round($totalBytes / 1MB, 2)
Write-Host "Potentially reclaimed: $totalMb MB"
