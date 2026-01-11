using DriverLedger.Domain.Statements;

namespace DriverLedger.Application.Statements.Reconciliation
{
/// <summary>
/// Runs statement reconciliation workflows (MVP: Uber monthly -> yearly).
/// Produces a persisted <see cref="ReconciliationRun"/> with per-metric variances.
/// </summary>
public interface IStatementReconciliationService
{
    /// <summary>
    /// Reconciles Uber monthly statements for the given year against the Uber yearly statement.
    /// It aggregates monthly totals and compares them to yearly totals, then persists a reconciliation run.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="year">Tax year (e.g., 2024).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted reconciliation run, typically with variances populated by the implementation.</returns>
    Task<ReconciliationRun> ReconcileUberYearAsync(Guid tenantId, int year, CancellationToken ct = default);
}
}
