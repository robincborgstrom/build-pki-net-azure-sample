﻿using System;
using System.Threading.Tasks;
using Microsoft.Azure.Management.AppService.Fluent;
using Microsoft.Azure.Management.AppService.Fluent.Models;
using Microsoft.Azure.Management.KeyVault.Fluent;
using Microsoft.Azure.Management.KeyVault.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Storage.Fluent;
using Microsoft.Rest;
using OperatingSystem = Microsoft.Azure.Management.AppService.Fluent.OperatingSystem;

namespace BuildPkiSample.Setup
{
    internal class ResourceManagementHelper
    {
        private readonly Configuration _configuration;
        private readonly AzureCredentials _azureCredentials;
        private readonly string _currentUserObjectId;

        public ResourceManagementHelper(Configuration configuration, AcquireTokenResult acquireTokenResult)
        {
            _configuration = configuration;
            _azureCredentials = new AzureCredentials(
                new TokenCredentials(acquireTokenResult.AccessToken),
                new TokenCredentials(acquireTokenResult.AccessToken),
                configuration.TenantId,
                AzureEnvironment.AzureGlobalCloud);
            _currentUserObjectId = acquireTokenResult.UserObjectId;
        }

        public async Task CreateAzureResourcesAsync(bool alwaysCreate)
        {
            if (!alwaysCreate && await ResourceGroupExistsAsync())
            {
                Console.WriteLine($"Resource group '{ResourceGroupName}' already exists. Skipping resource creation.");
                return;
            }

            var resourceGroup = await CreateResourceGroupAsync();
            var (newCerts, renewalCerts) = await CreateFunctionAppsAsync(resourceGroup);
            await CreateVaultAsync(resourceGroup, newCerts.SystemAssignedManagedServiceIdentityPrincipalId, renewalCerts.SystemAssignedManagedServiceIdentityPrincipalId);
        }

        private string ResourceGroupName => _configuration.ResourceNamePrefix;

        private Task<bool> ResourceGroupExistsAsync()
        {
            return ResourceManager
                .Authenticate(_azureCredentials)
                .WithSubscription(_configuration.SubscriptionId)
                .ResourceGroups
                .ContainAsync(ResourceGroupName);
        }

        private async Task<IResourceGroup> CreateResourceGroupAsync()
        {
            var resourceGroup = await ResourceManager
                .Authenticate(_azureCredentials)
                .WithSubscription(_configuration.SubscriptionId)
                .ResourceGroups
                .Define(ResourceGroupName)
                .WithRegion(_configuration.RegionName)
                .CreateAsync();
            Console.WriteLine($"Successfully created or updated resource group '{resourceGroup.Name}' in region '{resourceGroup.RegionName}'");
            return resourceGroup;
        }
        
        private async Task<(IFunctionApp newCerts, IFunctionApp renewalCerts)> CreateFunctionAppsAsync(IResourceGroup resourceGroup)
        {
            var storageAccount = await StorageManager
                .Authenticate(_azureCredentials, _configuration.SubscriptionId)
                .StorageAccounts
                .Define(_configuration.ResourceNamePrefix.ToLowerInvariant() + "storage")
                .WithRegion(_configuration.RegionName)
                .WithExistingResourceGroup(resourceGroup)
                .WithSku(StorageAccountSkuType.Standard_LRS)
                .CreateAsync();
            
            var appServiceManager = AppServiceManager.Authenticate(_azureCredentials, _configuration.SubscriptionId);
            var appServicePlan = await appServiceManager
                .AppServicePlans
                .Define(_configuration.ResourceNamePrefix + "Plan")
                .WithRegion(_configuration.RegionName)
                .WithExistingResourceGroup(resourceGroup)
                .WithPricingTier(PricingTier.FromSkuDescription(new SkuDescription("Y1", "Dynamic", "Y1", "Y", 0)))
                .WithOperatingSystem(OperatingSystem.Windows)
                .CreateAsync();
            Console.WriteLine($"Successfully created or updated app service plan '{appServicePlan.Name}'");

            var newCerts = await appServiceManager
                .FunctionApps
                .Define(_configuration.ResourceNamePrefix + "NewCerts")
                .WithExistingAppServicePlan(appServicePlan)
                .WithExistingResourceGroup(resourceGroup)
                .WithExistingStorageAccount(storageAccount)
                .WithSystemAssignedManagedServiceIdentity()
                .CreateAsync();
            Console.WriteLine($"Successfully created or updated function app '{newCerts.Name}'. Note that user authentication is not setup from code and needs to be set manually.");

            var renewCerts = await appServiceManager
                .FunctionApps
                .Define(_configuration.ResourceNamePrefix + "RenewCerts")
                .WithExistingAppServicePlan(appServicePlan)
                .WithExistingResourceGroup(resourceGroup)
                .WithExistingStorageAccount(storageAccount)
                .WithSystemAssignedManagedServiceIdentity()
                .WithClientCertEnabled(true)
                .CreateAsync();
            Console.WriteLine($"Successfully created or updated function app '{newCerts.Name}'");

            return (newCerts, renewCerts);
        }

        private async Task CreateVaultAsync(IResourceGroup resourceGroup, params string[] certificateAuthorityPrincipalIds)
        {
            var vaultDefinition = KeyVaultManager
                .Authenticate(_azureCredentials, _configuration.SubscriptionId)
                .Vaults
                .Define(_configuration.ResourceNamePrefix + "Vault")
                .WithRegion(_configuration.RegionName)
                .WithExistingResourceGroup(resourceGroup)
                .DefineAccessPolicy()
                .ForObjectId(_currentUserObjectId)
                .AllowCertificatePermissions(CertificatePermissions.List, CertificatePermissions.Get, 
                    CertificatePermissions.Create, CertificatePermissions.Update, CertificatePermissions.Delete)
                .AllowKeyPermissions(KeyPermissions.Sign)  // This is required for local testing & debugging. Would remove for production.
                .Attach();

            foreach (string principalId in certificateAuthorityPrincipalIds)
            {
                vaultDefinition = vaultDefinition
                    .DefineAccessPolicy()
                    .ForObjectId(principalId)
                    .AllowKeyPermissions(KeyPermissions.Sign)
                    .AllowCertificatePermissions(CertificatePermissions.Get)
                    .Attach();
            }
            
            var vault = await vaultDefinition.CreateAsync();
            Console.WriteLine($"Successfully created or updated key vault '{vault.Name}'");
        }
    }
}