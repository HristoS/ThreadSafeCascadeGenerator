```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean           | Ratio     | RatioSD  | Allocated | Alloc Ratio |
|----------------------- |---------------:|----------:|---------:|----------:|------------:|
| SystemRandom_Next      |       2.079 ns |      1.01 |     0.14 |         - |          NA |
| FastRng_NextByte       |      46.399 ns |     22.54 |     2.19 |         - |          NA |
| SystemRandom_NextBytes |   9,808.341 ns |  4,763.91 |   546.19 |         - |          NA |
| FastRng_NextBytes      | 152,251.897 ns | 73,948.79 | 6,932.31 |         - |          NA |
