namespace AltHenkan;

internal sealed partial class AltInputService
{
    // No polling, allocation or OS calls on the normal path. These fields are
    // diagnostic history, deliberately not cleared by a gesture's Reset().
    private struct ModifierReleaseAudit
    {
        public long DownVersion;
        public uint ReportedResidual;

        public long Begin(bool down) => down ? ++DownVersion : DownVersion;
    }

    private static uint ModifierResidual(bool down, bool passed, bool sent, bool consumed, bool shift = false) =>
        (down ? 1u : 0) | (passed ? 2u : 0) | (sent ? 4u : 0) | (consumed ? 8u : 0) | (shift ? 16u : 0);

    private void CheckModifierRelease(ref ModifierReleaseAudit audit, InputTraceKey key, uint residual,
        NativeMethods.KbdLlHookStruct data)
    {
        if (residual == 0)
        {
            audit.ReportedResidual = 0;
            return;
        }
        // Repeated unmatched ups with the same residual state must not flood logs.
        if (audit.ReportedResidual == residual) return;
        audit.ReportedResidual = residual;
        try
        {
            var sequence = Trace(new()
            {
                Kind = InputTraceKind.State, Reason = InputTraceReason.ModifierReleaseResidual,
                Key = key, Index = (int)residual, Up = true, Flags = data.Flags, EventTime = data.Time
            });
            DiagnosticLog.Write($"Modifier release residual: key={key}, residual=0x{residual:X}, " +
                $"eventTime={data.Time}, flags=0x{data.Flags:X}, downVersion={audit.DownVersion}, " +
                $"traceSession={InputTrace.Session:N}, traceSeq={sequence}, obs={_traceObservation}.");
        }
        catch { InputTrace.RecordFailure(); } // Diagnostics must not change input handling.
    }
}
