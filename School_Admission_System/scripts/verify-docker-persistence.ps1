# Run against a local development Compose stack with no concurrent data writes.
# Creates dummy records, recreates containers, and retains the named database volume.
param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [string]$FrontendUrl = 'http://localhost:3000'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$needsRestore = $false
Push-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Compose([string[]]$Arguments) {
    & docker compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($Arguments -join ' ') failed." }
}

function Wait-Ready {
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while ($timeout.Elapsed.TotalSeconds -lt 90) {
        try {
            $health = Invoke-RestMethod -Uri "$ApiBaseUrl/health" -TimeoutSec 3
            $frontend = Invoke-WebRequest -UseBasicParsing -Uri $FrontendUrl -TimeoutSec 3
            if ($health.ready -eq $true -and $frontend.StatusCode -eq 200) { return }
        } catch { } # Containers can still be starting; retry within a bounded deadline.
        Start-Sleep -Milliseconds 250
    }
    throw 'API/database readiness or frontend HTTP availability did not recover within 90 seconds.'
}

function Invoke-Database([string]$Sql) {
    $result = $Sql | & docker compose exec -T db sh -c 'psql -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed.' }
    return ($result -join "`n").Trim()
}

function Get-DatabaseVolume {
    $dbContainer = & docker compose ps -q db
    if ($LASTEXITCODE -ne 0 -or !$dbContainer) { throw 'Database container not found.' }
    $inspection = & docker inspect $dbContainer | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect database mount.' }
    $mount = @($inspection[0].Mounts | Where-Object Destination -eq '/var/lib/postgresql/data')
    if ($mount.Count -ne 1 -or $mount[0].Type -ne 'volume') { throw 'PostgreSQL is not using a named volume.' }
    return $mount[0].Name
}

function New-TestHold([string]$ClassId, [string]$StudentId) {
    $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$ApiBaseUrl/classes/$ClassId/holds" `
        -Headers @{ 'X-Student-Id' = $StudentId }
    if ($response.StatusCode -ne 201) { throw 'Expected hold creation HTTP 201.' }
    return $response.Content | ConvertFrom-Json
}

try {
    Invoke-Compose -Arguments @('config', '--quiet')
    Invoke-Compose -Arguments @('up', '--build', '-d')
    Wait-Ready

    $studentNumber = 90000000000000000L + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $confirmedOwner = $studentNumber.ToString()
    $heldOwner = ($studentNumber + 1).ToString()
    $name = "Persistence verification - $([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $body = @{ name = $name; totalSeats = 2 } | ConvertTo-Json
    $created = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$ApiBaseUrl/classes" `
        -ContentType 'application/json' -Body $body
    if ($created.StatusCode -ne 201) { throw 'Expected class creation HTTP 201.' }
    $admissionClass = $created.Content | ConvertFrom-Json
    $confirmed = New-TestHold $admissionClass.id $confirmedOwner
    $confirmation = Invoke-WebRequest -UseBasicParsing -Method Post `
        -Uri "$ApiBaseUrl/holds/$($confirmed.holdId)/confirm" -Headers @{ 'X-Student-Id' = $confirmedOwner }
    if ($confirmation.StatusCode -ne 200) { throw 'Expected confirmation HTTP 200.' }
    $held = New-TestHold $admissionClass.id $heldOwner
    if ([DateTimeOffset]::Parse($held.expiresAt) -lt [DateTimeOffset]::UtcNow.AddSeconds(180)) {
        throw 'Use a hold duration of at least 180 seconds for this restart check (default: 600).'
    }

    $classBefore = Invoke-RestMethod -Uri "$ApiBaseUrl/classes/$($admissionClass.id)" | ConvertTo-Json -Compress
    $confirmedBefore = Invoke-RestMethod -Uri "$ApiBaseUrl/holds/$($confirmed.holdId)" | ConvertTo-Json -Compress
    $heldBefore = Invoke-RestMethod -Uri "$ApiBaseUrl/holds/$($held.holdId)" | ConvertTo-Json -Compress
    # Compare every primary key, not just counts, without assuming other holds cannot naturally expire.
    $snapshotSql = @'
SELECT json_build_object(
  'classes', COALESCE((SELECT json_agg("Id" ORDER BY "Id") FROM "Classes"), '[]'::json),
  'holds', COALESCE((SELECT json_agg("Id" ORDER BY "Id") FROM "Holds"), '[]'::json),
  'migrations', (SELECT json_agg("MigrationId" ORDER BY "MigrationId") FROM "__EFMigrationsHistory")
)::text;
'@
    $snapshotBefore = Invoke-Database $snapshotSql
    $volumeBefore = Get-DatabaseVolume
    Write-Output "Before restart: named volume=$volumeBefore; fixture HELD=1, CONFIRMED=1, available=0."

    $needsRestore = $true
    Invoke-Compose -Arguments @('down')
    $retainedVolume = & docker volume inspect --format '{{.Name}}' $volumeBefore
    if ($LASTEXITCODE -ne 0 -or $retainedVolume -ne $volumeBefore) { throw 'Named volume was not retained.' }
    Invoke-Compose -Arguments @('up', '-d')
    Wait-Ready
    $needsRestore = $false

    if ((Get-DatabaseVolume) -ne $volumeBefore) { throw 'Database volume changed after container recreation.' }
    if ((Invoke-Database $snapshotSql) -cne $snapshotBefore) { throw 'Database record IDs or migration history changed across restart.' }
    $classAfter = Invoke-RestMethod -Uri "$ApiBaseUrl/classes/$($admissionClass.id)"
    if (($classAfter | ConvertTo-Json -Compress) -cne $classBefore) { throw 'Class details/counts changed across restart.' }
    if ($classAfter.heldSeats -ne 1 -or $classAfter.confirmedSeats -ne 1 -or $classAfter.availableSeats -ne 0) {
        throw 'Expected exactly one HELD and one CONFIRMED seat after restart.'
    }
    $confirmedAfter = Invoke-RestMethod -Uri "$ApiBaseUrl/holds/$($confirmed.holdId)" | ConvertTo-Json -Compress
    $heldAfter = Invoke-RestMethod -Uri "$ApiBaseUrl/holds/$($held.holdId)" | ConvertTo-Json -Compress
    if ($confirmedAfter -cne $confirmedBefore -or $heldAfter -cne $heldBefore) { throw 'Hold fields/statuses/deadlines changed across restart.' }
    foreach ($owner in @($confirmedOwner, $heldOwner)) {
        $history = Invoke-RestMethod -Uri "$ApiBaseUrl/me/holds" -Headers @{ 'X-Student-Id' = $owner }
        $record = @($history | Where-Object classId -eq $admissionClass.id)
        if ($record.Count -ne 1 -or $record[0].className -ne $name) { throw 'Student history was not preserved.' }
    }
    $snapshot = $snapshotBefore | ConvertFrom-Json
    Write-Output "PASS: all $(@($snapshot.classes).Count) class IDs and $(@($snapshot.holds).Count) hold IDs retained; migrations unchanged."
    Write-Output 'PASS: class, HELD/CONFIRMED records, deadlines, seat counts, and owner histories survived compose down/up.'
    Write-Output 'PASS: frontend HTTP 200 and database-aware API readiness recovered. No volume was deleted.'
    Write-Output "Dummy fixture remains for inspection: $name ($($admissionClass.id))."
} finally {
    if ($needsRestore) {
        Write-Warning 'Verification failed during restart; restoring the Compose services.'
        & docker compose up -d
    }
    Pop-Location
}
