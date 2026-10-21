using PerfBench;

// 性能基准工具：生成样例 + 跑基准 + 出结论。
// 用法：
//   dotnet run --project Uviewer/Sample/PerfBenchmark/tools -- sample   <书源JSON> <关键词> <章数>
//   dotnet run --project Uviewer/Sample/PerfBenchmark/tools -- bench
var mode = args.Length > 0 ? args[0] : "bench";

var root = FindRepoRoot();
var sampleDir = Path.Combine(root, "Sample", "PerfBenchmark", "text");
var rulesDir = Path.Combine(root, "Sample", "PerfBenchmark", "rules");
var resultsDir = Path.Combine(root, "Sample", "PerfBenchmark", "results");
var sourcePath = args.Length > 1 && args[1].Contains(".json", StringComparison.OrdinalIgnoreCase)
    ? args[1]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Uviewer", "booksources.json");

switch (mode)
{
    case "sample":
    {
        var keyword = args.Length > 2 ? args[2] : "斗破苍穹";
        var count = args.Length > 3 && int.TryParse(args[3], out var c) ? c : 60;
        var n = await SampleGenerator.GenerateAsync(sampleDir, sourcePath, keyword, count);
        return n > 0 ? 0 : 1;
    }

    case "bench":
    {
        await Benchmark.RunAsync(sampleDir, rulesDir, resultsDir);
        return 0;
    }

    case "proof":
    {
        await PurifyTimingProof.RunAsync();
        return 0;
    }

    default:
        Console.WriteLine("未知模式。用法：sample | bench | proof");
        return 2;
}

// 从当前目录向上找仓库根（含 Sample 目录的那一层）
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Sample")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return Environment.CurrentDirectory;
}
