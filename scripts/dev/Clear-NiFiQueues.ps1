[CmdletBinding()]
param(
    [string]$NifiUrl = "https://localhost:8543/nifi-api",
    [string]$Username = $env:NIFI_ADMIN_USER,
    [string]$Password = $env:NIFI_ADMIN_PASSWORD
)

$uri = [uri]$NifiUrl
if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'A trusted HTTPS NiFi API URL is required.'
}
if (-not $Username -or -not $Password) {
    throw 'Set NIFI_ADMIN_USER and NIFI_ADMIN_PASSWORD or pass them explicitly.'
}

Write-Host "Logging into Apache NiFi API ($NifiUrl)..." -ForegroundColor Cyan
$token = Invoke-RestMethod -MaximumRedirection 0 -Uri "$NifiUrl/access/token" -Method Post -Body @{
    username = $Username
    password = $Password
} -ContentType "application/x-www-form-urlencoded"
$headers = @{ "Authorization" = "Bearer $token" }

function Clear-GroupQueues($groupId, $groupName) {
    Write-Host "Checking queues in Process Group: $groupName ($groupId)..." -ForegroundColor Yellow
    $pgFlow = Invoke-RestMethod -MaximumRedirection 0 -Uri "$NifiUrl/flow/process-groups/$groupId" -Headers $headers
    
    foreach ($conn in $pgFlow.processGroupFlow.flow.connections) {
        $count = $conn.status.aggregateSnapshot.flowFilesQueued
        if ($count -gt 0) {
            $connId = $conn.id
            $src = $conn.component.source.name
            $dst = $conn.component.destination.name
            Write-Host "  -> Dropping $count FlowFiles from queue: [$src -> $dst] ($connId)" -ForegroundColor Magenta
            try {
                $drop = Invoke-RestMethod -MaximumRedirection 0 -Uri "$NifiUrl/flowfile-queues/$connId/drop-requests" -Method Post -Headers $headers
            } catch {
                Write-Warning "Failed to drop queue $connId : $_"
            }
        }
    }

    # Recurse sub-process groups
    foreach ($sub in $pgFlow.processGroupFlow.flow.processGroups) {
        Clear-GroupQueues $sub.id $sub.component.name
    }
}

$root = Invoke-RestMethod -MaximumRedirection 0 -Uri "$NifiUrl/flow/process-groups/root" -Headers $headers
Clear-GroupQueues "root" "NiFi Flow Root"

Write-Host "All NiFi queues cleared successfully!" -ForegroundColor Green
