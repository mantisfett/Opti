using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Opti.Native;

/// <summary>Кто сидит за компьютером (пользователь текущего сеанса), в отличие от учётной записи, от имени которой запущен процесс.</summary>
[SupportedOSPlatform("windows")]
public static class SessionUser
{
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? Query(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    /// <summary>«ДОМЕН\Имя» пользователя сеанса или null.</summary>
    public static string? Current()
    {
        try
        {
            var user = Query(WTSUserName);
            if (string.IsNullOrEmpty(user)) return null;
            var domain = Query(WTSDomainName);
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }
        catch { return null; }
    }

    /// <summary>
    /// Предупреждение, если права администратора выданы через пароль другой учётной записи:
    /// тогда настройки «для пользователя» (HKCU) и задача продолжения относятся к администратору, а не к тому, кто за компьютером.
    /// </summary>
    public static string? MismatchWarning()
    {
        var session = Current();
        var process = Environment.UserDomainName + "\\" + Environment.UserName;
        if (session is null || string.Equals(session, process, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Opti запущен от имени «{process}», а за компьютером — «{session}». Настройки пользователя (мышь, Game Bar, игровой режим, твики) " +
               $"применятся к «{process}», и продолжение после перезагрузки сработает только при его входе. " +
               "Чтобы всё применилось к вам, войдите в Windows под учётной записью администратора.";
    }
}
