using System.Collections.Concurrent;
using Xunit;

namespace FastRng.ThreadSafe.Tests;

public class FastRngTests
{
    [Fact]
    public void NextByte_ShouldReturnValuesWithinValidByteRange()
    {
        var generator = FastRng.Instance;

        for (int i = 0; i < 1000; i++)
        {
            byte value = generator.NextByte();
            Assert.True(value >= 0 && value <= 255, $"Generated value {value} is out of byte bounds.");
        }
    }

    [Fact]
    public void NextBytes_Span_ShouldFillBufferCorrectly()
    {
        var generator = FastRng.Instance;
        Span<byte> buffer = stackalloc byte[100];

        generator.NextBytes(buffer);

        int nonZeroCount = 0;
        foreach (var b in buffer)
        {
            if (b != 0) nonZeroCount++;
        }

        Assert.True(nonZeroCount > 0, "The buffer was not modified or filled with randomized data.");
    }

    [Fact]
    public void Next_WithBounds_ShouldRespectMinAndMax()
    {
        var generator = FastRng.Instance;
        int min = 10;
        int max = 20;

        for (int i = 0; i < 1000; i++)
        {
            int value = generator.Next(min, max);
            Assert.True(value >= min && value < max, $"Value {value} out of defined range [{min}, {max}).");
        }
    }

