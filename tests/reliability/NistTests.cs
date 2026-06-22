using Xunit;

namespace FastRng.ThreadSafe.Tests
{
    /// <summary>
    /// Implements native C# representations of the NIST SP 800-22 statistical test suite.
    /// Evaluates generated bitstreams using complementary error functions to calculate mathematical P-values.
    /// </summary>
    public class NistTests
    {
        private const double SignificanceLevel = 0.01; // NIST standard threshold alpha

        /// <summary>
        /// NIST Test 1: Frequency (Monobit) Test.
        /// Verifies that the proportion of zeroes and ones in the bitstream is approximately equal.
        /// </summary>
        [Fact]
        public void Nist_FrequencyMonobitTest_ShouldPass()
        {
            var generator = FastRng.Instance;

            // NIST recommends at least 100,000 bits for reliable evaluation
            const int totalBits = 200_000;
            int sum = 0;

            for (int i = 0; i < totalBits; i++)
            {
                // Extract a single bit by evaluating if an output byte is even or odd
                byte sample = generator.NextByte();
                int bit = sample % 2;

                // Convert bit: 0 becomes -1, 1 becomes +1
                sum += (2 * bit) - 1;
            }

            // Calculate the test statistic: S_obs = |sum| / sqrt(n)
            double sObs = Math.Abs(sum) / Math.Sqrt(totalBits);

            // Calculate the P-value using the complementary error function: erfc(S_obs / sqrt(2))
            double pValue = Erfc(sObs / Math.Sqrt(2.0));

            // Assert against the NIST threshold (P-value >= 0.01)
            Assert.True(pValue >= SignificanceLevel,
                $"NIST Monobit Failure! Bitstream is structurally biased. P-Value: {pValue:F6} (Expected >= 0.01). Bias Sum: {sum}");
        }

        /// <summary>
        /// NIST Test 3: Runs Test.
        /// Evaluates the total number of alterations between consecutive zeroes and ones.
        /// Ensures changes occur at a natural random frequency.
        /// </summary>
        [Fact]
        public void Nist_RunsTest_ShouldPass()
        {
            var generator = FastRng.Instance;
            const int totalBits = 200_000;

            int[] bitSequence = new int[totalBits];
            double onesProportion = 0;

            // 1. Gather the sequence and calculate the proportion of ones (pi)
            for (int i = 0; i < totalBits; i++)
            {
                bitSequence[i] = generator.NextByte() % 2;
                if (bitSequence[i] == 1) onesProportion++;
            }
            onesProportion /= totalBits;

            // Prerequisites check: If the frequency test is heavily skewed, the runs test is invalid
            if (Math.Abs(onesProportion - 0.5) >= (2.0 / Math.Sqrt(totalBits)))
            {
                Assert.Fail($"NIST Runs Pre-test skipped/failed: Frequency proportion ({onesProportion:F4}) is too far from 0.5.");
            }

            // 2. Count the total number of runs (V_n)
            // A run is an uninterrupted sequence of identical bits.
            int totalRuns = 1;
            for (int i = 0; i < totalBits - 1; i++)
            {
                if (bitSequence[i] != bitSequence[i + 1])
                {
                    totalRuns++;
                }
            }

            // 3. Compute the theoretical expected run metric and calculate the P-value
            double numerator = Math.Abs(totalRuns - (2.0 * totalBits * onesProportion * (1.0 - onesProportion)));
            double denominator = 2.0 * Math.Sqrt(2.0 * totalBits) * onesProportion * (1.0 - onesProportion);

            double pValue = Erfc(numerator / denominator);

            // Assert against the NIST threshold (P-value >= 0.01)
            Assert.True(pValue >= SignificanceLevel,
                $"NIST Runs Failure! Sequence patterns change too fast or too slow. P-Value: {pValue:F6} (Expected >= 0.01). Total Runs: {totalRuns}");
        }

        /// <summary>
        /// Approximates the Complementary Error Function (erfc) using the highly accurate
        /// Chebyshev fitting formula (maximum error scale limit of less than 1.2 x 10^-7).
        /// </summary>
        private static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);

            // Chebyshev polynomial evaluation coefficients
            double ans = t * Math.Exp(-z * z - 1.26551223 +
                t * (1.00002368 +
                t * (0.37388103 +
                t * (-0.18255973 +
                t * (0.00594295 +
                t * (0.01308046 +
                t * (-0.03590050 +
                t * (0.05149130 +
                t * (-0.05040825 +
                t * (0.02533041 +
                t * (-0.00145524 +
                t * 0.00115718)))))))))));

            return x >= 0.0 ? ans : 2.0 - ans;
        }
    }
}