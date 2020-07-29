using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using CreditApproval.Domain;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

namespace CreditApproval.Functions
{
    public class StartCreditAnalysis
    {
        private readonly HttpClient _client;
        public StartCreditAnalysis(IHttpClientFactory httpClientFactory)
        {
            _client = httpClientFactory.CreateClient();
        }

        [FunctionName(nameof(RunCreditAnalysisWorkflow))]
        public static async Task RunCreditAnalysisWorkflow(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger logger)
        {
            var operation = context.GetInput<CreditOperation>();
            logger.LogInformation("Start of credit analysis parallel workflow for {operation}", operation.Identifier);

            await context.CallActivityAsync(nameof(CreateCreditOperation), operation);

            var checkInternalScore = context.CallActivityAsync<double>(nameof(CheckInternalScore), operation);
            var checkExternalBackground = context.CallActivityAsync<ExternalBackground>(nameof(CheckExternalBackground), operation);
            var getDollar = context.CallActivityAsync<decimal>(nameof(GetDollarRate), operation);

            var parallelTasks = new List<Task>
            {
                checkInternalScore,
                checkExternalBackground,
                getDollar
            };

            await Task.WhenAll(parallelTasks);

            if (checkInternalScore.Result >= 0.3 && checkExternalBackground.Result >= ExternalBackground.SomeProblems)
            {
                logger.LogInformation("Starting approved sub-orchestration {workflow} for {operation}",
                        nameof(CreditConfirmationFunctions.CreditConfirmationWorkflow), operation.Identifier);
                await context.CallActivityAsync(nameof(ApproveCredit), (getDollar.Result, checkInternalScore.Result, operation.PartitionKey, operation.RowKey, operation.Identifier));
                await context.CallSubOrchestratorAsync(nameof(CreditConfirmationFunctions.CreditConfirmationWorkflow), operation);
            }
            else
            {
                await context.CallActivityAsync(nameof(RejectCredit), (checkInternalScore.Result, checkExternalBackground.Result, operation.PartitionKey, operation.RowKey, operation.Identifier));
            }


            logger.LogInformation("End of RunCreditAnalysisWorkflow.");
        }

        [FunctionName(nameof(ApproveCredit))]
        public static async Task<bool> ApproveCredit(
            [ActivityTrigger] (decimal cambioRate, double score, string partitionKey, string rowKey, string identifier) payload,
            [Table(nameof(CreditOperation))] CloudTable table,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger logger)
        {
            logger.LogInformation("Approving credit for operation: {operation} score: {score} cambio: {cambio}", payload.identifier, payload.score, payload.cambioRate);

            var queryResult = await table.ExecuteAsync(TableOperation.Retrieve<CreditOperation>(payload.partitionKey, payload.rowKey));
            var creditOperation = (CreditOperation)queryResult.Result;
            if (creditOperation == null)
                throw new Exception($"Credit Operation: {payload.identifier} not found!");

            creditOperation.Approve(payload.score, payload.cambioRate);
            await table.ExecuteAsync(TableOperation.Replace(creditOperation));

            await console.AddAsync($"Credit Approved for operation: {payload.identifier} score: {payload.score} cambio: {payload.cambioRate}");
            logger.LogInformation("Credit for operation: {operation} Approved!", payload.identifier, payload.score, payload.cambioRate);
            return true;
        }

        [FunctionName(nameof(RejectCredit))]
        public static async Task RejectCredit(
            [ActivityTrigger] (double score, ExternalBackground background, string partitionKey, string rowKey, string identifier) payload,
            [Table(nameof(CreditOperation))] CloudTable table,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger logger)
        {
            logger.LogInformation("Rejecting credit for operation {operation}");

            var queryResult = await table.ExecuteAsync(TableOperation.Retrieve<CreditOperation>(payload.partitionKey, payload.rowKey));
            var creditOperation = (CreditOperation)queryResult.Result;
            if (creditOperation == null)
                throw new Exception($"Credit Operation: {payload.identifier} not found!");

            string rejectedMessage = $"Credit rejected for operation: {payload.identifier} score: {payload.score} background check: {payload.background}";
            creditOperation.Reject(rejectedMessage);
            await table.ExecuteAsync(TableOperation.Replace(creditOperation));

            await console.AddAsync(rejectedMessage);
            logger.LogInformation(rejectedMessage);
        }

        [FunctionName(nameof(CreateCreditOperation))]
        public static async Task CreateCreditOperation(
            [ActivityTrigger] CreditOperation operation,
            [Table(nameof(CreditOperation))] CloudTable table,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger logger)
        {
            logger.LogInformation("Create credit operation: {identifier}", operation.Identifier);

            await table.ExecuteAsync(TableOperation.Insert(operation));
            await console.AddAsync($"Successfully created operation {operation.Identifier}");
            logger.LogInformation("Create operation {operation} successful", operation.Identifier);
        }

        [FunctionName(nameof(CheckInternalScore))]
        public static async Task<double> CheckInternalScore(
           [ActivityTrigger] CreditOperation operation,
           [Queue(Global.QUEUE)] IAsyncCollector<string> console,
           ILogger logger)
        {
            logger.LogInformation("Check internal score for operation: {identifier}", operation.Identifier);

            //Check internal score
            Random r = new Random(Guid.NewGuid().GetHashCode());
            var score = r.NextDouble();

            await console.AddAsync($"Successfully checked internal score for operation {operation.Identifier}. Score: {score}");
            logger.LogInformation("Successfully checked internal for operation {operation}. Score: {score}", operation.Identifier, score);

            return score;
        }

        [FunctionName(nameof(GetDollarRate))]
        public async Task<decimal> GetDollarRate(
            [ActivityTrigger] CreditOperation operation,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console,
            ILogger logger)
        {
            logger.LogInformation("Geting dollar rate for operation: {identifier}", operation.Identifier);

            //Check internal score
            var response = await _client.GetAsync("https://api.exchangeratesapi.io/latest?symbols=USD");
            if (response.IsSuccessStatusCode)
            {
                dynamic product = await response.Content.ReadAsAsync<dynamic>();

                await console.AddAsync($"Successfully got dollar rate for operation {operation.Identifier}. Dollar Rate: {(decimal)product.rates.USD}");
                logger.LogInformation("Successfully got dollar rate for operation {operation}. Dollar rate: {dollar}", operation.Identifier, (decimal)product.rates.USD);

                var result = (decimal)product.rates.USD;

                return result;
            }
            else
            {
                logger.LogError("Error on get dollar rate for operation {operation}. Status: {httpstatus}", operation.Identifier, response.StatusCode);
                return -1;
            }

        }

        [FunctionName(nameof(CheckExternalBackground))]
        public static async Task<ExternalBackground> CheckExternalBackground(
           [ActivityTrigger] CreditOperation operation,
           [Queue(Global.QUEUE)] IAsyncCollector<string> console,
           ILogger logger)
        {
            logger.LogInformation("Check external background for operation: {identifier}", operation.Identifier);

            // Would do a call to an external service in real life. Possible long operation
            Array values = Enum.GetValues(typeof(ExternalBackground));
            Random random = new Random();
            ExternalBackground randomBackGround = (ExternalBackground)values.GetValue(Math.Min(random.Next(values.Length) + 1, 3));

            await Task.Delay(1000);

            await console.AddAsync($"Successfully checked external background for operation {operation.Identifier}. Background: {randomBackGround.ToString()}");
            logger.LogInformation("Successfully checked external background for {operation}. Background: {background}", operation.Identifier, randomBackGround.ToString());

            return randomBackGround;
        }
    }
}