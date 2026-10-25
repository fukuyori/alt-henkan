using AltHenkan;
using System.Reflection;

// Replay a missing-release condition using the existing fake input boundary.
// No global hook registration or native SendInput. This does not reproduce
// the external reason that real Ctrl/Alt up notifications went unobserved.
const BindingFlags P = BindingFlags.Instance | BindingFlags.NonPublic;
var fixture = typeof(ModifierReleaseTests);
var platformType = fixture.GetNestedType("Platform", BindingFlags.NonPublic)!;
var platform = Activator.CreateInstance(platformType, nonPublic: true)!;
using var service = (AltInputService)fixture.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
    .Invoke(null, [platform, true])!;
var serviceType = typeof(AltInputService);
var deliver = platformType.GetMethod("Deliver")!;
nint Key(uint vk, bool up) => (nint)deliver.Invoke(platform, [vk, up, (nuint)0, 0u])!;
object State(string name) => serviceType.GetField(name, P)!.GetValue(service)!;
bool Down(object state) => (bool)state.GetType().GetProperty("IsDown")!.GetValue(state)!;
var control = State("_leftControl");
var alt = State("_leftAlt");
var osDown = (HashSet<uint>)platformType.GetField("_down", P)!.GetValue(platform)!;
var sent = (List<(uint Key, bool Up)>)platformType.GetField("Sent")!.GetValue(platform)!;
var failed = false;
void Check(string name, bool value)
{
    Console.WriteLine($"{(value ? "PASS" : "FAIL")}: {name}");
    failed |= !value;
}
Key(NativeMethods.VkLControl, false);
Key(NativeMethods.VkLMenu, false);
Check("Ctrl is promoted and visible to the simulated OS", osDown.Contains(NativeMethods.VkLControl));
// Simulate OS state becoming released without corresponding hook notifications.
osDown.Clear();
var reconcile = serviceType.GetMethod("ReconcileReleasedAlt", P)!;
reconcile.Invoke(service, [alt, true, (nint)1, false, 1000L]);
reconcile.Invoke(service, [alt, true, (nint)1, false, 2000L]);
Check("Alt recovery clears Alt while Ctrl remains tracked", !Down(alt) && Down(control) && osDown.Count == 0);
var modifiers = (NavigationModifiers)serviceType.GetMethod("GetNavigationModifiers", P)!.Invoke(service, null)!;
Check("Stale Ctrl participates in Emacs despite OS Ctrl being up", (modifiers & NavigationModifiers.Control) != 0);
sent.Clear();
var result = Key((uint)Keys.F, false);
Key((uint)Keys.F, true);
Check("Ordinary F is suppressed and produces Right (Ctrl+F action)",
    result == 1 && sent.Contains(((uint)Keys.Right, false)) && sent.Contains(((uint)Keys.Right, true)));
Check("OS Ctrl remains up immediately after the Emacs action", !osDown.Contains(NativeMethods.VkLControl));
sent.Clear();
Key((uint)Keys.X, false);
Key((uint)Keys.X, true);
Check("Next ordinary key reinjects Ctrl down from stale internal state",
    sent.Contains((NativeMethods.VkLControl, false)) && osDown.Contains(NativeMethods.VkLControl));
Key(NativeMethods.VkLControl, true);
Check("Delivered Ctrl up clears the tracked and simulated OS states", !Down(control) && !osDown.Contains(NativeMethods.VkLControl));
Environment.ExitCode = failed ? 1 : 0;
