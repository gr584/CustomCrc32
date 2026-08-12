using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using CustomCrc32.Benchmarks;

// Benchmarks are only meaningful against an optimised build:
//   dotnet run -c Release --project CustomCrc32.Benchmarks
// Append `-- --filter *` style arguments to narrow the run; they are forwarded through.
BenchmarkRunner.Run<Crc32Benchmarks>(
    DefaultConfig.Instance.AddColumn(new ThroughputColumn()),
    args);
