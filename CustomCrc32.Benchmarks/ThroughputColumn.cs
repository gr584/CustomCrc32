using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// Reports each case as bytes processed per second, the unit a checksum is normally
/// judged in. Derived from the mean and the case's <c>WordCount</c> parameter rather
/// than measured separately.
/// </summary>
public sealed class ThroughputColumn : IColumn
{
    /// <summary>Name of the benchmark parameter holding the input length in words.</summary>
    private const string WordCountParameter = "WordCount";

    public string Id => nameof(ThroughputColumn);

    public string ColumnName => "Throughput";

    public string Legend => "Input size divided by the mean execution time";

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool AlwaysShow => true;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        double? meanNanoseconds = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (meanNanoseconds is not > 0)
        {
            return "N/A";
        }

        if (benchmarkCase.Parameters[WordCountParameter] is not int wordCount)
        {
            return "N/A";
        }

        // Bytes per nanosecond is numerically identical to gigabytes per second.
        double gigabytesPerSecond = wordCount * (double)sizeof(uint) / meanNanoseconds.Value;

        return gigabytesPerSecond >= 1.0
            ? $"{gigabytesPerSecond:F2} GB/s"
            : $"{gigabytesPerSecond * 1000:F1} MB/s";
    }
}
