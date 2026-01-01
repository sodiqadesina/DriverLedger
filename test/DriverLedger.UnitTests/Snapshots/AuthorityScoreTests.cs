

using DriverLedger.Infrastructure.Statements.Snapshots;

namespace DriverLedger.UnitTests.Snapshots
{
    public sealed class AuthorityScoreTests
    {
        [Theory]
        [InlineData(10, 0, 0, 0.0)]
        [InlineData(10, 5, 50, 0.5)]
        [InlineData(10, 10, 100, 1.0)]
        public void Authority_score_is_computed_correctly(
            int total,
            int evidenced,
            int expectedScore,
            double expectedEvidencePct)
        {
            var result = AuthorityScoreCalculator.Compute(total, evidenced);

            result.AuthorityScore.Should().Be(expectedScore);
            result.EvidencePct.Should().Be((decimal)expectedEvidencePct);
            result.EstimatedPct.Should().Be(1m - (decimal)expectedEvidencePct);
        }
    }
}
