using System;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace CreditApproval.Domain
{
    public class CreditOperation : TableEntity
    {
        public string Account { get; set; }
        public string StatusAsString { get; set; } = CreditOperationStatus.InAnalysis.ToString();
        public string RejectedReason { get; set; }
        public double? Score { get; set; }
        public decimal? CambioRate { get; private set; }

        [IgnoreProperty]
        public CreditOperationStatus Status
        {
            get => (CreditOperationStatus)Enum.Parse(typeof(CreditOperationStatus), StatusAsString);
            set => StatusAsString = value.ToString();
        }

        [IgnoreProperty]
        public string Identifier
        {
            get => $"{Account}-{RowKey}";
        }

        public void Approve(double score, decimal cambioRate)
        {
            this.Status = CreditOperationStatus.Approved;
            this.Score = score;
            this.CambioRate = cambioRate;
        }

        public void Complete()
        {
            this.Status = CreditOperationStatus.Completed;
        }

        public void Expire()
        {
            this.Status = CreditOperationStatus.Expired;
        }

        public void New(CreditOperation operation)
        {
            this.Account = operation.Account;
            this.Status = CreditOperationStatus.InAnalysis;
        }

        public void Reject(string reason)
        {
            this.Status = CreditOperationStatus.Rejected;
            this.RejectedReason = reason;
        }

        public void Confirm()
        {
            this.Status = CreditOperationStatus.Confirmed;
        }

        public override bool Equals(object obj)
        {
            var creditOperation = obj as CreditOperation;
            if (creditOperation == null)
                return false;
            return creditOperation.RowKey == this.RowKey;
        }

        public override int GetHashCode() => this.RowKey.GetHashCode();

        [FunctionName(nameof(CreditOperation))]
        public static Task Run([EntityTrigger] IDurableEntityContext ctx)
            => ctx.DispatchAsync<CreditOperation>();
    }

    public enum CreditOperationStatus
    {
        InAnalysis,
        Rejected,
        Approved,
        Confirmed,
        Expired,
        Completed
    }
}
