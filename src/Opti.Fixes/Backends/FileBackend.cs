using Opti.Core.Fixes;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>
/// Файлы (конфиги OBS и т. п.). Read делает резервную копию и возвращает {Kind=BackupPath, Value=путь к копии}.
/// Write: Kind=Text — записать текст, Kind=BackupPath — восстановить из копии, Missing — удалить файл.
/// </summary>
public sealed class FileBackend : IStateBackend
{
    private readonly string _backupDir;
    public FileBackend(string backupDir) => _backupDir = backupDir;
    public ChangeType Type => ChangeType.File;

    public StateValue Read(string target)
    {
        if (!File.Exists(target)) return StateValue.Missing;
        Directory.CreateDirectory(_backupDir);
        var backup = Path.Combine(_backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Guid.NewGuid():N}_{Path.GetFileName(target)}");
        File.Copy(target, backup);
        return new StateValue(true, "BackupPath", backup);
    }

    public void Write(string target, StateValue value)
    {
        if (!value.Exists) { if (File.Exists(target)) File.Delete(target); return; }
        switch (value.Kind)
        {
            case "Text":
                var tmp = target + ".opti.tmp";
                File.WriteAllText(tmp, value.Value ?? "");
                File.Move(tmp, target, overwrite: true);
                break;
            case "BackupPath":
                var backup = value.Value!;
                // Копия могла переехать вместе с папкой данных (GameTune → Opti) — ищем её по имени в текущей папке копий
                if (!File.Exists(backup) && File.Exists(Path.Combine(_backupDir, Path.GetFileName(backup))))
                    backup = Path.Combine(_backupDir, Path.GetFileName(backup));
                if (!File.Exists(backup)) throw new FileNotFoundException(Loc.T("Резервная копия не найдена"), backup);
                File.Copy(backup, target, overwrite: true);
                break;
            default:
                throw new NotSupportedException(Loc.T($"Тип {value.Kind} для файла не поддерживается"));
        }
    }

    public static string ReadText(StateValue v) => v.Kind switch
    {
        "BackupPath" => File.ReadAllText(v.Value!),
        "Text" => v.Value ?? "",
        _ => throw new NotSupportedException(v.Kind),
    };
}
