using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
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
                        // Replace 'FastRng' with your exact instantiation call
                        // Simulating a standard roulette or slot reel outcome (0-36)
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
            for (int i = 0; i < threadCount; i++)
            {
                for (int j = i + 1; j < threadCount; j++)
                {
                    double correlation = CalculatePearsonCorrelation(threadOutputs[i], threadOutputs[j]);

                    // Regulatory standard: Correlation coefficient must be practically zero (-0.05 to 0.05)
                    Assert.True(Math.Abs(correlation) < 0.05,
                        $"Threads {i} and {j} show a correlation of {correlation}. This indicates spatial leakage in the shared matrix.");
                }
            }
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

        private double CalculatePearsonCorrelation(List<int> x, List<int> y)
        {
            double meanX = x.Average();
            double meanY = y.Average();

            double sumXY = 0, sumX2 = 0, sumY2 = 0;

            for (int i = 0; i < x.Count; i++)
            {
                double deltaX = x[i] - meanX;
                double deltaY = y[i] - meanY;

                sumXY += deltaX * deltaY;
                sumX2 += deltaX * deltaX;
                sumY2 += deltaY * deltaY;
            }

            if (sumX2 == 0 || sumY2 == 0) return 0;
            return sumXY / Math.Sqrt(sumX2 * sumY2);
        }
    }
}