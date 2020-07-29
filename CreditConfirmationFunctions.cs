using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreditApproval.Domain;
using CreditApproval.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CreditApproval.Functions
{
    public class CreditConfirmationFunctions
    {
        public const string CONFIRM_TASK = "ConfirmCredit";

        [FunctionName(nameof(CreditConfirmationWorkflow))]
        public static async Task CreditConfirmationWorkflow(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger logger)
        {
            var operation = context.GetInput<CreditOperation>();
            logger.LogInformation("Start of customer credit confirmation workflow for operation", operation.Identifier);

            await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                $"Account {operation.Account} now has {Global.ExpirationMinutes} minutes to confirm the {operation.Identifier} operation.");

            using (var timeoutCts = new CancellationTokenSource())
            {
                var dueTime = context.CurrentUtcDateTime
                    .Add(TimeSpan.FromMinutes(Global.ExpirationMinutes));

                var confirmationEvent = context.WaitForExternalEvent<bool>(CONFIRM_TASK);

                logger.LogInformation($"Now: {context.CurrentUtcDateTime} Timeout: {dueTime}");

                var durableTimeout = context.CreateTimer(dueTime, timeoutCts.Token);

                var confirmed = await Task.WhenAny(confirmationEvent, durableTimeout);

                if (confirmed == confirmationEvent && confirmationEvent.Result)
                {
                    timeoutCts.Cancel();
                    logger.LogInformation("Account {account} confirmed the operation {operation}.", operation.Account, operation.Identifier);
                    await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                        $"Account {operation.Account} confirmed the operation {operation.Identifier}!");
                    // await context.CallActivityAsync(nameof(Global.StartNewWorkflow),
                    //     ((nameof(NewUserSequentialFunctions.RunUserSequentialWorkflow),
                    //     username)));
                }
                else
                {
                    logger.LogInformation("Operation {operation} confirmation timed out.", operation.Identifier);
                    await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                        $"Operation {operation.Identifier} confirmation timed out.");
                    await context.CallActivityAsync(nameof(MakeCreditOperationExpired), operation);
                }
            }

            await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                $"Account {operation.Account} confirmation workflow complete for operation {operation.Identifier}.");
        }

        [FunctionName(nameof(ConfirmOperation))]
        public static async Task<IActionResult> ConfirmOperation(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            [Table(nameof(CreditOperation))] CloudTable table,
            [DurableClient] IDurableClient durableClient,
            ILogger log)
        {
            log.LogInformation("ConfirmOperation called.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string operation = data?.operation;

            if (string.IsNullOrWhiteSpace(operation))
            {
                await console.AddAsync("An attempt to confirm a operation without an identifier was made.");
                return new BadRequestObjectResult("Operation Identifier is required.");
            }

            var queryResult = await table.ExecuteAsync(TableOperation.Retrieve<CreditOperation>(operation, operation));
            var creditOperation = (CreditOperation)queryResult.Result;
            if (creditOperation == null)
            {
                await console.AddAsync($"Attempt to confirm missing operation {operation} failed.");
                return new BadRequestObjectResult("Operation does not exist.");
            }

            creditOperation.Confirm();
            await table.ExecuteAsync(TableOperation.Replace(creditOperation));

            log.LogInformation("Operation {operation} is valid, searching for confirmation workflow.", creditOperation.Identifier);

            var instance = await durableClient.FindJob(
                DateTime.UtcNow,
                nameof(CreditConfirmationWorkflow),
                creditOperation);

            if (instance == null)
            {
                log.LogInformation("Confirmation workflow not found for operation {operation}.", creditOperation.Identifier);
                return new NotFoundResult();
            }
            log.LogInformation("Confirmation workflow with id {instanceId} found for operation {operation}.", instance.InstanceId, creditOperation.Identifier);
            await durableClient.RaiseEventAsync(instance.InstanceId, CONFIRM_TASK, true);
            return new OkResult();
        }

        [FunctionName(nameof(MakeCreditOperationExpired))]
        public static async Task MakeCreditOperationExpired(
            [ActivityTrigger] CreditOperation operation,
            [Table(nameof(CreditOperation))] CloudTable table,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger logger
        )
        {
            logger.LogInformation("Expiring credit operation: {operation}", operation.Identifier);

            var queryResult = await table.ExecuteAsync(TableOperation.Retrieve<CreditOperation>(operation.PartitionKey, operation.RowKey));
            var creditOperation = (CreditOperation)queryResult.Result;
            if (creditOperation == null)
                throw new Exception($"Credit Operation: {operation.Identifier} not found!");

            creditOperation.Expire();
            await table.ExecuteAsync(TableOperation.Replace(creditOperation));

            await console.AddAsync($"Credit operation: {operation.Identifier} expired");
            logger.LogInformation("Credit operation: {operation} expired!", operation.Identifier);
        }
    }
}