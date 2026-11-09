using Opti.ViewModels;
using Opti.ViewModels.Demo;

namespace Opti.Tests;

/// <summary>Демо-окружение (снимки интерфейса в CI) проходит весь путь без Windows и ничего не трогает.</summary>
public sealed class DemoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-demo-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Demo_AnalyzePlanCompare()
    {
        var svc = new DemoAppServices(_dir);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        Assert.Same(main.Analysis, main.CurrentPage);
        Assert.NotEmpty(main.Analysis.Findings);

        var s = main.Session!;
        main.Strategy.Load(s);
        Assert.NotEmpty(main.Strategy.Fixes);

        var wf = svc.Workflow;
        wf.RecordBefore(s, Enumerable.Range(0, 3).Select(i => DemoAppServices.SyntheticRun($"до {i}", false, i)));
        wf.Apply(s, wf.BuildPlan(s).Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList());
        wf.RecordAfter(s, Enumerable.Range(0, 3).Select(i => DemoAppServices.SyntheticRun($"после {i}", true, 10 + i)));
        main.ShowCompare(CompareViewModel.ForSession(main, s));
        var cmp = Assert.IsType<CompareViewModel>(main.CurrentPage);
        Assert.Contains(cmp.Cards, c => c.Name == "Средний FPS" && c.Color == Palette.Good);
    }

    [Fact]
    public void Demo_HasEmbeddedProfiles()
    {
        Assert.NotNull(DemoAppServices.LoadEmbeddedProfile("desktop-rtx").DiscreteGpu);
        Assert.True(DemoAppServices.LoadEmbeddedProfile("laptop-optimus").Device.IsLaptop);
    }

    [Fact]
    public void Demo_ShowsBanner_ButScreenshotsDoNot()
    {
        Assert.NotNull(new MainViewModel(new DemoAppServices(_dir)).DemoBanner);
        Assert.Null(new MainViewModel(new DemoAppServices(_dir) { IsDemo = false }).DemoBanner);
    }
}
