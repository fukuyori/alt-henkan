using AltHenkan;
using System.Runtime.InteropServices;

var failures = new List<string>();

Check(
    "Short press sends conversion",
    AltPressAction.Conversion,
    AltPressDecision.Decide(599, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Threshold duration sends native Alt",
    AltPressAction.NativeAlt,
    AltPressDecision.Decide(600, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Long press sends native Alt",
    AltPressAction.NativeAlt,
    AltPressDecision.Decide(1200, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Disabled long press still sends conversion",
    AltPressAction.Conversion,
    AltPressDecision.Decide(1200, longPressEnabled: false, longPressMilliseconds: 600));

var normalizedLow = new AppSettings { LongPressMilliseconds = 1 }.Normalize();
Check(
    "Threshold is clamped to minimum",
    AppSettings.MinimumLongPressMilliseconds,
    normalizedLow.LongPressMilliseconds);

var normalizedHigh = new AppSettings { LongPressMilliseconds = 9999 }.Normalize();
Check(
    "Threshold is clamped to maximum",
    AppSettings.MaximumLongPressMilliseconds,
    normalizedHigh.LongPressMilliseconds);

Check(
    "Right Alt sends VK_IME_ON",
    (ushort)0x16,
    JapaneseImeKey.ImeOnVirtualKey);
Check(
    "Left Alt sends VK_IME_OFF",
    (ushort)0x1A,
    JapaneseImeKey.ImeOffVirtualKey);
Check(
    "Native INPUT has the Windows ABI size",
    IntPtr.Size == 8 ? 40 : 28,
    Marshal.SizeOf<NativeMethods.Input>());

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("All 9 logic tests passed.");
return 0;

void Check<T>(string name, T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, actual {actual}");
    }
}
