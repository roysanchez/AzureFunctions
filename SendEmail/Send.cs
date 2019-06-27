using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.Storage;
using SendGrid.Helpers.Mail;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using System.Collections.Concurrent;

namespace SendEmail
{
    public static class Send
    {
        [FunctionName("SendEmail")]
        public static async Task Run(
         [QueueTrigger("emails")] OutgoingEmail email,
         [SendGrid] IAsyncCollector<SendGridMessage> messageCollector, ILogger log)
        {
            try
            {
                if (email is null)
                {
                    throw new ArgumentNullException(nameof(email));
                }
                else if(email.Tos is null || !email.Tos.Any())
                {
                    throw new ArgumentException("There must be at least one recipient.", nameof(email.Tos));
                }
                else if(string.IsNullOrWhiteSpace(email.Body))
                {
                    throw new ArgumentNullException("There must be a body for the email.",nameof(email.Body));
                }

                log.LogInformation("Starting email process");

                var message = new SendGridMessage();

                message.SetFrom(new EmailAddress(Environment.GetEnvironmentVariable("FromEmail"), Environment.GetEnvironmentVariable("FromName")));
                message.SetSubject(email.Subject);
                message.HtmlContent = email.Body;

                var hardcodedToEmail = Environment.GetEnvironmentVariable("ToEmail");
                if (string.IsNullOrWhiteSpace(hardcodedToEmail))
                {
                    foreach (var toEmail in email.Tos)
                    {
                        log.LogInformation("email: {toEmail}", toEmail);
                        message.AddTo(toEmail);
                    }
                }
                else
                {
                    log.LogInformation("harcoded email: {email}", hardcodedToEmail);
                    message.AddTo(hardcodedToEmail);
                }

                if (email.Attachments != null && email.Attachments.Any())
                {
                    log.LogInformation("Have {count} attachment(s)", email.Attachments.Count);
                    var storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));

                    var client = storageAccount.CreateCloudBlobClient();
                    var container = client.GetContainerReference(Environment.GetEnvironmentVariable("Container_Name"));
                    await container.CreateIfNotExistsAsync();

                    foreach (var file in email.Attachments)
                    {
                        var dirRef = container.GetDirectoryReference(file);
                        var isDirectory = dirRef.ListBlobs().Count() > 0;

                        if (isDirectory)
                        {
                            log.LogInformation("Processing folder: {file}", file);
                            var bag = new ConcurrentBag<Attachment>();
                            var taskList = new List<Task>();

                            foreach (CloudBlob b in dirRef.ListBlobs(useFlatBlobListing: true))
                            {
                                taskList.Add(Task.Run(async () => bag.Add(await DownloadBlob(b, log))));
                            }

                            await Task.WhenAll(taskList);

                            if(bag.Any())
                            {
                                message.AddAttachments(bag.ToList());
                            }
                            else
                            {
                                log.LogWarning("There are no files in the {folder} folder", file);
                            }
                        }
                        else
                        {
                            log.LogInformation("Processing file: {file}", file);
                            var blobRef = container.GetBlobReference(file);
                            if (await blobRef.ExistsAsync())
                            {
                                message.Attachments.Add(await DownloadBlob(blobRef, log));
                            }
                            else
                            {
                                throw new ArgumentException($"The file {file} does not exists", nameof(file));
                            }
                        }
                    }
                }

                log.LogDebug("Sending an email with the subject: {subject}", email.Subject);

                await messageCollector.AddAsync(message);

                log.LogInformation("Finished sending email");
            }
            catch(Exception ex)
            {
                log.LogError(ex, ex.Message);
                throw ex;
            }
        }

        public static async Task<Attachment> DownloadBlob(CloudBlob blob, ILogger log)
        {
            var name = Path.GetFileName(blob.Name);

            log.LogInformation("Adding attachment: blob({blobName}) with name: {fileName}", blob.Name, name);

            var memoryStream = new MemoryStream();
            await blob.DownloadToStreamAsync(memoryStream);
            var str = Convert.ToBase64String(memoryStream.ToArray());
            return new Attachment
            {
                Content = str,
                Filename = Path.GetFileName(blob.Name),
                Type = blob.Properties.ContentType
            };
        }

        public class OutgoingEmail
        {
            public List<string> Tos { get; set; }
            public string Subject { get; set; }
            public string Body { get; set; }
            public List<string> Attachments { get; set; }
        }
    }
}
