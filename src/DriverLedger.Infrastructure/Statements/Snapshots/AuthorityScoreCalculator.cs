
namespace DriverLedger.Infrastructure.Statements.Snapshots
{
    public static class AuthorityScoreCalculator
    {
        public static AuthorityScoreResult Compute(int totalLines, int evidencedLines)
        {
            if (totalLines <= 0)
            {
                return new AuthorityScoreResult(
                    EvidencePct: 0m,
                    EstimatedPct: 1m,
                    AuthorityScore: 0
                );
            }

            var evidencePct = Math.Clamp(
                (decimal)evidencedLines / totalLines,
                0m,
                1m
            );

            var estimatedPct = 1m - evidencePct;

            var authorityScore = (int)Math.Round(
                evidencePct * 100m,
                MidpointRounding.AwayFromZero
            );

            return new AuthorityScoreResult(
                EvidencePct: evidencePct,
                EstimatedPct: estimatedPct,
                AuthorityScore: authorityScore
            );
        }
    }

    public sealed record AuthorityScoreResult(
        decimal EvidencePct,
        decimal EstimatedPct,
        int AuthorityScore
    );
}
