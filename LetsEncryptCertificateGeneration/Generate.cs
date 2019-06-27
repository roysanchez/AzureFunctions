using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace CertificateGenerator
{
    public static class Generate
    {
        static KeyVaultClient GetClient()
            => new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(new AzureServiceTokenProvider().KeyVaultTokenCallback));

        [FunctionName("Generate")]
        public static async Task Run([TimerTrigger("0 0 4 */7 * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            var KEY_VAULT_IDENTIFIER = Environment.GetEnvironmentVariable("KeyVault_Identifier");
            var cert_name = Environment.GetEnvironmentVariable("Certificate_Name");
            var CERTIFICATE_IDENTIFIER = $"{KEY_VAULT_IDENTIFIER}/certificates/{cert_name}";

            var allTasks = new List<Task>();
            var client = GetClient();
            CertificateBundle certificate = null;

            try
            {
                certificate = await client.GetCertificateAsync(CERTIFICATE_IDENTIFIER);
            }
            catch (KeyVaultErrorException ex)
            {
                if (ex.Body.Error.Code == "CertificateNotFound")
                {
                    log.LogInformation($"The certificate {cert_name} doesn't exists, will proceed to create a new one");
                }
                else
                {
                    throw;
                }
            }

            if (certificate == null || (!certificate.Attributes.Expires.HasValue || certificate.Attributes.Expires < DateTime.Now.AddDays(14)))
            {
                var storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
                var serviceClient = storageAccount.CreateCloudBlobClient();

                var container = serviceClient.GetContainerReference(Environment.GetEnvironmentVariable("Container_Name"));
                await container.CreateIfNotExistsAsync();
                var account_name = Environment.GetEnvironmentVariable("ACME_Account");

                var account_ref = container.GetBlockBlobReference(account_name + ".pem");
                AcmeContext acme;
                if (await account_ref.ExistsAsync())
                {
                    log.LogInformation("The account exists, reading from pem.");
                    var key = KeyFactory.FromPem(await account_ref.DownloadTextAsync());
                    acme = GetAcmeContext(log, key);
                    await acme.Account();
                }
                else
                {
                    log.LogInformation($"The account doesn't exists, creating a new one for {account_name}");
                    acme = GetAcmeContext(log);
                    await acme.NewAccount(account_name, termsOfServiceAgreed: true);

                    allTasks.Add(account_ref.UploadTextAsync(acme.AccountKey.ToPem()));
                }

                var domains = Environment.GetEnvironmentVariable("Domains");
                log.LogInformation($"Creating order for domains: {domains}");
                var order = await acme.NewOrder(domains.Split(","));

                log.LogInformation("Requesting authorization");
                var authz = (await order.Authorizations()).First();

                log.LogInformation("Looking for http challenge");
                var httpChallenge = await authz.Http();

                log.LogInformation($"The challenge key: {httpChallenge.Token}");
                log.LogInformation($"The challenge value: {httpChallenge.KeyAuthz}");

                log.LogInformation("Writing to blob storage");
                var challenge_ref = container.GetBlockBlobReference($"challenge/{httpChallenge.Token}");
                await challenge_ref.UploadTextAsync(httpChallenge.KeyAuthz);

                log.LogInformation("Validating the challenges");
                await httpChallenge.Validate();

                log.LogInformation("Challenge successful");
                log.LogInformation("Looking for private key");

                var privateKey_Ref = container.GetBlockBlobReference(Environment.GetEnvironmentVariable("PrivateKeyName"));
                IKey privateKey;

                if (await privateKey_Ref.ExistsAsync())
                {
                    log.LogInformation("Private key exists, reading from pem file");
                    privateKey = KeyFactory.FromPem(await privateKey_Ref.DownloadTextAsync());
                }
                else
                {
                    log.LogInformation("Private key doesn't exists, creating a new one");
                    privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);

                    log.LogInformation("Uploading pem to storage account");
                    allTasks.Add(privateKey_Ref.UploadTextAsync(privateKey.ToPem()));
                }

                log.LogInformation("Generating certificate");
                var cert = await order.Generate(new CsrInfo
                {
                    CountryName = "DO",
                    State = "Santo Domingo",
                    Locality = "Distrito Nacional",
                    Organization = "Roy Sanchez",
                    OrganizationUnit = "Tecnologia",
                    CommonName = Environment.GetEnvironmentVariable("ACME_CommonName")
                }, privateKey);
                log.LogInformation("Generation successful");

                var pfxBuilder = cert.ToPfx(privateKey);
                var blob = container.GetBlockBlobReference(cert_name + ".pfk");

                log.LogInformation("Building pfx file");
                var arr = pfxBuilder.Build(cert_name, string.Empty);

                log.LogInformation("Uploading to blob storage");
                allTasks.Add(blob.UploadFromByteArrayAsync(arr, 0, arr.Length));

                log.LogInformation($"Uploading to key vault, with the name: {cert_name}");
                allTasks.Add(client.ImportCertificateAsync(KEY_VAULT_IDENTIFIER, cert_name, Convert.ToBase64String(arr)));

                await Task.WhenAll(allTasks);
                log.LogInformation("Operation finished");
            }
            else
            {
                log.LogInformation("The certificate is still valid");
            }
        }

        private static AcmeContext GetAcmeContext(ILogger log, IKey key = null)
        {
            var environment = Environment.GetEnvironmentVariable("Environment");

            if (string.Equals(environment, "Production", StringComparison.InvariantCultureIgnoreCase))
            {
                log.LogInformation("Running on production");
                return new AcmeContext(WellKnownServers.LetsEncryptV2, key);
            }
            else
            {
                log.LogInformation("Running on staging");
                return new AcmeContext(WellKnownServers.LetsEncryptStagingV2, key);
            }
        }
    }
}
