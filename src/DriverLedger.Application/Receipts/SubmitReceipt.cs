using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Application.Receipts
{
    public sealed record SubmitReceiptResult(Guid ReceiptId, string Status);

    public interface IReceiptService
    {
        Task<SubmitReceiptResult> SubmitAsync(Guid receiptId, CancellationToken ct);
    }
}
