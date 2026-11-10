# Proves a successful payment survives SQL Server being down when its result arrives.
# Start the full stack first: docker compose --profile app up -d --build --wait
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$AdminEmail = 'admin@ticketing.local',
    [string]$AdminPassword = 'Local_Dev_Admin_1!',
    [int]$OutageSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Send-Json($Path, $Body, $Token) {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$Path" -ContentType 'application/json' `
        -Headers $headers -Body ($Body | ConvertTo-Json -Depth 5)
}

function Get-QueueDepth($Name) {
    $line = docker compose exec -T rabbitmq rabbitmqctl -q list_queues name messages | Where-Object { $_ -match "^$([regex]::Escape($Name))\s" }
    return [int](($line -split '\s+')[1])
}

$root = Split-Path -Parent $PSScriptRoot
$admin = (Send-Json '/api/auth/login' @{ email = $AdminEmail; password = $AdminPassword }).token
$customer = (Send-Json '/api/auth/register' @{
    email = "outage-$([guid]::NewGuid().ToString('N'))@tests.local"
    password = 'Outage_Check_Password_1!'
}).token
$event = Send-Json '/api/events' @{
    name = 'SQL outage check'
    venue = 'Hall'
    startsAt = (Get-Date).ToUniversalTime().AddDays(30).ToString('o')
    seats = @(@{ label = 'A1'; priceCents = 15000 })
} $admin
$order = Send-Json "/api/events/$($event.id)/seats/$($event.seats[0].id)/hold" @{} $customer

Push-Location $root
try {
    # The worker is held back so the result can only arrive once SQL Server is down.
    docker compose stop payment-worker | Out-Null
    Send-Json "/api/orders/$($order.id)/pay" @{ paymentToken = 'tok_ok' } $customer | Out-Null
    Start-Sleep -Seconds 3
    docker compose stop sql | Out-Null
    docker compose start payment-worker | Out-Null
    Start-Sleep -Seconds $OutageSeconds

    $deadDuringOutage = Get-QueueDepth 'payments.completed.dead'
    Write-Output "Dead-lettered during the outage: $deadDuringOutage"

    docker compose start sql | Out-Null
    $container = docker compose ps -q sql
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 1
        $health = docker inspect --format '{{.State.Health.Status}}' $container
    } while ($health -ne 'healthy' -and (Get-Date) -lt $deadline)
    Write-Output "SQL Server after outage: $health"
}
finally {
    docker compose start sql payment-worker | Out-Null
    Pop-Location
}

$status = 'unknown'
$deadline = (Get-Date).AddSeconds(60)
do {
    try {
        $current = Invoke-RestMethod -Uri "$BaseUrl/api/orders/$($order.id)" -Headers @{ Authorization = "Bearer $customer" }
        $status = $current.status
    }
    catch {
        $status = 'api not ready'
    }
    if ($status -eq 'Paid') { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)

Write-Output "Order after outage: $status"

if ($status -ne 'Paid' -or $deadDuringOutage -ne 0) {
    Write-Output 'RESULT: FAILED'
    exit 1
}

Write-Output 'RESULT: PASSED'
