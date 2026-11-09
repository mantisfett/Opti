using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Fixes.Catalog;

namespace Opti.Tests;

/// <summary>Числа в README совпадают с каталогами — документация не отстаёт от кода.</summary>
public class DocsTests
{
    private static string Readme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
    }

    [Fact]
    public void Readme_CountsMatchCatalogs()
    {
        var readme = Readme();
        int fixes = FixCatalog.All().Count, detectors = DetectorCatalog.All().Count, collectors = typeof(SystemAnalyzer).Assembly.GetTypes()
            .Count(t => t is { IsAbstract: false, IsInterface: false } && typeof(Opti.Analysis.Collectors.ICollector).IsAssignableFrom(t));
        Assert.True(readme.Contains($"{fixes} исправлени", StringComparison.Ordinal), $"README: ожидается «{fixes} исправлений» (детекторов {detectors}, коллекторов {collectors})");
        Assert.True(readme.Contains($"{detectors} детектор", StringComparison.Ordinal), $"README: ожидается «{detectors} детекторов»");
        Assert.True(readme.Contains($"{collectors} коллектор", StringComparison.Ordinal), $"README: ожидается «{collectors} коллекторов»");
    }

    [Fact]
    public void Readme_DescribesAllModes()
    {
        var readme = Readme();
        foreach (var g in Opti.Core.Strategy.GoalInfo.All) Assert.Contains($"**{g.Title}**", readme, StringComparison.Ordinal);
    }
}
