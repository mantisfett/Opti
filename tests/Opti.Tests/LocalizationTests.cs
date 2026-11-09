using System.Text.RegularExpressions;
using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Localization;
using Opti.Core.Software;
using Opti.Core.Strategy;
using Opti.Core.Tweaks;
using Opti.Fixes.Catalog;
using Opti.ViewModels;
using Opti.ViewModels.Demo;

namespace Opti.Tests;

[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection { }

/// <summary>Английский интерфейс: ничего русского не остаётся на проверяемых путях. Язык — глобальный, поэтому тесты идут не параллельно.</summary>
[Collection("Loc")]
public sealed class LocalizationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-loc-").FullName;

    public LocalizationTests() => Loc.SetLanguage("en");

    public void Dispose()
    {
        Loc.SetLanguage("ru");
        Directory.Delete(_dir, true);
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Opti.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static void NoCyrillic(string? text, string where) =>
        Assert.False(Loc.HasCyrillic(text), $"Не переведено ({where}): {text}");

    [Fact]
    public void Language_Auto_FollowsSystem_AndRussianIsIdentity()
    {
        Loc.SetLanguage("auto", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        Assert.False(Loc.IsEnglish);
        Loc.SetLanguage("auto", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        Assert.True(Loc.IsEnglish);
        Loc.SetLanguage("ru");
        Assert.Equal("Проанализировать", Loc.T("Проанализировать"));
        Loc.SetLanguage("en");
        Assert.Equal("Analyze", Loc.T("Проанализировать"));
    }

    [Fact]
    public void Patterns_TranslateInterpolatedText_AndNestedWords()
    {
        Assert.Equal("Found 12: critical 1, important 7, tips 4.", Loc.T("Найдено 12: критичных 1, важных 7, советов 4."));
        Assert.Equal("Enabled 3 of 11", Loc.T("Включено 3 из 11"));
        // русское слово внутри подстановки тоже переводится
        Assert.Equal("Display: Dell 2560×1440 @ 165 Hz (max 165)", Loc.T("Экран: Dell 2560×1440 @ 165 Гц (макс. 165)"));
        Assert.Equal("Unknown text", Loc.T("Unknown text"));
    }

    [Fact]
    public void AllXamlTexts_AreTranslated()
    {
        var keys = Loc.Keys.ToHashSet();
        var missing = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Opti.App"), "*.xaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\\b(?:Text|Content|Header|ToolTip|AutomationProperties\\.Name)=\"([^\"{][^\"]*)\""))
                if (Loc.HasCyrillic(m.Groups[1].Value) && !keys.Contains(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)))
                    missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        Assert.True(missing.Count == 0, "Нет перевода для XAML:\n" + string.Join("\n", missing));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Findings_AndPlan_HaveNoRussianLeft(string profile)
    {
        var p = TestProfiles.Load(profile);
        var findings = TestProfiles.Findings(p);
        Assert.NotEmpty(findings);
        foreach (var f in findings.Select(f => new FindingItem(f)))
        {
            NoCyrillic(f.Title, f.Finding.Id + " title");
            NoCyrillic(f.Explanation, f.Finding.Id + " explanation");
            foreach (var e in f.Evidence) NoCyrillic(e, f.Finding.Id + " evidence");
            foreach (var s in f.ManualSteps) NoCyrillic(s, f.Finding.Id + " step");
        }
        foreach (var goal in new[] { UserGoal.Maximum, UserGoal.Balanced, UserGoal.Minimal })
        {
            var plan = new StrategyEngine(FixCatalog.All()).Build(p, findings, goal);
            foreach (var pf in plan.Fixes)
            {
                var item = new FixItem(pf);
                NoCyrillic(item.Title, pf.Fix.Id);
                NoCyrillic(item.Explanation, pf.Fix.Id);
                foreach (var n in item.Notes) NoCyrillic(n, pf.Fix.Id + " note");
            }
            foreach (var e in plan.Excluded) NoCyrillic(Loc.T(e.Reason), e.FixId);
            NoCyrillic(plan.Device.Describe(), "device");
        }
    }

    public static IEnumerable<object[]> Profiles() => TestProfiles.All.Select(n => new object[] { n });

    [Fact]
    public void Catalogs_HaveNoRussianLeft()
    {
        foreach (var g in GoalOption.All)
        {
            foreach (var s in new[] { g.Title, g.Description, g.Summary, g.ChangesLevel, g.HeatLevel, g.Verification }.Concat(g.Includes).Concat(g.Skips)) NoCyrillic(s, "mode " + g.Goal);
        }
        foreach (var r in ModeComparisonRow.All)
        {
            NoCyrillic(r.Feature, "comparison");
            foreach (var c in r.Cells) NoCyrillic(c.Text, "comparison cell");
        }
        var svc = new DemoAppServices(_dir);
        var main = new MainViewModel(svc);
        foreach (var n in main.Nav) { NoCyrillic(n.Title, "nav"); NoCyrillic(n.Header, "nav header"); NoCyrillic(n.DisabledHint, "nav hint"); }
        foreach (var c in new SoftwareViewModel(main).Categories)
        {
            NoCyrillic(c.Title, "software category");
            foreach (var e in c.Items) { NoCyrillic(e.Description, e.Name); NoCyrillic(e.Note, e.Name); }
        }
        var tweaks = new TweaksViewModel(main);
        tweaks.OnShown();
        foreach (var g in tweaks.Groups)
        {
            NoCyrillic(g.Title, "tweak group");
            foreach (var t in g.Items) { NoCyrillic(t.Title, t.Tweak.Id); NoCyrillic(t.Description, t.Tweak.Id); NoCyrillic(t.ApplyText, t.Tweak.Id); }
        }
        foreach (var t in svc.CleanupTargets()) { NoCyrillic(t.Title, t.Id); NoCyrillic(t.Description, t.Id); NoCyrillic(t.Caution, t.Id); NoCyrillic(t.WarnText, t.Id); }
    }

    [Fact]
    public async Task DemoJourney_Messages_AreEnglish()
    {
        var svc = new DemoAppServices(_dir);
        var main = new MainViewModel(svc);
        NoCyrillic(main.DemoBanner, "demo banner");
        NoCyrillic(main.Home.SelectedGoalHint, "hint");
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        NoCyrillic(main.Analysis.Summary, "analysis summary");
        NoCyrillic(main.Analysis.AutoFixText, "auto fix text");
        main.Strategy.Load(main.Session!);
        NoCyrillic(main.Strategy.Header, "strategy header");
        NoCyrillic(main.Strategy.GoalSummary, "goal summary");
        await main.Network.RunRegionsCommand.ExecuteAsync(null);
        await main.Network.RunDnsCommand.ExecuteAsync(null);
        NoCyrillic(main.Network.RegionsSummary, "regions summary");
        NoCyrillic(main.Network.DnsSummary, "dns summary");
        foreach (var r in main.Network.Regions) { NoCyrillic(r.Name, "region"); NoCyrillic(r.MedianText, "region ms"); NoCyrillic(r.DetailText, "region detail"); }
        foreach (var r in main.Network.DnsRows) { NoCyrillic(r.Name, "dns"); NoCyrillic(r.MedianText, "dns ms"); }
        await main.Latency.RunCommand.ExecuteAsync(null);
        NoCyrillic(main.Latency.Summary, "latency summary"); NoCyrillic(main.Latency.TimerText, "latency timer"); NoCyrillic(main.Latency.Title, "latency title");
        foreach (var r in main.Latency.Rows) { NoCyrillic(r.DpcText, "latency dpc"); NoCyrillic(r.IsrText, "latency isr"); }
        foreach (var st in main.Latency.Steps) NoCyrillic(st, "latency step");
        await main.Network.RunTrafficCommand.ExecuteAsync(null);
        NoCyrillic(main.Network.TrafficSummary, "traffic summary");
        foreach (var r in main.Network.TrafficRows) { NoCyrillic(r.UpText, "traffic up"); NoCyrillic(r.DownText, "traffic down"); }
        await main.Network.RunWifiCommand.ExecuteAsync(null);
        NoCyrillic(main.Network.WifiSummary, "wifi summary");
        foreach (var r in main.Network.WifiRows) { NoCyrillic(r.Name, "wifi name"); NoCyrillic(r.Detail, "wifi detail"); NoCyrillic(r.Tag, "wifi tag"); }
        await main.Network.RunBloatCommand.ExecuteAsync(null);
        NoCyrillic(main.Network.BloatSummary, "bloat summary");
        NoCyrillic(main.Network.BloatIdle, "bloat idle"); NoCyrillic(main.Network.BloatDown, "bloat down"); NoCyrillic(main.Network.BloatUp, "bloat up");
        main.Network.AddSample(new Opti.Measure.Capture.PingSample(25));
        main.Network.AddSample(new Opti.Measure.Capture.PingSample(double.NaN));
        NoCyrillic(main.Network.MonitorLast, "monitor last"); NoCyrillic(main.Network.MonitorLoss, "monitor loss");
        foreach (var b in main.Network.Bars) NoCyrillic(b.Tip, "bar tip");
        await main.Network.RunRouteCommand.ExecuteAsync(null);
        NoCyrillic(main.Network.RouteSummary, "route summary");
        foreach (var h in main.Network.Hops) { NoCyrillic(h.Address, "hop"); NoCyrillic(h.MedianText, "hop ms"); }
        await main.Background.ScanCommand.ExecuteAsync(null);
        NoCyrillic(main.Background.Summary, "background summary");
        NoCyrillic(main.Background.Title, "background title");
        await main.Background.PrepareGameCommand.ExecuteAsync(null);
        NoCyrillic(main.Toast, "game session toast");
        main.Background.EndGameCommand.Execute(null);
        NoCyrillic(main.Toast, "game end toast");
        foreach (var r in main.Background.Rows) { NoCyrillic(r.MarkHint, "bg mark hint"); NoCyrillic(r.Category, "bg category"); NoCyrillic(r.MemoryText, "bg memory"); NoCyrillic(r.Detail, "bg detail"); }
        main.Startup.Load();
        NoCyrillic(main.Startup.Summary, "startup summary");
        NoCyrillic(main.Startup.Title, "startup title");
        foreach (var r in main.Startup.Rows) { NoCyrillic(r.Category, "startup category"); NoCyrillic(r.Source, "startup source"); NoCyrillic(r.AdviceText, "startup advice"); }
        var drivers = new DriversViewModel(main);
        drivers.Load(svc.Profile, DateTimeOffset.Now);
        NoCyrillic(drivers.Summary, "drivers summary");
        foreach (var g in drivers.Groups)
        {
            NoCyrillic(g.Title, "driver group");
            foreach (var r in g.Rows) { NoCyrillic(r.StatusText, r.Name); NoCyrillic(r.LinkTitle, r.Name); NoCyrillic(r.Details, r.Name); }
        }
    }
}
