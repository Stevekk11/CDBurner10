using System.Runtime.InteropServices;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public sealed class DiscEraserService
{
    public Task EraseAsync(
        DriveModel drive,
        bool fullErase = false,
        bool ejectAfterErase = true,
        IProgress<EraseProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var discInfo = drive.GetDiscInfo(refresh: true);
            if (!discInfo.HasDisc)
                throw new InvalidOperationException("No disc loaded in drive.");
            if (!discInfo.IsErasable)
                throw new InvalidOperationException($"Disc type '{discInfo.DiscTypeDescription}' is not erasable.");

            IDiscRecorder2? recorder = null;
            IDiscFormat2Erase? eraser = null;
            try
            {
                recorder = drive.OpenRecorder();
                eraser = Imapi2Interop.CreateDiscFormat2Erase();
                eraser.put_ClientName("CDBurner10");
                eraser.put_Recorder(recorder);
                eraser.put_FullErase(fullErase);

                var sink = new DiscFormat2EraseEventSink((elapsed, estimatedTotal) =>
                {
                    int pct = estimatedTotal > 0
                        ? (int)Math.Clamp(elapsed * 100.0 / estimatedTotal, 0, 99)
                        : 0;
                    int rem = Math.Max(0, estimatedTotal - elapsed);
                    progress?.Report(new EraseProgress(
                        pct,
                        TimeSpan.FromSeconds(rem),
                        fullErase ? $"Performing secure erase... {pct}%" : $"Performing quick erase... {pct}%"));
                });

                using var sub = Imapi2Interop.AdviseEventSink(
                    eraser,
                    typeof(DDiscFormat2EraseEvents).GUID,
                    sink);

                progress?.Report(new EraseProgress(0, TimeSpan.Zero, "Starting erase..."));
                eraser.EraseMedia();
            }
            finally
            {
                Imapi2Interop.SafeRelease(eraser);
                Imapi2Interop.SafeRelease(recorder);
            }

            if (ejectAfterErase)
            {
                try { drive.Eject(); } catch { }
            }

            progress?.Report(new EraseProgress(100, TimeSpan.Zero, "Disc erased successfully."));
        }, ct);
    }
}
