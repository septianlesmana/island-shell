using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland;

/// Apps currently using the microphone / camera (read from the Windows privacy records,
/// the same source as the taskbar microphone/camera icons).
static class PrivacyIndicators
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static (List<string> Mic, List<string> Cam) Read() => (InUse("microphone"), InUse("webcam"));

    static List<string> InUse(string capability)
    {
        var list = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root == null) return list;
            Scan(root, list, nonPackaged: false);
            using var np = root.OpenSubKey("NonPackaged");
            if (np != null) Scan(np, list, nonPackaged: true);
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Scan(RegistryKey key, List<string> list, bool nonPackaged)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var k = key.OpenSubKey(name);
            if (k == null) continue;
            // In use: started (Start > 0) and not stopped yet (Stop = 0)
            if (k.GetValue("LastUsedTimeStart") is long start && start > 0 &&
                k.GetValue("LastUsedTimeStop") is long stop && stop == 0)
            {
                list.Add(nonPackaged
                    ? Path.GetFileNameWithoutExtension(name.Replace('#', '\\'))
                    : name.Split('_')[0].Split('.').Last());
            }
        }
    }
}

/// Windows Focus / Do Not Disturb. Windows has no official API; the state is read & changed via the
/// "WNF state" also used by the Windows Focus Assist button. If that fails, the caller opens
/// the Windows Notification Center (which has a Do not disturb button).
static class FocusAssist
{
    const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    /// true = Focus on, false = off, null = unreadable.
    public static bool? IsOn()
    {
        try
        {
            ulong state = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            int value = 0;
            uint size = 4;
            return NtQueryWnfStateData(ref state, IntPtr.Zero, IntPtr.Zero, out _, ref value, ref size) == 0
                ? value != 0 : null;
        }
        catch { return null; }
    }

    /// Turn on (priority only) / off. True if Windows accepted the change.
    public static bool Set(bool on)
    {
        try
        {
            ulong state = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            int value = on ? 1 : 0;
            return NtUpdateWnfStateData(ref state, ref value, 4, IntPtr.Zero, IntPtr.Zero, 0, 0) == 0;
        }
        catch { return false; }
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
                                          out uint changeStamp, ref int buffer, ref uint bufferSize);

    [DllImport("ntdll.dll")]
    static extern int NtUpdateWnfStateData(ref ulong stateName, ref int buffer, int length, IntPtr typeId,
                                           IntPtr explicitScope, uint matchingChangeStamp, uint checkStamp);
}
