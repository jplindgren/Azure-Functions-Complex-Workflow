using System;

namespace CreditApproval.Domain
{
    public interface ICreditOperationActions
    {
        void New(CreditOperation operation);
        void Reject(string reason);
        void Approve(int score);
        void Expire();
        void Complete();
    }
}