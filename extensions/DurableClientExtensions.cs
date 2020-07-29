using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace CreditApproval.Extensions
{
    public static class DurableClientExtensions
    {
        public const int ExpirationMinutes = 2;
        public const int DefaultSpanMinutes = 60;
        //public const int MonitorTimeoutHours = 1;
        public static async Task<DurableOrchestrationStatus> FindJob<T>(
            this IDurableClient client,
            DateTime time,
            string workflowName,
            T creditOperation,
            bool runningOnly = true,
            bool confirmation = true)
        {
            var filter = runningOnly ?
                new List<OrchestrationRuntimeStatus> { OrchestrationRuntimeStatus.Running }
                : new List<OrchestrationRuntimeStatus>();
            var offset = TimeSpan.FromMinutes(confirmation ?
                ExpirationMinutes : DefaultSpanMinutes);

            var condition = new OrchestrationStatusQueryCondition()
            {
                RuntimeStatus = filter,
                CreatedTimeFrom = time.Subtract(offset),
                CreatedTimeTo = time.Add(offset)
            };

            var instances = await client.ListInstancesAsync(condition, new System.Threading.CancellationToken());
            foreach (var instance in instances.DurableOrchestrationState)
            {
                if (instance.Input.ToObject<T>().Equals(creditOperation) &&
                    instance.Name == workflowName)
                    return instance;
            }
            return null;
        }
    }
}