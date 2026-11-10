# Proves payments still complete after RabbitMQ restarts underneath a running system.
# Start the full stack first: docker compose --profile app up -d --build --wait
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$AdminEmail = 'admin@ticketing.local',
    [string]$AdminPassword = 'Local_Dev_Admin_1!',
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'

function Send-Json($Path, $Body, $Token) {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$Path" -ContentType 'application/json' `
        -Headers $headers -Body ($Body | ConvertTo-Json -Depth 5)
}

function Invoke-Payment($Event, $Label, $Token) {
    $seat = $Event.seats | Where-Object label -eq $Label
    $order = Send-Json "/api/events/$($Event.id)/seats/$($seat.id)/hold" @{} $Token
    Send-Json "/api/orders/$($order.id)/pay" @{ paymentToken = 'tok_ok' } $Token | Out-Null

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $current = Invoke-RestMethod -Uri "$BaseUrl/api/orders/$($order.id)" -Headers @{ Authorization = "Bearer $Token" }
        if ($current.status -eq 'Paid') { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $current.status
}

$root = Split-Path -Parent $PSScriptRoot
$admin = (Send-Json '/api/auth/login' @{ email = $AdminEmail; password = $AdminPassword }).token
$customer = (Send-Json '/api/auth/register' @{
    email = "restart-$([guid]::NewGuid().ToString('N'))@tests.local"
    password = 'Restart_Check_Password_1!'
}).token
$event = Send-Json '/api/events' @{
    name = 'Broker restart check'
    venue = 'Hall'
    startsAt = (Get-Date).ToUniversalTime().AddDays(30).ToString('o')
    seats = @(@{ label = 'A1'; priceCents = 15000 }, @{ label = 'A2'; priceCents = 15000 })
} $admin

$before = Invoke-Payment $event 'A1' $customer
Write-Output "Before restart: $before"

Push-Location $root
try {
    docker compose restart rabbitmq | Out-Null
    $container = docker compose ps -q rabbitmq
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 1
        $health = docker inspect --format '{{.State.Health.Status}}' $container
    } while ($health -ne 'healthy' -and (Get-Date) -lt $deadline)
    Write-Output "Broker after restart: $health"
}
finally {
    Pop-Location
}

$after = Invoke-Payment $event 'A2' $customer
Write-Output "After restart: $after"

if ($before -ne 'Paid' -or $after -ne 'Paid') {
    Write-Output 'RESULT: FAILED'
    exit 1
}

Write-Output 'RESULT: PASSED'
