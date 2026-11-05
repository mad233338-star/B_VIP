<#
.SYNOPSIS
    End-to-end smoke test for a running B_VIP backend.

.DESCRIPTION
    Exercises the real HTTP surface in the order a customer uses it: health, anonymous access
    refusal, first-admin bootstrap, sign-in, user creation, license issue, device operation with a
    recorded result, diagnostic report, support ticket, license verification and audit history.

    Read-only with respect to configuration; it does create test data (a user named
    smoke.<random>, one license, one operation, one report, one ticket). Run it against a staging
    server, or against production knowing that those rows will exist.

.PARAMETER BaseUrl
    Backend base URL, e.g. http://127.0.0.1:5088

.PARAMETER BootstrapToken
    Bootstrap:FirstAdminToken. Needed only when no administrator exists yet; when the admin already
    exists the bootstrap call is expected to fail with 409 and the script continues.

.PARAMETER AdminUserName / AdminPassword
    Credentials used for the administrative calls.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\smoke-test.ps1 -BaseUrl http://127.0.0.1:5088 `
        -AdminUserName root.admin -AdminPassword 'Admin!Passw0rd-2026'
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://127.0.0.1:5088',
    [string] $BootstrapToken = '',
    [string] $AdminUserName = 'root.admin',
    [string] $AdminPassword = 'Admin!Passw0rd-2026',
    [string] $TechPassword = 'Smoke!Passw0rd-2026'
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$script:failures = 0
$script:steps = 0

function Write-Step {
    param([string] $Name, [bool] $Ok, [string] $Detail = '')
    $script:steps++
    if ($Ok) {
        Write-Host ("  PASS  {0} {1}" -f $Name, $Detail) -ForegroundColor Green
    }
    else {
        $script:failures++
        Write-Host ("  FAIL  {0} {1}" -f $Name, $Detail) -ForegroundColor Red
    }
}

function Invoke-Api {
    param(
        [string] $Method = 'GET',
        [string] $Path,
        $Body,
        [string] $Token,
        [hashtable] $Headers
    )

    $requestHeaders = @{}
    if ($Headers) { foreach ($key in $Headers.Keys) { $requestHeaders[$key] = $Headers[$key] } }
    if ($Token) { $requestHeaders['Authorization'] = "Bearer $Token" }

    $parameters = @{
        Uri                = "$BaseUrl$Path"
        Method             = $Method
        Headers            = $requestHeaders
        UseBasicParsing    = $true
        TimeoutSec         = 120
    }

    if ($null -ne $Body) {
        # charset is stated explicitly: the PowerShell 5.1 HTTP stack otherwise encodes a string
        # body with the system codepage, which breaks non-ASCII descriptions.
        $parameters['ContentType'] = 'application/json; charset=utf-8'
        $parameters['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress)
    }

    try {
        $response = Invoke-WebRequest @parameters
        return [pscustomobject]@{
            Status = [int] $response.StatusCode
            Body   = if ($response.Content) { $response.Content } else { '' }
            Json   = if ($response.Content) { try { $response.Content | ConvertFrom-Json } catch { $null } } else { $null }
        }
    }
    catch {
        $webResponse = $_.Exception.Response
        $status = 0
        $content = ''
        if ($webResponse -ne $null) {
            $status = [int] $webResponse.StatusCode
            try {
                $stream = $webResponse.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $content = $reader.ReadToEnd()
                }
            }
            catch { }
        }

        return [pscustomobject]@{
            Status = $status
            Body   = $content
            Json   = if ($content) { try { $content | ConvertFrom-Json } catch { $null } } else { $null }
        }
    }
}

function New-RandomSuffix { -join ((48..57) + (97..122) | Get-Random -Count 8 | ForEach-Object { [char]$_ }) }

Write-Host "B_VIP Tool smoke test against $BaseUrl" -ForegroundColor Cyan
Write-Host ''

# 1. Health
$health = Invoke-Api -Path '/api/health'
Write-Step 'GET /api/health' ($health.Status -eq 200) "(HTTP $($health.Status))"

# 2. Anonymous access is refused on a protected route
$anonymous = Invoke-Api -Path '/api/users'
Write-Step 'GET /api/users without a token is refused' ($anonymous.Status -eq 401) "(HTTP $($anonymous.Status))"

# 3. First admin bootstrap (409 when one already exists)
if ($BootstrapToken) {
    $bootstrap = Invoke-Api -Method 'POST' -Path '/api/bootstrap/first-admin' -Body @{
        token    = $BootstrapToken
        userName = $AdminUserName
        email    = "$AdminUserName@b-vip.test"
        password = $AdminPassword
    }
    Write-Step 'Bootstrap first administrator' ($bootstrap.Status -in 200, 201, 409) "(HTTP $($bootstrap.Status))"
}

# 4. Sign in as administrator
$login = Invoke-Api -Method 'POST' -Path '/api/auth/login' -Body @{ userName = $AdminUserName; password = $AdminPassword }
$adminToken = $login.Json.tokens.accessToken
Write-Step 'Administrator sign-in' ($login.Status -eq 200 -and $adminToken) "(HTTP $($login.Status))"
if (-not $adminToken) {
    Write-Host ''
    Write-Host "Cannot continue without an administrator session ($script:failures failure(s))." -ForegroundColor Red
    exit 1
}

# 5. Metadata endpoint
$meta = Invoke-Api -Path '/api/meta'
Write-Step 'GET /api/meta' ($meta.Status -eq 200) "(HTTP $($meta.Status))"

