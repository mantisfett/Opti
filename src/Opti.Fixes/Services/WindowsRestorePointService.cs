using System.Management;
using System.Runtime.Versioning;
using Opti.Core.Fixes;
using Opti.Core.Localization;

namespace Opti.Fixes.Services;

/// <summary>Точка восстановления через WMI SystemRestore. Windows может пропустить создание, если точка уже создавалась за последние 24 часа.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestorePointService : IRestorePointService
{
    private const int MODIFY_SETTINGS = 12, BEGIN_SYSTEM_CHANGE = 100;

    public string? TryCreate(string description, out string? error)
    {
        error = null;
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = MODIFY_SETTINGS;
            inParams["EventType"] = BEGIN_SYSTEM_CHANGE;
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var rc = Convert.ToUInt32(outParams["ReturnValue"]);
            if (rc == 0) return description;
            error = rc == 1058
                ? Loc.T("Защита системы выключена — точка восстановления не создана (журнал изменений Opti всё равно позволяет откат).")
                : Loc.T($"Точка восстановления не создана (код {rc}). Откат возможен через журнал Opti.");
            return null;
        }
        catch (Exception ex)
        {
            error = Loc.T($"Точка восстановления не создана: {ex.Message}. Откат возможен через журнал Opti.");
            return null;
        }
    }
}
