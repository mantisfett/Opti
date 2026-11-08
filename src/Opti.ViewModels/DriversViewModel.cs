using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Drivers;
using Opti.Core.Findings;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed class DriverRow
{
    public required string Name { get; init; }
    public required string Details { get; init; }
    public required string StatusText { get; init; }
    public required string Tone { get; init; }
    public required string LinkTitle { get; init; }
    public required string Url { get; init; }
    public bool NeedsAttention { get; init; }
}

public sealed class DriverGroup
{
    public required string Title { get; init; }
    public required List<DriverRow> Rows { get; init; }
}

/// <summary>Все важные драйверы: состояние и официальная ссылка на обновление. Opti драйверы не скачивает.</summary>
public sealed partial class DriversViewModel : PageViewModel
{
    private List<DriverGroup> _all = new();
    public DriversViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Драйверы");

    public ObservableCollection<DriverGroup> Groups { get; } = new();
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _oemTitle;
    [ObservableProperty] private string? _oemUrl;
    [ObservableProperty] private string? _boardText;
    [ObservableProperty] private bool _onlyAttention;
    public bool HasOem => OemUrl is not null;
    partial void OnOemUrlChanged(string? value) => OnPropertyChanged(nameof(HasOem));
    partial void OnOnlyAttentionChanged(bool value) => Filter();

    public override void OnShown()
    {
        if (Main.Session?.Profile is { } p) Load(p, DateTimeOffset.Now);
    }

    public void Load(SystemProfile p, DateTimeOffset now)
    {
        var groups = new List<DriverGroup>();
        var problems = p.ProblemDevices.Where(d => d.ErrorCode != 22).Select(d => new DriverRow
        {
            Name = d.Name, Details = d.DeviceClass ?? Loc.T("устройство"), StatusText = d.ErrorText, Tone = Palette.Bad, NeedsAttention = true,
            LinkTitle = Loc.T("Необязательные обновления"), Url = Sources.WindowsOptionalUpdates,
        }).ToList();
        if (problems.Count > 0) groups.Add(new DriverGroup { Title = Loc.T("Устройства без драйвера или с ошибкой"), Rows = problems });

        foreach (var cat in DriverAdvisor.KeyCategories)
        {
            var rows = p.Drivers.Where(d => d.Category == cat).OrderBy(d => d.DeviceName).Select(d =>
            {
                var st = DriverAdvisor.Evaluate(d, now);
                var link = DriverAdvisor.LinkFor(d, p.Device);
                return new DriverRow
                {
                    Name = d.DeviceName,
                    Details = string.Join(" · ", new[] { d.Provider, d.Version is null ? null : Loc.T("версия ") + d.Version, d.Date?.ToString("dd.MM.yyyy") }.Where(x => !string.IsNullOrEmpty(x))),
                    StatusText = st switch
                    {
                        DriverStatus.Missing => Loc.T("драйвер не установлен"),
                        DriverStatus.Old => Loc.T("старый — обновите"),
                        DriverStatus.Generic => Loc.T("стандартный Microsoft"),
                        _ => Loc.T("в порядке"),
                    },
                    Tone = st switch { DriverStatus.Missing => Palette.Bad, DriverStatus.Ok => Palette.Good, _ => Palette.Warn },
                    NeedsAttention = st != DriverStatus.Ok,
                    LinkTitle = link.Title, Url = link.Url,
                };
            }).ToList();
            if (rows.Count > 0) groups.Add(new DriverGroup { Title = DriverAdvisor.CategoryName(cat), Rows = rows });
        }
        _all = groups;

        var oem = DriverAdvisor.OemSupport(p.Device);
        OemTitle = oem?.Title;
        OemUrl = oem?.Url;
        var board = p.Device.IsLaptop ? $"{p.Device.Manufacturer} {p.Device.Model}" : $"{p.Device.BoardManufacturer} {p.Device.BoardProduct}";
        BoardText = string.IsNullOrWhiteSpace(board) ? null : (p.Device.IsLaptop ? Loc.T("Ноутбук: ") : Loc.T("Материнская плата: ")) + board.Trim();

        int attention = groups.Sum(g => g.Rows.Count(r => r.NeedsAttention));
        int total = groups.Sum(g => g.Rows.Count);
        Summary = total == 0 ? Loc.T("Список драйверов недоступен — повторите анализ с правами администратора.")
            : attention == 0 ? Loc.T($"Проверено {total}: всё в порядке.")
            : Loc.T($"Проверено {total}, требуют внимания {attention}.");
        Filter();
    }

    private void Filter()
    {
        Groups.Clear();
        foreach (var g in _all)
        {
            var rows = OnlyAttention ? g.Rows.Where(r => r.NeedsAttention).ToList() : g.Rows;
            if (rows.Count > 0) Groups.Add(new DriverGroup { Title = g.Title, Rows = rows });
        }
    }

    [RelayCommand] private void OpenLink(string url) => Main.Services.OpenPath(url);
    [RelayCommand] private void OpenOptionalUpdates() => Main.Services.OpenPath(Sources.WindowsOptionalUpdates);
    [RelayCommand] private void OpenOem() { if (OemUrl is not null) Main.Services.OpenPath(OemUrl); }
}
