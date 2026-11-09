using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Opti.Fixes.Backends;

/// <summary>
/// Защита папки данных (C:\ProgramData\Opti) от подмены обычным пользователем.
///
/// Почему это важно: Opti работает с правами администратора и доверяет тому, что лежит в папке данных —
/// журнал отката (по нему пишутся реестр и BCD), settings.json (путь к PresentMon.exe, который запускается),
/// резервные копии файлов. По умолчанию в подпапках ProgramData обычный пользователь может создавать файлы
/// и папки, а также создать саму папку Opti до первого запуска. Поэтому при каждом старте:
///  1) папка, созданная не администратором, откладывается в карантин целиком;
///  2) на папку ставятся явные права без наследования: SYSTEM и Администраторы — полный доступ, Пользователи — чтение;
///  3) файлы, папки и ссылки (junction/symlink) внутри, владелец которых не администратор, переносятся в карантин.
/// Карантин не удаляется — его можно посмотреть и удалить вручную.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirSecurity
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Владельцы, которым можно доверять: система, администраторы и сам текущий (повышенный) пользователь.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner)
    {
        if (owner is null) return false;
        if (owner == LocalSystem || owner == Administrators || owner == TrustedInstaller) return true;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (IsElevated() && owner == id.User) return true;
        }
        catch { /* ниже — проверка по группе */ }
        // При выключенном UAC файлы получают владельцем саму учётную запись администратора, а не группу.
        return LocalAdminSids.Value.Contains(owner);
    }

    private static readonly Lazy<HashSet<SecurityIdentifier>> LocalAdminSids = new(ReadLocalAdmins);

    private static HashSet<SecurityIdentifier> ReadLocalAdmins()
    {
        var set = new HashSet<SecurityIdentifier>();
        try
        {
            // Имя группы локализовано («Администраторы»), поэтому берём его по SID.
            var name = ((NTAccount)Administrators.Translate(typeof(NTAccount))).Value;
            name = name[(name.IndexOf('\\') + 1)..];
            if (NetLocalGroupGetMembers(null, name, 0, out var buf, -1, out var read, out _, IntPtr.Zero) != 0) return set;
            try
            {
                for (int i = 0; i < read; i++)
                {
                    var psid = global::System.Runtime.InteropServices.Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                    set.Add(new SecurityIdentifier(psid));
                }
            }
            finally { _ = NetApiBufferFree(buf); }
        }
        catch { /* нет доступа — доверяем только группе и текущему пользователю */ }
        return set;
    }

    [global::System.Runtime.InteropServices.DllImport("netapi32.dll", CharSet = global::System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buf, int prefMaxLen,
        out int entriesRead, out int totalEntries, IntPtr resume);

    [global::System.Runtime.InteropServices.DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    public static SecurityIdentifier? GetOwner(FileSystemInfo item)
    {
        try
        {
            FileSystemSecurity sec = item is DirectoryInfo d ? d.GetAccessControl(AccessControlSections.Owner) : ((FileInfo)item).GetAccessControl(AccessControlSections.Owner);
            return sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch { return null; }
    }

    /// <summary>
    /// Готовит и защищает папку данных. Возвращает сообщения для пользователя (пусто — всё в порядке).
    /// Без прав администратора ничего не делает: такой процесс сам ничего важного не меняет.
    /// </summary>
    public static IReadOnlyList<string> Harden(string dataDir)
    {
        var notes = new List<string>();
        if (!IsElevated()) return notes;
        var full = Path.GetFullPath(dataDir).TrimEnd('\\');
        // На FAT/exFAT (флешка с OPTI_DATA_DIR) и сетевых папках прав доступа нет: проверка владельца там всегда
        // «не прошла бы» и отправляла бы в карантин всю папку. Такие папки не трогаем, а честно предупреждаем.
        if (!SupportsAcl(full))
        {
            notes.Add($"Папка данных {full} находится на диске без прав доступа (FAT/exFAT или сеть) — защитить её от подмены нельзя. " +
                      "Для обычной работы уберите переменную OPTI_DATA_DIR.");
            return notes;
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string QuarantineRoot() => full + ".untrusted-" + stamp;

        // 1. Сама папка: ссылка или чужой владелец — откладываем целиком.
        var dir = new DirectoryInfo(full);
        if (dir.Exists && (dir.LinkTarget is not null || !IsTrustedOwner(GetOwner(dir))))
        {
            var q = QuarantineRoot();
            Directory.Move(full, q);
            notes.Add($"Папка данных {full} была создана не администратором — она отложена в {q} и не используется. " +
                      "Если вы не создавали её сами, проверьте компьютер антивирусом.");
            dir = new DirectoryInfo(full);
        }

        // 2. Явные права без наследования. Владельца не меняем (он уже проверен), меняем только список доступа.
        dir.Create();
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);

        // 3. Подброшенное внутри: чужой владелец или ссылка — в карантин. По ссылкам не ходим.
        int moved = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            FileSystemInfo[] items;
            try { items = cur.GetFileSystemInfos(); }
            catch { continue; }
            foreach (var item in items)
            {
                bool isLink = item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isLink || !IsTrustedOwner(GetOwner(item)))
                {
                    var rel = Path.GetRelativePath(full, item.FullName);
                    var dest = Path.Combine(QuarantineRoot(), rel);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (item is DirectoryInfo) Directory.Move(item.FullName, dest);
                        else File.Move(item.FullName, dest);
                        moved++;
                    }
                    catch
                    {
                        // Не удалось перенести — удаляем ссылку (не цель) или файл: доверять ему нельзя.
                        try
                        {
                            if (item is DirectoryInfo d) Directory.Delete(d.FullName, recursive: !isLink);
                            else File.Delete(item.FullName);
                            moved++;
                        }
                        catch { notes.Add($"Не удалось убрать подозрительный объект {item.FullName}."); }
                    }
                    continue;
                }
                if (item is DirectoryInfo sub) stack.Push(sub);
            }
        }
        if (moved > 0)
            notes.Add($"В папке данных найдено {moved} объект(ов), созданных не администратором. Они перенесены в {QuarantineRoot()} и не используются.");
        return notes;
    }

    private static bool SupportsAcl(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var fs = new DriveInfo(root).DriveFormat;
            return fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Можно ли доверять папке, созданной прошлой версией (перенос данных GameTune).</summary>
    public static bool IsTrustedDirectory(string path)
    {
        var d = new DirectoryInfo(path);
        return d.Exists && d.LinkTarget is null && IsTrustedOwner(GetOwner(d));
    }
}
