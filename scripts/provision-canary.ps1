<#
.SYNOPSIS
    Issue 10.2 -- provision the PHI allowlist canary app + keys.

.DESCRIPTION
    Idempotent. Creates a single `canary-test` application with Dev + Prod
    environments and mints one ServerApi key per environment. The canary
    workflow (.github/workflows/canary.yml) uses these keys to prove the
    deployed allowlist still rejects forbidden (PHI) fields.

    Capture the printed app id and plaintext keys and store them as GitHub
    repo secrets so the canary workflow can read them:
      CANARY_APP_ID    = <app id printed below>
      CANARY_KEY_DEV   = <Dev ServerApi key>
      CANARY_KEY_PROD  = <Prod ServerApi key>

    Also set the platform's `Observability:CanaryApplicationId` app setting
    (Dev + Prod) to CANARY_APP_ID so the dashboard namespaces canary rows out
    of real tenants' views.

    Plaintext keys are printed once and are NOT retrievable afterwards.

.PARAMETER ApiBase
    Adaptive Observability API base URL. Defaults to obs-api-dev.

.PARAMETER AdminKey
    The X-Observability-Admin-Key value. If omitted, pulls from the
    AdaptiveToolsKeyVault secret named ObservabilityAdminKey via az CLI.

.EXAMPLE
    .\provision-canary.ps1
    # Pulls admin key from KV, hits obs-api-dev, creates canary-test + 2 keys.
#>

param(
    [string]$ApiBase = "https://obs-api-dev.azurewebsites.net",
    [string]$AdminKey = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    Write-Host "Fetching ObservabilityAdminKey from AdaptiveToolsKeyVault..."
    $AdminKey = az keyvault secret show `
        --vault-name "AdaptiveToolsKeyVault" `
        --name "ObservabilityAdminKey" `
        --query value -o tsv
    if ([string]::IsNullOrWhiteSpace($AdminKey)) {
        throw "Could not fetch admin key. Pass -AdminKey or check az login."
    }
}

$headers = @{
    "X-Observability-Admin-Key" = $AdminKey
    "Content-Type"              = "application/json"
}

function Create-App([string]$slug, [string]$name, [string]$description) {
    $body = @{
        name         = $name
        slug         = $slug
        description  = $description
        environments = @("Development", "Production")
    } | ConvertTo-Json -Compress

    Write-Host ""
    Write-Host "POST /api/admin/apps  ($slug)" -ForegroundColor Cyan
    $resp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/admin/apps" `
        -Headers $headers `
        -Body $body
    $envNames = ($resp.environments | ForEach-Object { $_.name }) -join ","
    Write-Host ("  -> created={0} app_id={1} envs={2}" -f $resp.created, $resp.id, $envNames)
    return $resp
}

function Mint-Key([string]$slug, [string]$env, [string]$keyType) {
    $body = @{ key_type = $keyType } | ConvertTo-Json -Compress

    Write-Host ""
    Write-Host "POST /api/admin/apps/$slug/environments/$env/keys  ($keyType)" -ForegroundColor Cyan
    $resp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/admin/apps/$slug/environments/$env/keys" `
        -Headers $headers `
        -Body $body
    Write-Host "  PLAINTEXT KEY (shown once -- capture now):" -ForegroundColor Yellow
    Write-Host ("    {0}" -f $resp.plaintext_key) -ForegroundColor Yellow
    Write-Host ("  key_id={0}  key_type={1}  env={2}" -f $resp.id, $resp.key_type, $env)
    return $resp
}

Write-Host "=== Adaptive Observability -- PHI allowlist canary provisioning (Issue 10.2) ==="
Write-Host "API base: $ApiBase"

$app = Create-App -slug "canary-test" -name "Allowlist Canary" -description "Issue 10.2 -- scheduled PHI allowlist regression canary. Not a real tenant."

Write-Host ""
Write-Host "--- Minting server keys for canary-test ---"
Mint-Key -slug "canary-test" -env "Development" -keyType "ServerApi" | Out-Null
Mint-Key -slug "canary-test" -env "Production"  -keyType "ServerApi" | Out-Null

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Green
Write-Host "Next steps:"
Write-Host ("  1. Set GitHub repo secrets: CANARY_APP_ID={0}, CANARY_KEY_DEV / CANARY_KEY_PROD (keys above)." -f $app.id)
Write-Host "  2. Set the platform app setting Observability:CanaryApplicationId on obs-api-dev AND obs-api-prod:"
Write-Host ("     az webapp config appsettings set -g AdaptiveTools -n obs-api-dev  --settings Observability__CanaryApplicationId={0}" -f $app.id)
Write-Host ("     az webapp config appsettings set -g AdaptiveTools -n obs-api-prod --settings Observability__CanaryApplicationId={0}" -f $app.id)
Write-Host "  3. Enable .github/workflows/canary.yml (cron + workflow_dispatch)."
