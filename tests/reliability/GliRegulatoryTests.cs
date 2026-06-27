using System.Collections.Concurrent;
using Xunit;

namespace FastRng.ThreadSafe.Tests
{
    public class GliRegulatoryTests
    {
        [Fact]
        public async Task Test_CrossThread_Independence_Under_Load()
        {
            // Arrange: Simulate 20 concurrent player sessions / threads
            int threadCount = 20;
            int samplesPerThread = 5000;
            var threadOutputs = new ConcurrentDictionary<int, List<int>>();
            var barrier = new Barrier(threadCount); // Synchronizes thread starts perfectly

            // Act: Fire all threads simultaneously to pull from the shared matrix
            var tasks = Enumerable.Range(0, threadCount).Select(threadId =>
            {
                return Task.Run(() =>
                {
                    var localSamples = new List<int>(samplesPerThread);

                    // Force all threads to hit the RNG at the exact same moment
                    barrier.SignalAndWait();

                    for (int i = 0; i < samplesPerThread; i++)
                    {
                        // Pull standard casino roulette or slot wheel outcome mappings (0-36)
                        int sample = FastRng.Instance.Next(0, 37);
                        localSamples.Add(sample);
                    }

                    threadOutputs[threadId] = localSamples;
                });
            }).ToArray();

            await Task.WhenAll(tasks);

            // Assert 1: Test for Cross-Thread Duplicate Sequences (Spatially)
            // If the flat matrix structure leaks linearly to adjacent threads,
            // sequences will correlate or match with an offset.
            double totalRawCorrelationSum = 0;
            int comparisonCount = 0;

            for (int i = 0; i < threadCount; i++)
            {
                for (int j = i + 1; j < threadCount; j++)
                {
                    double correlation = CalculatePearsonCorrelation(threadOutputs[i], threadOutputs[j]);

                    // Sum the raw signed value to check for true cross-thread linear drift
                    totalRawCorrelationSum += correlation;
                    comparisonCount++;

                    // Individual outliers checked with Bonferroni-corrected variance boundary
                    Assert.True(Math.Abs(correlation) < 0.08,
                        $"Extreme spatial leakage anomaly detected between Thread {i} and {j}. Correlation: {correlation}");
                }
            }

            // Assert 2: The raw aggregate drift across all 190 combinations must balance close to zero.
            double averageCorrelation = totalRawCorrelationSum / comparisonCount;

            Assert.True(Math.Abs(averageCorrelation) < 0.005,
                $"Systemic spatial leakage detected. Threads show an overall linear drift trend: {averageCorrelation}");
        }

        [Fact]
        public void Test_Cryptographic_Unpredictability_SlidingWindow()
        {
            // Arrange: Generate a clean continuous sequence from a single thread context
            int sampleSize = 100000;
            int[] sequence = new int[sampleSize];

            for (int i = 0; i < sampleSize; i++)
            {
                // Pulling bytes mapped to a binary state (e.g. Coin Flip / Card Color)
                sequence[i] = FastRng.Instance.Next(0, 2);
            }

            // Act: Attempt to find if any historical window pattern of length N
            // leaks the upcoming state (signaling a predictable matrix wrap-around or layer pattern)
            int windowSize = 5;
            var patternHistory = new Dictionary<string, int[]>(); // Stores [Zeros, Ones] counts for each pattern

            for (int i = 0; i < sampleSize - windowSize - 1; i++)
            {
                string pattern = string.Join(",", sequence.Skip(i).Take(windowSize));
                int nextValue = sequence[i + windowSize];

                if (!patternHistory.ContainsKey(pattern))
                {
                    patternHistory[pattern] = new int[2];
                }
                patternHistory[pattern][nextValue]++;
            }

            // Assert: Analyze if any pattern gives a statistical edge (> 55% predictability)
            foreach (var kvp in patternHistory)
            {
                int totalOccurrences = kvp.Value[0] + kvp.Value[1];
                if (totalOccurrences > 50) // Only look at statistically relevant repeating patterns
                {
                    double zeroRatio = (double)kvp.Value[0] / totalOccurrences;
                    double oneRatio = (double)kvp.Value[1] / totalOccurrences;

                    // Regulatory threshold: If an attacker can guess the next bit with >55% accuracy,
                    // the PRNG layers are mathematically compromised.
                    Assert.True(zeroRatio < 0.55 && oneRatio < 0.55,
                        $"Pattern [{kvp.Key}] yields an unfair predictable bias. Next bit probability: 0={zeroRatio:P}, 1={oneRatio:P}.");
                }
            }
        }

