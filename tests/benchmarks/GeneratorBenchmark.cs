using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace FastRng.ThreadSafe.Benchmarks;

// Маскираме алокацията и статистическия шум в конзолата
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class GeneratorBenchmark
{
    private const int BufferSize = 65536; // 64KB буфер за масови тестове
    private byte[] _sharedBuffer = null!;
    private System.Random _systemRandom = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharedBuffer = new byte[BufferSize];
        _systemRandom = new System.Random();
    }

    // === ТЕСТ 1: ГЕНЕРИРАНЕ НА ЕДИНИЧНО ЧИСЛО ===
    [Benchmark(Baseline = true)]
    public int SystemRandom_Next() => _systemRandom.Next(0, 256);

    [Benchmark]
    public byte FastRng_NextByte() => FastRng.Instance.NextByte();

    // === ТЕСТ 2: ПОПЪЛВАНЕ НА ГОЛЯМ МАСИВ (64KB) ===
    [Benchmark]
    public void SystemRandom_NextBytes() => _systemRandom.NextBytes(_sharedBuffer);

    [Benchmark]
    public void FastRng_NextBytes() => FastRng.Instance.NextBytes(_sharedBuffer);
}

public class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run<GeneratorBenchmark>();
}