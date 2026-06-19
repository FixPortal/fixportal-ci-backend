#requires -Version 7.0
<#
.SYNOPSIS
  One-time Azure bootstrap for the fixportal-ci-backend deploy pipeline.
.DESCRIPTION
  Creates the resource group, an Entra app + service principal with a GitHub
  OIDC federated credential (main branch), grants Contributor on the RG, and
  sets the GitHub repo secrets the deploy workflow needs. Re-runnable.
  Prerequisites: an authenticated 'az login' and 'gh auth login' session.
#>
[CmdletBinding()]
param(
  [string]$SubscriptionId = (az account show --query id -o tsv),
  [string]$ResourceGroup  = 'rg-fixportal-ci-backend',
  [string]$Location       = 'uksouth',
  # Canonical mixed-case repo name. Federated-credential subject matching is
  # CASE-SENSITIVE (Entra Aug-2024 change); do not lowercase.
  [string]$Repo           = 'FixPortal/fixportal-ci-backend',
  [string]$EntraAppName   = 'fixportal-ci-backend-deploy',
  [string]$AcrName,
  [string]$AcrLoginServer,
  [string]$AcaEnvironmentId,
  [string]$PullIdentityId,
  [string]$CustomDomainCertId,
  [string]$CustomDomainName,
  [string]$CorsAllowedOrigins,
  [string]$CiAdminKey,
  [Parameter(Mandatory)]
  [string]$GitHubToken
)

$ErrorActionPreference = 'Stop'

function Assert-ExitCode {
  param([string]$What)
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $What (exit $LASTEXITCODE)" }
}

function Set-GitHubSecret {
  param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Value
  )

  gh secret set $Name --repo $Repo --body $Value
  Assert-ExitCode "gh secret $Name"
}

function Set-GitHubSecretIfValue {
  param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Value
  )

  if (-not [string]::IsNullOrWhiteSpace($Value)) {
    Set-GitHubSecret -Name $Name -Value $Value
  }
}

function Set-GitHubVariableIfValue {
  param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Value
  )

  if (-not [string]::IsNullOrWhiteSpace($Value)) {
    gh variable set $Name --repo $Repo --body $Value
    Assert-ExitCode "gh variable $Name"
  }
}

function Get-HasGitHubSecret {
  param([Parameter(Mandatory)][string]$Name)

  $match = gh secret list --repo $Repo | Select-String -Pattern "^$([regex]::Escape($Name))\b"
  Assert-ExitCode "gh secret list"
  return $null -ne $match
}

function Get-HasGitHubVariable {
  param([Parameter(Mandatory)][string]$Name)

  $match = gh variable list --repo $Repo | Select-String -Pattern "^$([regex]::Escape($Name))\b"
  Assert-ExitCode "gh variable list"
  return $null -ne $match
}

function Sync-GitHubSecret {
  param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Value,
    [bool]$WasProvided
  )

  if (-not $WasProvided) {
    return
  }

  if ([string]::IsNullOrWhiteSpace($Value)) {
    if (Get-HasGitHubSecret -Name $Name) {
      gh secret delete $Name --repo $Repo
      Assert-ExitCode "gh secret delete $Name"
    }
    return
  }

  Set-GitHubSecret -Name $Name -Value $Value
}

function Sync-GitHubVariable {
  param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Value,
    [bool]$WasProvided
  )

  if (-not $WasProvided) {
    return
  }

  if ([string]::IsNullOrWhiteSpace($Value)) {
    if (Get-HasGitHubVariable -Name $Name) {
      gh variable delete $Name --repo $Repo
      Assert-ExitCode "gh variable delete $Name"
    }
    return
  }

  gh variable set $Name --repo $Repo --body $Value
  Assert-ExitCode "gh variable $Name"
}

Write-Host "== Selecting subscription $SubscriptionId =="
az account set --subscription $SubscriptionId
Assert-ExitCode "account set"

Write-Host "== Registering resource providers =="
$rps = @('Microsoft.Web', 'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.App')
foreach ($rp in $rps) {
  az provider register --namespace $rp --wait
  Assert-ExitCode "register $rp"
}

Write-Host "== Creating resource group $ResourceGroup ($Location) =="
az group create --name $ResourceGroup --location $Location --output none
Assert-ExitCode "group create"

Write-Host "== Ensuring Entra application '$EntraAppName' =="
$appIds = @(az ad app list --display-name $EntraAppName --query "[].appId" -o json | ConvertFrom-Json)
Assert-ExitCode "app list"
if ($appIds.Count -gt 1) {
  throw "Multiple Entra apps match '$EntraAppName'. Use a unique display name or clean up the duplicates."
}

$appId = if ($appIds.Count -eq 1) { $appIds[0] } else { $null }
if (-not $appId) {
  $appId = az ad app create --display-name $EntraAppName --query appId -o tsv
  Assert-ExitCode "app create"
  Write-Host "Created app $appId"
} else {
  Write-Host "Reusing app $appId"
}

Write-Host "== Ensuring service principal =="
$spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
Assert-ExitCode "sp list"
if (-not $spId) {
  az ad sp create --id $appId --output none
  Assert-ExitCode "sp create"
  Write-Host "Created service principal for $appId"
}

