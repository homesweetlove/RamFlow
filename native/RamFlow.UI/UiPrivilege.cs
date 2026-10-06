using System.Security.Principal;

namespace RamFlow.UI;

internal static class UiPrivilege
{
    public static bool IsAdministrator { get; } = ReadPrivilege();

    private static bool ReadPrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
