using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using CustomCrc32.Benchmarks;

// Benchmarks are only meaningful against an optimised build:
//   dotnet run -c Release --project CustomCrc32.Benchmarks
// Append `-- --filter *` style arguments to narrow the run; they are forwarded through.
// Every benchmark class in the assembly runs by default, which includes the 1 GiB streaming
// set — filter it out when you only want the quick ones.
BenchmarkRunner.Run(
    Assembly.GetExecutingAssembly(),
    DefaultConfig.Instance.AddColumn(new ThroughputColumn()),
    args);
