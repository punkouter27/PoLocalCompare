targetScope = 'resourceGroup'

// PoLocalCompare.RenderService on Azure Container Apps: the vision judge's headless-Chromium
// screenshots. The Api runs on a Free (F1) App Service, which cannot launch a browser, so it posts
// each page here. Scales to zero between duels, so it normally fits inside the Container Apps
// monthly free grant. Applied by scripts/deploy-renderer.ps1, not by main.bicep or CI.

param location string = resourceGroup().location

@description('Container image, e.g. ghcr.io/<owner>/polocalcompare-render:<sha>. Must be pullable anonymously.')
param image string

@secure()
@description('Shared key the Api sends as X-Render-Key (Key Vault: PoLocalCompare--Rendering--ApiKey).')
param renderApiKey string

@description('Existing Log Analytics workspace for container logs. Empty = no log destination.')
param logAnalyticsWorkspaceName string = ''

@description('Resource group of that workspace.')
param logAnalyticsResourceGroup string = 'PoShared'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = if (!empty(logAnalyticsWorkspaceName)) {
  name: logAnalyticsWorkspaceName
  scope: resourceGroup(logAnalyticsResourceGroup)
}

// Consumption-only environment: billed per second of replica activity, nothing while scaled to zero.
resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'cae-polocalcompare'
  location: location
  properties: {
    appLogsConfiguration: empty(logAnalyticsWorkspaceName)
      ? null
      : {
          destination: 'log-analytics'
          logAnalyticsConfiguration: {
            customerId: logAnalytics!.properties.customerId
            sharedKey: logAnalytics!.listKeys().primarySharedKey
          }
        }
  }
}

resource renderer 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-polocalcompare-render'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: [
        {
          name: 'render-api-key'
          value: renderApiKey
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'render'
          image: image
          // Chromium needs the memory: two concurrent renders plus the browser sit near 1 GiB.
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'Render__ApiKey'
              secretRef: 'render-api-key'
            }
          ]
          probes: [
            {
              // /health launches the browser; a replica whose Chromium will not start is restarted.
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 2
              failureThreshold: 30
            }
          ]
        }
      ]
      scale: {
        // Zero between duels is what keeps this free. One replica is enough: the service
        // queues renders four at a time, and a duel needs two.
        minReplicas: 0
        maxReplicas: 1
        cooldownPeriod: 120
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

output renderServiceUrl string = 'https://${renderer.properties.configuration.ingress.fqdn}'
