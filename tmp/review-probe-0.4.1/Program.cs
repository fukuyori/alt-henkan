using System.Reflection;
using System.Runtime.CompilerServices;
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var serviceType = assembly.GetType("AltHenkan.AltInputService", true)!;
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
var stateType = serviceType.GetNestedType("AltState", BindingFlags.NonPublic)!;
var sideType = serviceType.GetNestedType("AltSide", BindingFlags.NonPublic)!;
var dataType = assembly.GetType("AltHenkan.NativeMethods+KbdLlHookStruct", true)!;
object Call(object obj, string name, params object[] values) =>
    obj.GetType().GetMethod(name, flags)!.Invoke(obj, values)!;
void Set(object obj, string name, object value) => obj.GetType().GetProperty(name, flags)!.SetValue(obj, value);
object Get(object obj, string name) => obj.GetType().GetProperty(name, flags)!.GetValue(obj)!;
foreach (var side in new[] { "Left", "Right" })
{
    // No constructor: no native hooks, timer, settings, or SendInput are started.
    var service = RuntimeHelpers.GetUninitializedObject(serviceType);
    var state = Activator.CreateInstance(stateType, [Enum.Parse(sideType, side)])!;
    Call(state, "Start", (uint)0x38, side == "Right");
    Set(state, "NativeDownSent", true);
    var monitor = Get(state, "ReleaseMonitor");
    var sinceField = monitor.GetType().GetField("_releasedSince", flags)!;
    Call(service, "ReconcileReleasedAlt", state, true, (nint)1, false, (long)1000);
    var beforeTime = Get(state, "DownAtMilliseconds");
    var data = Activator.CreateInstance(dataType)!;
    dataType.GetField("VirtualKeyCode")!.SetValue(data, (uint)(side == "Left" ? 0xA4 : 0xA5));
    dataType.GetField("ScanCode")!.SetValue(data, (uint)0x38);
    var suppressed = (nint)Call(service, "HandleAltEvent", state, 0, (nint)0x104, (nint)0, data, true) == 1;
    Console.WriteLine($"{side}: newDownSuppressed={suppressed}, downTimestampUnchanged={Equals(beforeTime, Get(state, "DownAtMilliseconds"))}, releaseMonitorSinceAfterNewDown={sinceField.GetValue(monitor)}");
    Call(service, "ReconcileReleasedAlt", state, true, (nint)1, false, (long)2000);
    Console.WriteLine($"{side}: IsDownAfterSecondReleasedSample={Get(state,"IsDown")}, refreshRequested={serviceType.GetField("_refreshRequested",flags)!.GetValue(service)}");
    if (!suppressed || (bool)Get(state,"IsDown")) throw new Exception("Expected current behavior was not reproduced.");
}
Console.WriteLine("Synthetic state-transition reproduction completed. No Windows input was sent. No global hook was installed.");