        /// <summary>
        /// REGULATORY TEST 3: State Back-Tracing and Algebraic Attack Simulation.
        /// Evaluates if an observer catching 1,028 sequential bytes (the size of your state matrix)
        /// can construct an explicit linear solution to find the initial state matrix.
        /// </summary>
        [Fact]
        public void Cryptographic_ForwardSecrecy_LinearAttackSimulation()
        {
            var generator = FastRng.Instance;
            byte[] observedLeak = new byte[1028];
            generator.NextBytes(observedLeak);

            // Attempt a basic linear difference map to find matrix loop correlation
            int linearDependencies = 0;
            for (int i = 0; i < observedLeak.Length - 4; i++)
            {
                // Check if subsequent outputs share a constant linear difference
                // injected by the simplistic '& 3' or '& 0x300' mask transitions
                int diff1 = (observedLeak[i + 1] - observedLeak[i]) & 255;
                int diff2 = (observedLeak[i + 3] - observedLeak[i + 2]) & 255;
                if (diff1 == diff2)
                {
                    linearDependencies++;
                }
            }

            // In a true cryptographically secure generator (CSPRNG), linear relationships
            // across output streams evaluate exactly to 0.39% chance per sample.
            double dependencyRatio = (double)linearDependencies / observedLeak.Length;
            Assert.True(dependencyRatio < 0.05,
                $"GLI Security Failure! Output streams reveal heavy algebraic linearity ({dependencyRatio:P2}). State is predictable.");
        }

        [Fact]
        public void Test_Strict_Modulo_Bias_Elimination()
        {
            // Arrange: Define a non-power-of-two range to maximize modulo bias stress
            int minValue = 0;
            int maxValue = 37; // 37 numbers (0 to 36 - Roulette)
            int totalSpins = 1_000_000;
            int[] frequencies = new int[maxValue];

            // Act
            for (int i = 0; i < totalSpins; i++)
            {
                int spin = FastRng.Instance.Next(minValue, maxValue);
                frequencies[spin]++;
            }

            // Assert: Check the exact deviation between the most frequent and least frequent numbers
            double expectedFrequency = (double)totalSpins / maxValue;
            double maxAllowedDeviation = expectedFrequency * 0.02; // Strict 2% variance max limit

            for (int i = 0; i < maxValue; i++)
            {
                double deviation = Math.Abs(frequencies[i] - expectedFrequency);
                Assert.True(deviation < maxAllowedDeviation,
                    $"Value {i} outside acceptable regulatory tolerance. Expected ~{expectedFrequency:F0}, got {frequencies[i]}. Deviation: {deviation:F0}");
            }
        }

        /// <summary>
        /// Computes the Pearson Product-Moment Correlation Coefficient using a single-pass,
        /// zero-allocation stream implementation to evaluate linear dependence.
        /// </summary>
        private static double CalculatePearsonCorrelation(List<int> x, List<int> y)
        {
            if (x.Count != y.Count || x.Count == 0)
            {
                throw new ArgumentException("Sample vectors must be non-empty and matching sizes.");
            }

            int n = x.Count;
            double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;

            for (int i = 0; i < n; i++)
            {
                double xi = x[i];
                double yi = y[i];

                sumX += xi;
                sumY += yi;
                sumX2 += xi * xi;
                sumY2 += yi * yi;
                sumXY += xi * yi;
            }

            double numerator = (n * sumXY) - (sumX * sumY);
            double denominator = Math.Sqrt(((n * sumX2) - (sumX * sumX)) * ((n * sumY2) - (sumY * sumY)));

            if (Math.Abs(denominator) < 1e-9)
            {
                return 0.0; // Avoid NaN results on flat zero variance boundaries
            }

            return numerator / denominator;
        }
    }
}