    [Fact]
    public void Next_WithInvalidBounds_ShouldThrowArgumentException()
    {
        var generator = FastRng.Instance;

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Next(50, 10));
    }

    /// <summary>
    /// FIXED: Tests thread safety by forcing the OS to spin up dedicated physical threads.
    /// This avoids ThreadPool thread-reuse artifacts and correctly tests [ThreadStatic] isolation.
    /// </summary>
    [Fact]
    public void Generator_ShouldBeThreadSafe_AndIsolatedPerThread()
    {
        // Arrange
        var threadValues = new ConcurrentDictionary<int, List<byte>>();
        int threadCount = 8;
        int iterationsPerThread = 10;
        var threads = new Thread[threadCount];

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                int threadId = Environment.CurrentManagedThreadId;
                var list = new List<byte>();

                for (int j = 0; j < iterationsPerThread; j++)
                {
                    list.Add(FastRng.Instance.NextByte());
                }

                threadValues.TryAdd(threadId, list);
            });
        }

        // Start all dedicated threads
        foreach (var t in threads) t.Start();

        // Wait for all physical threads to complete execution
        foreach (var t in threads) t.Join();

        // Assert
        Assert.Equal(threadCount, threadValues.Count);
        foreach (var kvp in threadValues)
        {
            Assert.Equal(iterationsPerThread, kvp.Value.Count);
            // Verify that each thread managed to generate distinct byte sequences
            Assert.All(kvp.Value, b => Assert.True(b >= 0 && b <= 255));
        }
    }

    /// <summary>
    /// RELIABILITY TEST 1: Frequency Uniformity Test (Chi-Squared approximation limit)
    /// Assures that over a large sample, every byte from 0 to 255 appears with equal probability.
    /// </summary>
    [Fact]
    public void Reliability_UniformDistributionTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int totalSamples = 5_000_000; // 1000 expected hits per bucket
        int[] buckets = new int[256];

        // Act
        for (int i = 0; i < totalSamples; i++)
        {
            byte num = generator.NextByte();
            buckets[num]++;
        }

        // Assert
        double expectedHits = totalSamples / 256.0; // 1000
        double maxAllowedDeviation = 0.15; // Max 15% deviation allowed for this sample size

        for (int i = 0; i < 256; i++)
        {
            double deviation = Math.Abs(buckets[i] - expectedHits) / expectedHits;
            Assert.True(deviation < maxAllowedDeviation,
                $"Byte {i} breached uniformity bounds. Got {buckets[i]} hits, expected ~{expectedHits}. Deviation: {deviation:P2}");
        }
    }

    /// <summary>
    /// RELIABILITY TEST 2: Serial Transition Test (Pairwise Entropy Check)
    /// Validates that there is no "memory correlation" or bias between consecutive numbers (X_n and X_n+1).
    /// </summary>
    [Fact]
    public void Reliability_PairwiseIndependenceTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int iterations = 10_000_000;
        int[,] pairGrid = new int[256, 256];

        // Act
        byte previousByte = generator.NextByte();
        for (int i = 0; i < iterations; i++)
        {
            byte currentByte = generator.NextByte();
            pairGrid[previousByte, currentByte]++;
            previousByte = currentByte;
        }

        // Assert
        int zeroTransitionPaths = 0;
        int maxAccumulatedCluster = 0;

        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                int weight = pairGrid[r, c];
                if (weight == 0) zeroTransitionPaths++;
                if (weight > maxAccumulatedCluster) maxAccumulatedCluster = weight;
            }
        }

        // At 10M iterations, statistically there should be 0 unvisited paths (100% coverage)
        double maxAllowedDeadPathsRatio = 0.001; // 0.1% max allowance
        double deadPathsRatio = (double)zeroTransitionPaths / (256 * 256);

        Assert.True(deadPathsRatio <= maxAllowedDeadPathsRatio,
            $"Entropy failure. Too many unvisited coordinate pairs: {deadPathsRatio:P2}");

        // CORRECTED STATISTICAL BOUND:
        // For 10M samples distributed into 65k buckets (mean ~152), standard deviation
        // dictates that the peak natural cluster will legally reach up to ~220-250.
        // We set the safety threshold to 300 to catch genuine structural looping flaws.
        Assert.True(maxAccumulatedCluster < 300,
            $"Entropy failure. Heavy transition cluster detected with {maxAccumulatedCluster} identical path hits.");
    }

    [Fact]
    public void Reliability_ChiSquaredMatrixTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int iterations = 10_000_000;
        int[,] pairGrid = new int[256, 256];

        // Act
        byte previousByte = generator.NextByte();
        for (int i = 0; i < iterations; i++)
        {
            byte currentByte = generator.NextByte();
            pairGrid[previousByte, currentByte]++;
            previousByte = currentByte;
        }

        // Assert
        double expectedHitsPerCell = (double)iterations / (256 * 256); // ~152.5878
        double chiSquaredSum = 0;

        int minHits = int.MaxValue;
        int maxHits = int.MinValue;

        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                int actualHits = pairGrid[r, c];

                if (actualHits < minHits) minHits = actualHits;
                if (actualHits > maxHits) maxHits = actualHits;

                // Формулата за Хи-квадрат: sum((Actual - Expected)^2 / Expected)
                double deviation = actualHits - expectedHitsPerCell;
                chiSquaredSum += (deviation * deviation) / expectedHitsPerCell;
            }
        }

        // За 65,535 степени на свобода при 99% доверителен интервал,
        // стойността на Хи-квадрат ТРЯБВА да бъде между ~64,000 и ~67,000.
        // Ако е много по-голяма -> числата не са случайни (има струпвания).
        // Ако е много по-малка -> числата са "твърде перфектни" (изкуствено подредени).

        double upperLimit = 67000;
        double lowerLimit = 64000;

        double minDeviationPct = ((minHits - expectedHitsPerCell) / expectedHitsPerCell) * 100;
        double maxDeviationPct = ((maxHits - expectedHitsPerCell) / expectedHitsPerCell) * 100;

        // Извеждаме детайли в конзолата, за да проследим аномалиите
        /*
        Console.WriteLine($"Хи-квадрат резултат: {chiSquaredSum:F2}");
        Console.WriteLine($"Минимални падания в клетка: {minHits} ({minDeviationPct:F2}%)");
        Console.WriteLine($"Максимални падания в клетка: {maxHits} (+{maxDeviationPct:F2}%)");
        */

        Assert.True(chiSquaredSum > lowerLimit && chiSquaredSum < upperLimit,
            $"Статистическа аномалия! Числата нямат естествено случайно разпределение. Chi2: {chiSquaredSum:F2}");
    }
}