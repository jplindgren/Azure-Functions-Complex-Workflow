using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using CreditApproval.Domain;

namespace CreditApproval.Functions
{
    public static class CreditAnalysis
    {
        [FunctionName("CreditAnalysis")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
            [DurableClient] IDurableClient starter,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger log)
        {
            log.LogInformation("Credit Analysis Stated");

            var data = await ReadBodyAsJson<CreditAnalysisModel>(req);

            var isValid = ValidateEntity<CreditAnalysisModel>(data, out List<ValidationResult> results);
            if (!isValid)
            {
                await console.AddAsync("An attempt to start an credit analysis with incorrect parameters was made.");
                return new BadRequestObjectResult(results);
            }

            //check if credit analysis already exists for account;

            Guid operationId = Guid.NewGuid();

            await starter.StartNewAsync<CreditOperation>(nameof(StartCreditAnalysis.RunCreditAnalysisWorkflow), operationId.ToString(), new CreditOperation()
            {
                Account = data.Account,
                Status = CreditOperationStatus.InAnalysis,
                PartitionKey = operationId.ToString(),
                RowKey = operationId.ToString(),
            });
            log.LogInformation("Started new parallel workflow for operation {operationId}", operationId);

            // log.LogInformation("Starting new monitor workflow for operation: {operationId}", operationId.ToString());
            // await starter.StartNewAsync<Guid>(nameof(CreditOperationMonitor.MonitorCreditOperationWorkflow), operationId.ToString(), operationId);
            // log.LogInformation("Started new monitor workflow for operation: {operationId}", operationId.ToString());

            return new OkObjectResult(new
            {
                operationId
            });
        }

        private static bool ValidateEntity<T>(T data, out List<ValidationResult> results)
        {
            results = new List<ValidationResult>();

            var validationContext = new ValidationContext(data);
            return Validator.TryValidateObject(data, validationContext, results, true);
        }

        private static async Task<T> ReadBodyAsJson<T>(HttpRequest request) where T : new()
        {
            string body = await new StreamReader(request.Body).ReadToEndAsync();
            return JsonConvert.DeserializeObject<T>(body);
        }
    }

    public class CreditAnalysisModel
    {
        public string Name { get; set; }
        public string Identifier { get; set; }
        public string Account { get; set; }
    }
}
