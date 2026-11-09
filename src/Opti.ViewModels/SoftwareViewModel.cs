using CommunityToolkit.Mvvm.Input;
using Opti.Core.Software;
using Opti.Core.Localization;

namespace Opti.ViewModels;

/// <summary>Рекомендуемые программы по категориям — только ссылки на официальные сайты.</summary>
public sealed partial class SoftwareViewModel : PageViewModel
{
    public SoftwareViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Программы");
    /// <summary>Каталог на языке интерфейса (сам каталог — русский источник).</summary>
    public IReadOnlyList<SoftwareCategory> Categories { get; } = SoftwareCatalog.All
        .Select(c => new SoftwareCategory(Loc.T(c.Title), c.Icon,
            c.Items.Select(e => e with { Description = Loc.T(e.Description), Note = e.Note is null ? null : Loc.T(e.Note) }).ToList()))
        .ToList();

    [RelayCommand] private void Open(SoftwareEntry e) => Main.Services.OpenPath(e.Url);
}
