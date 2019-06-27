using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net;
using System.Text;

namespace CertificateGenerator
{
    public static class ReadChallengeFile
    {
        [FunctionName("ReadChallengeFile")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, Route = "acme-challenge/{challenge}")] HttpRequest req,
            [Blob("%Container_Name%/challenge/{challenge}", FileAccess.Read)] Stream blob,
            string challenge,
            ILogger log)

        {
            if (blob != null)
            {
                log.LogInformation($"Blob exists for challenge: {challenge}");
                var memory = new MemoryStream();
                await blob.CopyToAsync(memory);

                var value = Encoding.UTF8.GetString(memory.ToArray());
                log.LogInformation($"The challenge value: {value}");
                
                return new ContentResult
                {
                    Content = value
                };
            }
            else
            {
                var msg = $"The file: {challenge} does not exists.";
                log.LogWarning(msg);
                return new BadRequestObjectResult(msg);
            }
        }
    }
}
