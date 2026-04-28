# Batch import for ephemeris samples in 10-year windows (AD 1600 to 2200).
# Bodies outside a window's date range return 0 samples silently.
# Run from repo root: .\import_ephemeris_batches.ps1

$projectPath = "backend/Sol.Api"
$startYear   = 1600
$endYear     = 2200

$year = $startYear
while ($year -le $endYear) {
    $windowEnd = if ($year + 9 -le $endYear) { $year + 9 } else { $endYear }
    $startStr  = "$year-01-01T00:00:00Z"
    $endStr    = "$windowEnd-12-31T00:00:00Z"

    Write-Host "$(Get-Date -Format 'HH:mm:ss')  $startStr → $endStr"
    dotnet run --project $projectPath --no-build -- import-samples $startStr $endStr
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Window $startStr - $endStr exited with code $LASTEXITCODE"
    }
    $year += 10
}

Write-Host "Done."
