using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System.Threading;
using CreditApproval.Domain;
using System;
using Microsoft.Azure.Cosmos.Table;

namespace CreditApproval.Monitor
{
    public static class CreditOperationMonitor
    {
        public const int CheckIntervalSeconds = 20;

        [FunctionName(nameof(MonitorCreditOperationWorkflow))]
        public static async Task MonitorCreditOperationWorkflow(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            var operationId = context.GetInput<Guid>();

            log.LogInformation("Start of operation monitor workflow for {operation}", operationId);

            var expiryTime = context.CurrentUtcDateTime.AddHours(Global.MonitorTimeoutHours);

            await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                    $"Operation: {operationId} is being monitored every {CheckIntervalSeconds} seconds.");

            var timeout = false;
            while (context.CurrentUtcDateTime < expiryTime)
            {
                timeout = false;
                var done = await context.CallActivityAsync<bool>(nameof(MonitorCreditOperation), operationId);

                if (done) break;

                var nextCheck = context.CurrentUtcDateTime.AddSeconds(CheckIntervalSeconds);
                await context.CreateTimer(nextCheck, CancellationToken.None);
                timeout = true;
            }
            if (timeout)
            {
                await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                    $"Operation: {operationId} monitoring has timed out.");
            }
            await context.CallActivityAsync(nameof(ConsoleFunctions.AddToQueue),
                    $"Operation: {operationId} monitoring is done.");
        }

        [FunctionName(nameof(MonitorCreditOperation))]
        public static async Task<bool> MonitorCreditOperation(
           [ActivityTrigger] Guid operationId,
           [Table(nameof(CreditOperation))] CloudTable creditOperationTable,
           [Queue(Global.QUEUE)] IAsyncCollector<string> console,
           ILogger logger)
        {
            logger.LogInformation("Starting new **MonitorCreditOperation** function for operation: {operationId}", operationId.ToString());
            var queryResult = await creditOperationTable.ExecuteAsync(TableOperation.Retrieve(operationId.ToString(), operationId.ToString()));
            var creditOperation = (CreditOperation)queryResult.Result;
            if (creditOperation == null)
            {
                throw new Exception($"Operation: {operationId} not found!");
            }

            Action logStatus = async () =>
            {
                logger.LogInformation("Operation: {operationId} for account: {account} is {status}", creditOperation.Identifier, creditOperation.Account, creditOperation.Status.ToString());
                await console.AddAsync($"Operation: {creditOperation.Identifier} for account: {creditOperation.Account} is {creditOperation.Status.ToString()}");
            };

            var done = false;
            // switch (creditOperation.Status)
            // {
            //     case CreditOperationStatus.Rejected:
            //     case CreditOperationStatus.Completed:
            //     case CreditOperationStatus.Expired:
            //         done = true;
            //         break;
            //     default:
            //         done = false;
            //         break;
            // }

            return done;
        }
    }
}