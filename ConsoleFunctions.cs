using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace CreditApproval
{
    public static class ConsoleFunctions
    {
        [FunctionName(nameof(AddToQueue))]
        public static async Task AddToQueue(
            [ActivityTrigger] string message,
            [Queue(Global.QUEUE)] IAsyncCollector<string> console)
        {
            await console.AddAsync(message);
        }
    }
}