Write-Host "== Ensuring federated credential (main branch) =="
$subject = "repo:${Repo}:ref:refs/heads/main"
$existing = az ad app federated-credential list --id $appId `
  --query "[?subject=='$subject'].id" -o tsv
Assert-ExitCode "fed-cred list"
if (-not $existing) {
  # File-based body: inline JSON args have their quotes stripped by PowerShell.
  $fc = [ordered]@{
    name      = 'github-main'
    issuer    = 'https://token.actions.githubusercontent.com'
    subject   = $subject
    audiences = @('api://AzureADTokenExchange')
  }
  $fcPath = New-TemporaryFile
  [IO.File]::WriteAllText($fcPath.FullName, ($fc | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
  try {
    az ad app federated-credential create --id $appId --parameters $fcPath.FullName --output none
    Assert-ExitCode "fed-cred create"
    Write-Host "Created federated credential for $subject"
  } finally {
    Remove-Item $fcPath -ErrorAction SilentlyContinue
  }
} else {
  Write-Host "Federated credential already present for $subject"
}

Write-Host "== Granting Contributor on the resource group =="
$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
$existingAssignment = az role assignment list `
  --assignee $appId `
  --role 'Contributor' `
  --scope $scope `
  --query "[0].id" -o tsv
Assert-ExitCode "role assignment list"
if ($existingAssignment) {
  Write-Host "Contributor assignment already present"
} else {
  az role assignment create --assignee $appId --role 'Contributor' --scope $scope --output none
  Assert-ExitCode "role assignment create"
  Write-Host "Contributor granted (RBAC propagation ~30-60s before the first deploy)"
}

Write-Host "== Setting GitHub repo secrets =="
$tenantId = az account show --query tenantId -o tsv
Assert-ExitCode "account show tenant"
Set-GitHubSecret -Name AZURE_CLIENT_ID -Value $appId
Set-GitHubSecret -Name AZURE_TENANT_ID -Value $tenantId
Set-GitHubSecret -Name AZURE_SUBSCRIPTION_ID -Value $SubscriptionId
Set-GitHubSecret -Name AZURE_RESOURCE_GROUP -Value $ResourceGroup
Set-GitHubSecret -Name DASHBOARD_GH_TOKEN -Value $GitHubToken
Set-GitHubSecretIfValue -Name ACR_NAME -Value $AcrName
Set-GitHubSecretIfValue -Name ACR_LOGIN_SERVER -Value $AcrLoginServer
Set-GitHubSecretIfValue -Name ACA_ENVIRONMENT_ID -Value $AcaEnvironmentId
Set-GitHubSecretIfValue -Name PULL_IDENTITY_ID -Value $PullIdentityId
Sync-GitHubSecret -Name CUSTOM_DOMAIN_CERT_ID -Value $CustomDomainCertId -WasProvided $PSBoundParameters.ContainsKey('CustomDomainCertId')
Sync-GitHubSecret -Name CORS_ALLOWED_ORIGINS -Value $CorsAllowedOrigins -WasProvided $PSBoundParameters.ContainsKey('CorsAllowedOrigins')
Sync-GitHubSecret -Name CI_ADMIN_KEY -Value $CiAdminKey -WasProvided $PSBoundParameters.ContainsKey('CiAdminKey')
Sync-GitHubVariable -Name CUSTOM_DOMAIN_NAME -Value $CustomDomainName -WasProvided $PSBoundParameters.ContainsKey('CustomDomainName')

$pending = @()
if ([string]::IsNullOrWhiteSpace($AcrName)) { $pending += 'Secret ACR_NAME' }
if ([string]::IsNullOrWhiteSpace($AcrLoginServer)) { $pending += 'Secret ACR_LOGIN_SERVER' }
if ([string]::IsNullOrWhiteSpace($AcaEnvironmentId)) { $pending += 'Secret ACA_ENVIRONMENT_ID' }
if ([string]::IsNullOrWhiteSpace($PullIdentityId)) { $pending += 'Secret PULL_IDENTITY_ID' }
if ([string]::IsNullOrWhiteSpace($CustomDomainCertId) -and -not [string]::IsNullOrWhiteSpace($CustomDomainName)) {
  $pending += 'Secret CUSTOM_DOMAIN_CERT_ID'
}
if ([string]::IsNullOrWhiteSpace($CustomDomainName) -and -not [string]::IsNullOrWhiteSpace($CustomDomainCertId)) {
  $pending += 'Variable CUSTOM_DOMAIN_NAME'
}
if ([string]::IsNullOrWhiteSpace($CiAdminKey) -and -not (Get-HasGitHubSecret -Name CI_ADMIN_KEY)) {
  $pending += 'Secret CI_ADMIN_KEY'
}

Write-Host ""
if ($pending.Count -eq 0) {
  Write-Host "Bootstrap complete. Push to main (or run the CI workflow manually) to deploy."
} else {
  Write-Host "Bootstrap complete for identity + base repo secrets, but deployment is NOT ready yet."
  Write-Host "Still set these repository settings before deploying:"
  foreach ($item in $pending) {
    Write-Host " - $item"
  }
}
