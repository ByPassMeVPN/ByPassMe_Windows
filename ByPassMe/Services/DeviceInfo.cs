using Microsoft.Win32;

namespace ByPassMe.Services;

public static class DeviceInfo
{
    public static string GetHwid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(guid)) return guid;
        }
        catch { /* ignore */ }

        return Environment.MachineName + "-" + Environment.UserName;
    }

    public static string GetDeviceName() => Environment.MachineName;
}