# 6. Create a technician account
$suffix = New-RandomSuffix
$techUserName = "smoke.$suffix"
$techEmail = "$techUserName@b-vip.test"
$createUser = Invoke-Api -Method 'POST' -Path '/api/users' -Token $adminToken -Body @{
    userName    = $techUserName
    email       = $techEmail
    displayName = "Smoke $suffix"
    password    = $TechPassword
}
$techId = $createUser.Json.id
Write-Step 'Create technician account' ($createUser.Status -in 200, 201 -and $techId) "(HTTP $($createUser.Status))"

# 7. Issue a license for that account
$license = Invoke-Api -Method 'POST' -Path '/api/licenses' -Token $adminToken -Body @{ userId = $techId; planCode = 'trial' }
$licenseKey = $license.Json.licenseKey
Write-Step 'Issue a trial license' ($license.Status -in 200, 201 -and $licenseKey) "(HTTP $($license.Status))"

# 8. Sign in as the technician
$techLogin = Invoke-Api -Method 'POST' -Path '/api/auth/login' -Body @{ userName = $techUserName; password = $TechPassword }
$techToken = $techLogin.Json.tokens.accessToken
Write-Step 'Technician sign-in' ($techLogin.Status -eq 200 -and $techToken) "(HTTP $($techLogin.Status))"

# 9. Device operation lifecycle
$operation = Invoke-Api -Method 'POST' -Path '/api/device-operations' -Token $techToken -Body @{ operationType = 'adb.devices' }
$operationId = $operation.Json.id
Write-Step 'Create a device operation' ($operation.Status -in 200, 201 -and $operationId) "(HTTP $($operation.Status))"

if ($operationId) {
    $result = Invoke-Api -Method 'POST' -Path "/api/device-operations/$operationId/result" -Token $techToken -Body @{
        exitCode    = 0
        timedOut    = $false
        cancelled   = $false
        output      = "List of devices attached`nSMOKE0001`tdevice"
        errorOutput = ''
        summary     = 'Smoke test: one device reported.'
        verified    = $true
    }
    Write-Step 'Record the real operation result' ($result.Status -eq 200 -and $result.Json.status -eq 'Completed') "(HTTP $($result.Status), status $($result.Json.status), exit $($result.Json.exitCode))"

    $events = Invoke-Api -Path "/api/device-operations/$operationId/events" -Token $techToken
    Write-Step 'Read the operation timeline' ($events.Status -eq 200 -and @($events.Json).Count -gt 0) "(HTTP $($events.Status))"
}

# 10. Diagnostic report
$report = Invoke-Api -Method 'POST' -Path '/api/diagnostic-reports' -Token $techToken -Body @{
    title                 = 'Smoke test diagnostic report'
    module                = 'android-diagnostics'
    deviceId              = 'SMOKE-0001'
    contentJson           = '{"battery":{"health":"good"}}'
    containsSensitiveData = $false
    redact                = $true
}
Write-Step 'Create a diagnostic report' ($report.Status -in 200, 201) "(HTTP $($report.Status))"

# 11. Support ticket
$ticket = Invoke-Api -Method 'POST' -Path '/api/tickets' -Token $techToken -Body @{
    title       = 'Smoke test ticket'
    description = 'Created by deploy/smoke-test.ps1 - safe to close.'
    priority    = 'Low'
}
$ticketId = $ticket.Json.id
Write-Step 'Create a support ticket' ($ticket.Status -in 200, 201 -and $ticketId) "(HTTP $($ticket.Status))"

if ($ticketId) {
    $message = Invoke-Api -Method 'POST' -Path "/api/tickets/$ticketId/messages" -Token $techToken -Body @{ body = 'Smoke test reply.' }
    Write-Step 'Reply on the ticket' ($message.Status -in 200, 201) "(HTTP $($message.Status))"

    $myTickets = Invoke-Api -Path '/api/tickets' -Token $techToken
    Write-Step 'List own tickets' ($myTickets.Status -eq 200) "(HTTP $($myTickets.Status))"
}

# 12. License verification with the technician's session
$verify = Invoke-Api -Method 'POST' -Path '/api/licenses/verify' -Token $techToken -Body @{
    licenseKey         = $licenseKey
    deviceFingerprint  = "smoke-$suffix"
    clientVersion      = '1.0.0'
    recordEvent        = $true
}
Write-Step 'Verify the license' ($verify.Status -eq 200) "(HTTP $($verify.Status))"

# 13. Audit trail is being written
$audit = Invoke-Api -Path '/api/admin/audit-events' -Token $adminToken
$auditCount = if ($audit.Json.items) { @($audit.Json.items).Count } else { 0 }
Write-Step 'Read the audit trail' ($audit.Status -eq 200 -and $auditCount -gt 0) "(HTTP $($audit.Status), $auditCount event(s))"

# 14. Sign out
$logout = Invoke-Api -Method 'POST' -Path '/api/auth/logout' -Token $techToken -Body @{ refreshToken = $techLogin.Json.tokens.refreshToken }
Write-Step 'Sign out' ($logout.Status -in 200, 204) "(HTTP $($logout.Status))"

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "Smoke test passed: $script:steps step(s), no failures." -ForegroundColor Green
    exit 0
}

Write-Host "Smoke test failed: $script:failures of $script:steps step(s) failed." -ForegroundColor Red
exit 1
