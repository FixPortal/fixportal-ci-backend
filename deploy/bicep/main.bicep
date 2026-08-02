// Deploys the dashboard as an Azure Container App into an EXISTING Container Apps
// managed environment, pulling its image from an existing registry via a
// user-assigned identity that holds AcrPull. Container Apps is used (rather than
// App Service) because it shares quota with the rest of a typical estate and runs
// a single always-on replica for the in-process refresh worker.
//
// The environment-specific resource IDs below have no defaults: supply them at
// deploy time (the CI workflow passes them from GitHub Actions repository
// variables — see operator-handoff.md). This keeps deployer-specific identifiers
// out of source so the template is reusable as-is.

@description('Azure region.')
param location string = 'uksouth'

@description('Container app name (also the ingress hostname prefix).')
param appName string = 'fixportal-ci-backend'

@description('Resource ID of the existing Container Apps managed environment to host this app.')
param managedEnvironmentId string

@description('ACR login server the image is pulled from (e.g. myregistry.azurecr.io).')
param acrLoginServer string

@description('Resource ID of the user-assigned identity holding AcrPull on the registry.')
param pullIdentityId string

@description('Full container image reference (login-server/repo:tag).')
param image string

@description('GitHub org/owner that hosts the tracked repos.')
param gitHubOwner string

@description('Fine-grained read-only GitHub PAT (Actions, Pull requests, Contents, and Code scanning alerts: Read-only).')
@secure()
param gitHubToken string

@description('''GitHub App ID. Optional — empty keeps PAT authentication.
Supplied together with gitHubAppPrivateKey, this authenticates API calls as an App
installation instead. Required to read check runs at all: a fine-grained PAT is refused
on statusCheckRollup and has no "Checks" permission to grant. It also gives the dashboard
its own GraphQL points budget instead of sharing a human's.''')
param gitHubAppId string = ''

@description('PEM contents of the GitHub App private key. Optional — empty keeps PAT authentication.')
@secure()
param gitHubAppPrivateKey string = ''

@description('Shared key for the /api/dashboard/snapshot/admin endpoint. Must match the Admin__Key secret in the simulator backend.')
@secure()
param adminKey string

@description('Background refresh cadence in seconds. ETag conditional GETs keep a tight cadence within the GitHub rate budget (304s are not billed).')
param refreshSeconds int = 30

@description('''Origins permitted to GET the snapshot cross-origin (the FixPortal SPA).
Emitted as Cors__AllowedOrigins__N env vars and read by the API's CORS policy.
Empty (the default) allows no cross-origin reads.''')
param corsAllowedOrigins array = []

@description('Custom domain bound to the ingress (empty — the default — disables the binding and serves on the generated FQDN).')
param customDomainName string = ''

@description('''Resource ID of the Azure-managed certificate for the custom domain.
Created out-of-band by `az containerapp hostname bind` (managed certs cannot be
provisioned inline in Bicep — chicken-and-egg with the binding). Must be declared
here so incremental deployments preserve the binding instead of stripping it.
Leave empty when customDomainName is empty.''')
param customDomainCertificateId string = ''

var enableCustomDomainBinding = !empty(customDomainName) && !empty(customDomainCertificateId)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${pullIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // Declared so incremental `az deployment group create` runs preserve the
        // binding. The cert is created out-of-band (see customDomainCertificateId).
        // The binding is only emitted when both custom-domain settings are present.
        customDomains: enableCustomDomainBinding ? [
          {
            name: customDomainName
            bindingType: 'SniEnabled'
            certificateId: customDomainCertificateId
          }
        ] : null
      }
      registries: [
        {
          server: acrLoginServer
          identity: pullIdentityId
        }
      ]
      secrets: concat([
        {
          name: 'github-token'
          value: gitHubToken
        }
        {
          name: 'admin-key'
          value: adminKey
        }
      ],
      // Only declared when supplied: an empty secret value is rejected by Container Apps,
      // so the App credentials must be absent rather than blank when unconfigured.
      empty(gitHubAppPrivateKey) ? [] : [
        {
          name: 'github-app-private-key'
          value: gitHubAppPrivateKey
        }
      ])
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          // CORS origins are appended as Cors__AllowedOrigins__N entries. They live
          // in the template (not set out-of-band) so an incremental deployment can't
          // strip them — the env array is fully managed here.
          env: concat([
            {
              name: 'GitHub__Owner'
              value: gitHubOwner
            }
            {
              name: 'GitHub__Token'
              secretRef: 'github-token'
            }
            {
              name: 'Admin__AdminKey'
              secretRef: 'admin-key'
            }
            {
              name: 'Dashboard__RefreshSeconds'
              value: string(refreshSeconds)
            }
            {
              name: 'ReviewSignals__Reviewers__0__Name'
              value: 'CodeRabbit'
            }
            {
              name: 'ReviewSignals__Reviewers__0__BotLogin'
              value: 'coderabbitai'
            }
            {
              name: 'ReviewSignals__Reviewers__0__RequiredLabel'
              value: 'review-high'
            }
            {
              name: 'ReviewSignals__Reviewers__1__Name'
              value: 'Gitar'
            }
            {
              name: 'ReviewSignals__Reviewers__1__BotLogin'
              value: 'gitar-bot'
            }
            {
              name: 'ReviewSignals__Reviewers__2__Name'
              value: 'CodeQL'
            }
            {
              name: 'ReviewSignals__Reviewers__2__Source'
              value: 'CodeScanning'
            }
          ], map(range(0, length(corsAllowedOrigins)), i => {
            name: 'Cors__AllowedOrigins__${i}'
            value: corsAllowedOrigins[i]
          }),
          // App credentials only when both halves are supplied. Half-configured must look
          // UNCONFIGURED to the app, which then falls back to the PAT rather than starting
          // up and failing every request with an unsigned JWT.
          empty(gitHubAppId) || empty(gitHubAppPrivateKey) ? [] : [
            {
              name: 'GitHubApp__AppId'
              value: gitHubAppId
            }
            {
              name: 'GitHubApp__PrivateKeyPem'
              secretRef: 'github-app-private-key'
            }
          ])
        }
      ]
      // Single fixed replica: the in-process refresh worker must stay running,
      // and the snapshot is held in-container (no scale-out coordination).
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
