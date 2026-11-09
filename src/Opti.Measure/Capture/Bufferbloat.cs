using System.Net;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

public enum BloatGrade { A, B, C, D, F }

/// <param name="IdleMs">Пинг в простое.</param>
/// <param name="DownloadMs">Пинг, пока канал забит скачиванием (null — не измерялось).</param>
/// <param name="UploadMs">Пинг, пока канал забит отдачей (null — не измерялось).</param>
public sealed record BufferbloatResult(string Target, double IdleMs, double IdleJitterMs, double LossPercent, double? DownloadMs, double? UploadMs)
{
    public double? DownloadIncreaseMs => DownloadMs - IdleMs;
    public double? UploadIncreaseMs => UploadMs - IdleMs;
    public double WorstIncreaseMs => Math.Max(DownloadIncreaseMs ?? 0, UploadIncreaseMs ?? 0);
    public BloatGrade Grade => Bufferbloat.GradeFor(WorstIncreaseMs);
}

/// <summary>
/// Bufferbloat: во сколько раз растёт пинг, когда канал занят. Сначала пинг в простое, затем под скачиванием и под отдачей
/// (чаще виновата отдача: исходящий канал у домашнего интернета узкий). Рост пинга оценивается по шкале A–F, как в известных
/// онлайн-тестах: до 30 мс — A, до 60 — B, до 200 — C, до 400 — D, больше — F.
/// </summary>
public static class Bufferbloat
{
    public const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=200000000";
    public const string UploadUrl = "https://speed.cloudflare.com/__up";

    public static BloatGrade GradeFor(double increaseMs) =>
        increaseMs < 30 ? BloatGrade.A : increaseMs < 60 ? BloatGrade.B : increaseMs < 200 ? BloatGrade.C : increaseMs < 400 ? BloatGrade.D : BloatGrade.F;

    public static string GradeTone(BloatGrade g) => g switch { BloatGrade.A or BloatGrade.B => "good", BloatGrade.C => "warn", _ => "bad" };

    /// <summary>Вывод простым языком. Решение для плохих оценок — на роутере: SQM / Smart Queue.</summary>
    public static string Describe(BufferbloatResult r)
    {
        if (double.IsNaN(r.IdleMs)) return Loc.T("Сервер не ответил — проверьте подключение к интернету.");
        var worst = r.UploadIncreaseMs >= r.DownloadIncreaseMs ? Loc.T("отдачи") : Loc.T("скачивания");
        return r.Grade switch
        {
            BloatGrade.A or BloatGrade.B => Loc.T($"Под нагрузкой пинг почти не растёт (+{r.WorstIncreaseMs:0} мс): очередь на роутере под контролем."),
            BloatGrade.C => Loc.T($"Пинг растёт на {r.WorstIncreaseMs:0} мс, когда канал занят ({worst}). В онлайн-играх это заметные рывки, если кто-то качает или смотрит видео. Лечится на роутере: SQM / Smart Queue / Adaptive QoS со скоростью ~90 % от тарифа."),
            _ => Loc.T($"Пинг растёт на {r.WorstIncreaseMs:0} мс, когда канал занят ({worst}) — это сильный bufferbloat. Пока кто-то качает или загружает файлы, игра будет лагать. Включите на роутере SQM / Smart Queue / Adaptive QoS (скорость ~90 % от тарифа) или ограничьте скорость загрузчиков. Программа на ПК этого не исправит."),
        };
    }

    public static async Task<BufferbloatResult> RunAsync(string target = NetworkTest.DefaultTarget, IProgress<string>? phase = null,
        int pings = 25, CancellationToken ct = default)
    {
        phase?.Report(Loc.T("Пинг в простое…"));
        var idle = await NetworkTest.PingSeriesAsync(target, pings, ct);
        double idleMs = idle.Ok.Count > 0 ? idle.Ok.Average() : double.NaN;
        double? down = null, up = null;
        if (idle.Ok.Count > 0)
        {
            phase?.Report(Loc.T("Пинг под скачиванием…"));
            down = await UnderLoadAsync(target, pings, ct, DownloadAsync);
            phase?.Report(Loc.T("Пинг под отдачей…"));
            up = await UnderLoadAsync(target, pings, ct, UploadAsync);
        }
        return new BufferbloatResult(target, idleMs, NetworkTest.Jitter(idle.Ok), 100.0 * idle.Lost / Math.Max(1, pings), down, up);
    }

    private static async Task<double?> UnderLoadAsync(string target, int pings, CancellationToken ct, Func<CancellationToken, Task> load)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = Enumerable.Range(0, 4).Select(_ => load(cts.Token)).ToList();
        try
        {
            await Task.Delay(2000, ct); // дать очереди наполниться
            var s = await NetworkTest.PingSeriesAsync(target, pings, ct);
            // Если загрузка закончилась раньше пингов (сервер отказал), нагрузки не было — честнее ничего не показывать
            bool held = tasks.Any(t => !t.IsCompleted);
            return held && s.Ok.Count > 0 ? s.Ok.Average() : null;
        }
        finally
        {
            cts.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* отмена загрузки */ }
        }
    }

    private static async Task DownloadAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        var buf = new byte[1 << 16];
        // На быстром канале 200 МБ скачиваются за пару секунд — качаем снова, пока не отменят
        while (!ct.IsCancellationRequested)
        {
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            while (await s.ReadAsync(buf, ct) > 0) { }
        }
    }

    private static async Task UploadAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var content = new EndlessContent();
        while (!ct.IsCancellationRequested)
        {
            try { using var resp = await http.PostAsync(UploadUrl, content, ct); resp.EnsureSuccessStatusCode(); }
            catch (Exception) when (ct.IsCancellationRequested) { }
        }
    }

    /// <summary>Тело запроса, которое пишет данные, пока не отменят: так забивается исходящий канал.</summary>
    private sealed class EndlessContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await Write(stream, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => await Write(stream, ct);
        private static async Task Write(Stream stream, CancellationToken ct)
        {
            var buf = new byte[1 << 16];
            Random.Shared.NextBytes(buf);
            while (!ct.IsCancellationRequested) await stream.WriteAsync(buf, ct);
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }
}
