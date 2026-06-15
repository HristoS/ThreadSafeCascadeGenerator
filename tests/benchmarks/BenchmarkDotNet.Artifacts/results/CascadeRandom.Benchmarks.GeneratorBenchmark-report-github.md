```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                  | Mean             | Ratio        | RatioSD    | Allocated | Alloc Ratio |
|------------------------ |-----------------:|-------------:|-----------:|----------:|------------:|
| SystemRandom_Next       |         2.181 ns |         1.01 |       0.16 |         - |          NA |
| CascadeRandom_NextByte  |        38.037 ns |        17.67 |       2.50 |         - |          NA |
| SystemRandom_NextBytes  |     9,713.469 ns |     4,512.44 |     580.81 |         - |          NA |
| CascadeRandom_NextBytes | 2,165,790.704 ns | 1,006,129.22 | 137,574.52 |         - |          NA |
