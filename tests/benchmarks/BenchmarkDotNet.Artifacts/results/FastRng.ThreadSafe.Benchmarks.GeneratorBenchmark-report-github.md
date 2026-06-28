```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean             | Ratio        | RatioSD   | Allocated | Alloc Ratio |
|----------------------- |-----------------:|-------------:|----------:|----------:|------------:|
| SystemRandom_Next      |         1.871 ns |         1.00 |      0.03 |         - |          NA |
| FastRng_NextByte       |        35.511 ns |        18.98 |      0.44 |         - |          NA |
| SystemRandom_NextBytes |     8,944.203 ns |     4,781.07 |    138.01 |         - |          NA |
| FastRng_NextBytes      | 2,000,999.922 ns | 1,069,622.44 | 21,369.06 |         - |          NA |
