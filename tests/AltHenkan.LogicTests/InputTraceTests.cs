using AltHenkan;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class InputTraceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AltHenkan-InputTrace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            QueueTests(check);
            RecoveryTests(check);
            var rotationPath = Path.Combine(directory, "rotation.log");
            using (var rotating = new InputTraceWriter(() => new RotatingDiagnosticWriter(rotationPath, 2048, 2, false), Guid.NewGuid()))
            {
                for (var i = 0; i < 30; i++) rotating.TryWrite(new() { Kind = InputTraceKind.State });
                rotating.Complete();
                rotating.Completion.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            }
            var archives = Directory.GetFiles(directory, "rotation.log*");
            check("Buffered trace rotation retains bounded files", archives.Length == 3 && archives.All(p => new FileInfo(p).Length <= 2048));
            check("Every rotated file retains schema and clock context", archives.All(p =>
            {
                var header = File.ReadLines(p).First();
                return header.StartsWith("Input trace session=") && header.Contains("schema=2") &&
                    header.Contains("frequency=") && header.Contains("version=") && header.Contains("utc=");
            }));
            var path = Path.Combine(directory, "events.log");
            InputTrace.Initialize(path, Guid.NewGuid());
            var platform = new FakePlatform();
            using var service = new AltInputService(new AppSettings
            { Enabled = true, EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs }, platform);
            platform.Keyboard = typeof(AltInputService).GetMethod("KeyboardHookCallback", Private)!
                .CreateDelegate<NativeMethods.HookProc>(service);
            var mouse = typeof(AltInputService).GetMethod("MouseHookCallback", Private)!
                .CreateDelegate<NativeMethods.HookProc>(service);

            // Arbitrary ordinary keys, not a symptom-specific key, exercise the real promotion path.
            check("Ctrl is initially deferred", platform.Deliver(NativeMethods.VkLControl, false) == 1);
            platform.Deliver((uint)Keys.X, false);
            platform.Deliver((uint)Keys.X, true);
            platform.Deliver(NativeMethods.VkLControl, true);
            platform.Deliver(NativeMethods.VkRControl, false);
            platform.Deliver((uint)Keys.Z, false);
            platform.Deliver((uint)Keys.Z, true);
            platform.Deliver(NativeMethods.VkRControl, true);

            platform.Deliver(NativeMethods.VkLControl, false);
            platform.Deliver((uint)Keys.F, false);
            platform.Deliver((uint)Keys.F, true);
            platform.Deliver(NativeMethods.VkLControl, true);

            platform.Deliver(NativeMethods.VkLControl, false);
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MouseLlHookStruct>());
            try
            {
                Marshal.StructureToPtr(new NativeMethods.MouseLlHookStruct { Flags = 1, Time = 123 }, pointer, false);
                mouse(0, NativeMethods.WmLButtonDown, pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
            typeof(AltInputService).GetMethod("ResetActiveStates", Private)!.Invoke(service, [true, InputTraceReason.Disabled]);

            // Force physical up reentry inside SendInput. This is fault injection, not proof
            // that Windows produces this ordering. Both parent contexts must survive it.
            platform.Deliver(NativeMethods.VkLControl, false);
            platform.OnSend = () => platform.Deliver(NativeMethods.VkLControl, true);
            platform.Deliver((uint)Keys.X, false);
            platform.Deliver((uint)Keys.X, true);

            platform.NextResult = 42;
            check("A downstream hook's result is returned unchanged", platform.Deliver((uint)Keys.J, false) == 42);
            platform.NextResult = 0;
            platform.Deliver((uint)Keys.J, true);

            typeof(AltInputService).GetMethod("ResetActiveStates", Private)!.Invoke(service, [true, InputTraceReason.Renewal]);
            // Clear the fake OS state left by the deliberately out-of-order Ctrl sequence.
            platform.Deliver(NativeMethods.VkLControl, true);
            foreach (var alt in new[] { NativeMethods.VkLMenu, NativeMethods.VkRMenu })
            {
                platform.Deliver(alt, false);
                platform.Deliver((uint)Keys.X, false);
                platform.Deliver((uint)Keys.X, true);
                platform.Deliver(alt, true);
            }
            platform.Deliver((uint)Keys.Back, false, flags: 0x10);
            platform.Deliver((uint)Keys.Back, true, flags: 0x10);

            platform.OnSend = () => throw new IOException("Synthetic send fault");
            try { typeof(AltInputService).GetMethod("SendKeyboardInputs", Private)!.Invoke(service, [new[] { ControlInput(false) }]); }
            catch (TargetInvocationException e) when (e.InnerException is IOException) { }
            check("Failed sends restore depth and batch context",
                (int)typeof(AltInputService).GetField("_inputInjectionDepth", Private)!.GetValue(service)! == 0 &&
                (long)typeof(AltInputService).GetField("_traceBatch", Private)!.GetValue(service)! == 0);

            platform.Limit = 1;
            var inputs = new[] { ControlInput(false), ControlInput(true) };
            typeof(AltInputService).GetMethod("SendKeyboardInputs", Private)!.Invoke(service, [inputs]);
            platform.Limit = 0;
            typeof(AltInputService).GetMethod("SendKeyboardInputs", Private)!.Invoke(service, [inputs]);
            platform.Limit = null;
            platform.DeferOwnInput = true;
            typeof(AltInputService).GetMethod("SendKeyboardInputs", Private)!.Invoke(service, [inputs]);
            platform.DeferOwnInput = false;
            foreach (var deliver in platform.PendingOwnInput) deliver();
            platform.PendingOwnInput.Clear();
            platform.FailForeground = true;
            check("Metadata failure does not change input forwarding", platform.Deliver((uint)Keys.J, false) == 0);
            platform.FailForeground = false;
            platform.Deliver((uint)Keys.J, true);

            platform.Deliver(NativeMethods.VkLControl, true);
            platform.Deliver(NativeMethods.VkLControl, false);
            platform.OnSend = () => throw new IOException("Callback send fault");
            platform.NextResult = 42;
            check("Callback exceptions still forward input", platform.Deliver((uint)Keys.X, false) == 42);
            platform.NextResult = 0;
            platform.Deliver((uint)Keys.X, true);
            platform.Deliver(NativeMethods.VkLControl, true);

            // Exercise settings and lifecycle callbacks through the test dispatch path.
            service.UpdateSettings(new AppSettings { EmacsEnabled = false, EmacsControlSide = ModifierSideSelection.Right,
                LeftAltLongPressEnabled = false, LongPressMilliseconds = 777 });
            platform.Deliver(NativeMethods.VkLControl, false);
            platform.Deliver(NativeMethods.VkLControl, true);
            service.UpdateSettings(new AppSettings { EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs });
            platform.Deliver((uint)Keys.X, false);
            platform.Deliver(NativeMethods.VkLControl, false);
            platform.Deliver(NativeMethods.VkLControl, true);
            platform.Deliver((uint)Keys.X, true);
            typeof(AltInputService).GetMethod("OnPowerModeChanged", Private)!.Invoke(service,
                [service, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)]);
            typeof(AltInputService).GetMethod("OnSessionSwitch", Private)!.Invoke(service,
                [service, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionLock)]);
            typeof(AltInputService).GetMethod("CleanupHookThread", Private)!.Invoke(service, null);

            // Persistence must happen while the service is alive, with no maintenance timer.
            var deadline = Environment.TickCount64 + 4000;
            string text = "";
            do
            {
                Thread.Sleep(25);
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
            } while (!text.Contains("requested=2 sent=0 error=5") && Environment.TickCount64 < deadline);
            check("Trace persists without shutdown or the hook-thread timer", text.Contains("requested=2 sent=0 error=5"));
            InputTrace.Shutdown();
            text = File.ReadAllText(path);
            var lines = text.Split('\n').Where(l => l.StartsWith("seq=")).ToArray();
            check("Both Ctrl sides have begin/end observations", lines.Any(l => l.Contains("kind=KeyBegin") && l.Contains("key=LeftControl")) &&
                lines.Any(l => l.Contains("kind=KeyEnd") && l.Contains("key=RightControl")));
            check("General input promotes Ctrl with before and after states", lines.Any(l => l.Contains("kind=State reason=PromoteControl") && l.Contains("index=0")) &&
                lines.Any(l => l.Contains("kind=State reason=PromoteControl") && l.Contains("index=1")));
            var promotion = lines.Where(l => l.Contains("kind=State reason=PromoteControl")).Take(2).ToArray();
            check("Promotion captures the actual hidden-to-sent Ctrl state", promotion.Length == 2 &&
                (State(promotion[0]) & 15) == 1 && (State(promotion[1]) & 15) == 5);
            check("Forwarding records cleared Ctrl state after release", lines.Any(l => l.Contains("kind=KeyEnd reason=ControlSentUp") &&
                (State(l) & 15) == 0));
            check("Emacs operations and consumption are recorded", text.Contains("reason=ConsumeControl") &&
                text.Contains("kind=SendBegin reason=Navigation") && text.Contains("verdict=Suppressed"));
            check("Mouse promotion has an observation boundary", text.Contains("kind=MouseBegin") && text.Contains("kind=MouseEnd"));
            check("Reset records the pre-reset boundary and completion", text.Contains("kind=Boundary reason=Disabled") &&
                text.IndexOf("reason=ResetBegin") < text.IndexOf("reason=ResetEnd"));
            check("Downstream suppression is not attributed to this application",
                lines.Any(l => l.Contains("kind=KeyEnd") && l.Contains("verdict=Forwarded result=42")));
            check("Exception fallback records its result under the original observation", lines.Any(l =>
                l.Contains("kind=Fallback") && l.Contains("verdict=Forwarded result=42") && Number(l, "obs") != 0));
            check("Native Ctrl decision reasons are distinguished", text.Contains("reason=ControlNativeNotDeferrable") &&
                lines.Any(l => l.Contains("reason=ControlNativeOtherInput") && Number(l, "nonAltKeys") > 0));
            check("Settings changes include policy values", text.Contains("kind=Settings") && text.Contains("longPressMs=777") && text.Contains("shortcuts=0x"));
            check("Suspend, lock, and post-unhook shutdown observations are present", text.Contains("reason=Suspend") &&
                text.Contains("reason=SessionLock") && text.Contains("reason=Unregistered"));
            check("Status events carry reason and callback counters", lines.Any(l => l.Contains("kind=Status reason=Shutdown") && Number(l, "keyboardCallbacks") > 0));
            check("Partial and zero sends preserve exact counts", text.Contains("requested=2 sent=1 error=5") && text.Contains("requested=2 sent=0 error=5"));
            check("Exceptional sends are distinct from zero sends", lines.Any(l => l.Contains("kind=SendEnd") &&
                Number(l, "index") == 0 && Number(l, "requested") == 1));
            check("Both Alt sides and their promotion are observed", text.Contains("key=LeftAlt") && text.Contains("key=RightAlt") && text.Contains("reason=PromoteAlt"));
            check("Foreign injected input remains distinct from own input", lines.Any(l => l.Contains("kind=KeyBegin") &&
                l.Contains("key=Other") && l.Contains("flags=0x10 own=False")));
            check("Observed input never exposes generated-input code fields", lines.Where(l => l.Contains("kind=KeyBegin") || l.Contains("kind=KeyEnd"))
                .All(l => !l.Contains("sentScan=") && !l.Contains("sentVk=") && !l.Contains("scan=") && !l.Contains("vk=")));
            check("Ordinary keys share a category regardless of key code", new[] { Keys.X, Keys.Z, Keys.Back, Keys.Enter, Keys.Left }
                .All(k => InputTrace.Classify((uint)k, 0) == InputTraceKey.Other));
            check("Send items do not sample async state", lines.Where(l => l.Contains("kind=SendItem")).All(l => !l.Contains("asyncRaw=")));
            check("Nested sends preserve parent batch identifiers", lines.Any(l => l.Contains("kind=SendBegin") && Number(l, "parentBatch") != 0));
            check("Nested callbacks preserve parent observation identifiers", lines.Any(l => l.Contains("kind=KeyBegin") && Number(l, "parent") != 0));
            if (Environment.Is64BitProcess)
            {
                var sentItems = lines.Where(l => l.Contains("kind=SendItem")).ToArray();
                var observed = lines.Where(l => l.Contains("kind=KeyBegin") && l.Contains("own=True")).ToArray();
                check("Each sent item has a distinct correlation token", sentItems.Select(l => Number(l, "token")).Distinct().Count() == sentItems.Length);
                check("Own observations link to sent items without a FIFO guess", observed.All(l => Number(l, "token") != 0 &&
                    sentItems.Any(s => Number(s, "token") == Number(l, "token"))));
                check("Own input is observed between send begin and completion", sentItems.Any(item =>
                {
                    var batch = Number(item, "batch");
                    var token = Number(item, "token");
                    var begin = lines.First(l => l.Contains("kind=SendBegin") && Number(l, "batch") == batch);
                    var end = lines.First(l => l.Contains("kind=SendEnd") && Number(l, "batch") == batch);
                    return observed.Any(l => Number(l, "token") == token && Number(begin, "seq") < Number(l, "seq") && Number(l, "seq") < Number(end, "seq"));
                }));
                check("Delayed own input still correlates after send completion", observed.Any(observation =>
                {
                    var item = sentItems.Single(s => Number(s, "token") == Number(observation, "token"));
                    var end = lines.First(l => l.Contains("kind=SendEnd") && Number(l, "batch") == Number(item, "batch"));
                    return Number(observation, "seq") > Number(end, "seq") && Number(observation, "batch") == 0;
                }));
            }
            check("Shutdown drains accepted records", text.Contains("Input trace ended:") && text.Contains("droppedTotal=0"));
            check("Only the injected metadata failure is counted", InputTrace.RecordFailures == 1);
            MarkerTests(check);
            EquivalenceTests(directory, check);
        }
        finally
        {
            InputTrace.Shutdown();
            // Resolve and verify our unique test directory before recursive removal.
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("AltHenkan-InputTrace-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static long Number(string line, string field) => long.Parse(
        line.Split(' ').First(x => x.StartsWith(field + "=", StringComparison.Ordinal)).Split('=')[1]);

    private static uint State(string line) => Convert.ToUInt32(line.Split(' ').First(x => x.StartsWith("state="))[8..], 16);

    private static NativeMethods.Input ControlInput(bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput
        { ScanCode = 0x1D, Flags = NativeMethods.KeyeventfScanCode | (up ? NativeMethods.KeyeventfKeyUp : 0) } }
    };

    private static void QueueTests(Action<string, bool> check)
    {
        using var gate = new ManualResetEventSlim();
        var output = new StringWriter();
        using var writer = new InputTraceWriter(() => { gate.Wait(); return output; }, Guid.NewGuid(), 2);
        var value = new InputTraceEvent { Kind = InputTraceKind.State };
        for (var i = 0; i < 7; i++) writer.TryWrite(value);
        check("Full trace queue drops without waiting for its blocked consumer", writer.Dropped == 5);
        writer.Complete();
        gate.Set();
        writer.Completion.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        check("Trailing loss is reported even without a following event", output.ToString().Contains("droppedTotal=5") && output.ToString().Contains("latestSeq=7"));
        using var broken = new InputTraceWriter(() => throw new IOException("test failure"), Guid.NewGuid());
        check("Trace sink failure is observed", SpinWait.SpinUntil(() => broken.LastError is not null, 4000));
        check("Trace disk failure does not propagate to a producer", broken.LastError is IOException && !broken.TryWrite(value));
        broken.Stop(TimeSpan.FromSeconds(4));
    }

    private static void RecoveryTests(Action<string, bool> check)
    {
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var attempts = 0;
        using var reopen = new ManualResetEventSlim();
        var output = new StringWriter();
        using var writer = new InputTraceWriter(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return new FailingWriter();
            reopen.Wait();
            return output;
        }, Guid.NewGuid(), report: reports.Enqueue, retryDelay: TimeSpan.FromMilliseconds(10));
        writer.TryWrite(new() { Kind = InputTraceKind.State });
        check("Mid-write failure suspends expensive observation", SpinWait.SpinUntil(() => !writer.Accepting, 4000));
        check("Failure is reported with its message", SpinWait.SpinUntil(() => reports.Any(r => r.Contains("test mid-write failure")), 4000));
        reopen.Set();
        check("Writer resumes after reopening", SpinWait.SpinUntil(() => writer.Accepting && writer.LastError is null, 4000));
        writer.TryWrite(new() { Kind = InputTraceKind.Boundary, Reason = InputTraceReason.Shutdown });
        check("Recovered writer drains", writer.Stop(TimeSpan.FromSeconds(4)));
        check("Outage is explicitly preserved", output.ToString().Contains("Trace outage:") &&
            output.ToString().Contains("observationPaused=True") && output.ToString().Contains("reason=Shutdown"));
        check("Dequeued failed writes count as missing", writer.Dropped == 1);

        using var gate = new ManualResetEventSlim();
        using var blocked = new InputTraceWriter(() => { gate.Wait(); return new StringWriter(); }, Guid.NewGuid(), report: reports.Enqueue);
        try
        {
            blocked.TryWrite(new() { Kind = InputTraceKind.State });
            check("Shutdown timeout is reported", !blocked.Stop(TimeSpan.Zero) && reports.Any(r => r.Contains("drain timed out")));
        }
        finally { gate.Set(); }
        check("Timed-out writer can finish draining", blocked.Stop(TimeSpan.FromSeconds(4)));
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value?.StartsWith("seq=") == true
            ? throw new IOException("test mid-write failure") : base.WriteLineAsync(value);
    }

    private static void MarkerTests(Action<string, bool> check)
    {
        using var service = new AltInputService(new AppSettings(), new FakePlatform());
        var marker = (nuint)typeof(AltInputService).GetMethod("NextInputMarker", Private)!.Invoke(service, null)!;
        check("New marker preserves the old low 32 bits", unchecked((uint)marker) == 0x414C5448);
        check("Truncated marker still bypasses input transformation", AltInputService.IsOwnInput(unchecked((nuint)(uint)marker)) &&
            AltInputService.TraceToken(unchecked((nuint)(uint)marker)) == 0);
        check("Full marker retains correlation token", !Environment.Is64BitProcess || AltInputService.TraceToken(marker) == 1);
        check("Foreign marker is not our input", !AltInputService.IsOwnInput(0x12345678));
    }

    // Explicit opt-in: two F24 events, intercepted by our test hook before forwarding.
    // This verifies transport of ExtraInfo; it does not reproduce the reported fault.
    public static void RunNativeMarker(Action<string, bool> check)
    {
        using var service = new AltInputService(new AppSettings(), new FakePlatform());
        var next = typeof(AltInputService).GetMethod("NextInputMarker", Private)!;
        var downMarker = (nuint)next.Invoke(service, null)!;
        var upMarker = (nuint)next.Invoke(service, null)!;
        var observations = new System.Collections.Concurrent.ConcurrentQueue<nuint>();
        nint hook = 0;
        NativeMethods.HookProc callback = (code, message, pointer) =>
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(pointer);
                if (data.VirtualKeyCode == (uint)Keys.F24 && (data.Flags & 0x10) != 0 &&
                    AltInputService.IsOwnInput(data.ExtraInfo))
                {
                    observations.Enqueue(data.ExtraInfo);
                    return 1;
                }
            }
            return NativeMethods.CallNextHookEx(hook, code, message, pointer);
        };
        using (var loop = new DedicatedMessageLoop(() =>
        {
            hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, callback, NativeMethods.GetModuleHandle(null), 0);
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }, () => { if (hook != 0) NativeMethods.UnhookWindowsHookEx(hook); }))
        {
            NativeMethods.Input Stroke(bool up, nuint marker) => new()
            {
                Type = NativeMethods.InputKeyboard,
                Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput
                { VirtualKey = (ushort)Keys.F24, Flags = up ? NativeMethods.KeyeventfKeyUp : 0, ExtraInfo = marker } }
            };
            var sent = NativeMethods.SendKeyboardInputsDetailed([Stroke(false, downMarker), Stroke(true, upMarker)]);
            check("Native marker probe accepts both test events", sent.Sent == 2);
            check("Native marker probe observes both events", SpinWait.SpinUntil(() => observations.Count == 2, 4000));
            check("Windows preserves both full input markers", observations.SequenceEqual(new[] { downMarker, upMarker }));
            check("Native markers preserve correlation", observations.All(m => AltInputService.IsOwnInput(m) &&
                (!Environment.Is64BitProcess || AltInputService.TraceToken(m) != 0)));
        }
        GC.KeepAlive(callback);
    }

    private static void EquivalenceTests(string directory, Action<string, bool> check)
    {
        string Scenario(bool trace)
        {
            if (trace) InputTrace.Initialize(Path.Combine(directory, "equivalence.log"), Guid.NewGuid());
            var platform = new FakePlatform();
            using var service = new AltInputService(new AppSettings { EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs }, platform);
            platform.Keyboard = typeof(AltInputService).GetMethod("KeyboardHookCallback", Private)!.CreateDelegate<NativeMethods.HookProc>(service);
            var results = new List<nint>();
            foreach (var ordinary in new[] { Keys.X, Keys.F, Keys.Back })
            {
                results.Add(platform.Deliver(NativeMethods.VkLControl, false));
                results.Add(platform.Deliver((uint)ordinary, false));
                results.Add(platform.Deliver((uint)ordinary, true));
                results.Add(platform.Deliver(NativeMethods.VkLControl, true));
            }
            results.Add(platform.Deliver(NativeMethods.VkRMenu, false));
            results.Add(platform.Deliver((uint)Keys.X, false));
            results.Add(platform.Deliver((uint)Keys.X, true));
            results.Add(platform.Deliver(NativeMethods.VkRMenu, true));
            if (trace) InputTrace.Shutdown();
            return string.Join(',', results) + "|" + string.Join(';', platform.Sent);
        }
        check("Trace on/off preserves decisions and generated input", Scenario(false) == Scenario(true));
    }

    private sealed class FakePlatform : InputPlatform
    {
        public NativeMethods.HookProc Keyboard = null!;
        public Action? OnSend;
        public uint? Limit;
        public nint NextResult;
        public bool FailForeground;
        public readonly List<string> Sent = [];
        public bool DeferOwnInput;
        public readonly List<Action> PendingOwnInput = [];
        private readonly HashSet<int> _down = [];
        public override bool IsDown(int key) => _down.Contains(key) ||
            key == NativeMethods.VkControl && (_down.Contains((int)NativeMethods.VkLControl) || _down.Contains((int)NativeMethods.VkRControl));
        public override InputForeground Foreground() => FailForeground ? throw new IOException("Metadata fault") : new(1, 2, 3);
        public override nint NextKeyboard(nint hook, int code, nint wParam, nint lParam) => NextResult;
        public override nint NextMouse(nint hook, int code, nint wParam, nint lParam) => 0;
        public nint Deliver(uint key, bool up, nuint marker = 0, uint flags = 0)
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KbdLlHookStruct>());
            try
            {
                var extended = key is NativeMethods.VkRControl or NativeMethods.VkRMenu;
                Marshal.StructureToPtr(new NativeMethods.KbdLlHookStruct
                {
                    VirtualKeyCode = key, ScanCode = key is NativeMethods.VkLControl or NativeMethods.VkRControl ? 0x1Du : 0x38u,
                    ExtraInfo = marker, Flags = flags | (extended ? NativeMethods.LlkhfExtended : 0) | (up ? 0x80u : 0), Time = 123
                }, pointer, false);
                var result = Keyboard(0, up ? NativeMethods.WmKeyUp : NativeMethods.WmKeyDown, pointer);
                if (result == 0) { if (up) _down.Remove((int)key); else _down.Add((int)key); }
                return result;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public override InputSendResult Send(NativeMethods.Input[] inputs)
        {
            var callback = OnSend;
            OnSend = null;
            callback?.Invoke();
            var count = Math.Min(Limit ?? (uint)inputs.Length, (uint)inputs.Length);
            foreach (var input in inputs.Take((int)count))
            {
                var key = input.Data.Keyboard;
                Sent.Add($"{key.VirtualKey}/{key.ScanCode}/{key.Flags}/{key.ExtraInfo}");
                var vk = key.VirtualKey != 0 ? key.VirtualKey : key.ScanCode == 0x1D
                    ? ((key.Flags & NativeMethods.KeyeventfExtendedKey) != 0 ? NativeMethods.VkRControl : NativeMethods.VkLControl)
                    : key.ScanCode == 0x38
                        ? ((key.Flags & NativeMethods.KeyeventfExtendedKey) != 0 ? NativeMethods.VkRMenu : NativeMethods.VkLMenu) : 0u;
                void Observe() => Deliver(vk, (key.Flags & NativeMethods.KeyeventfKeyUp) != 0, key.ExtraInfo, 0x10);
                if (DeferOwnInput) PendingOwnInput.Add(Observe);
                else Observe();
            }
            return new(count, count == inputs.Length ? 0 : 5);
        }
    }
}
