using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

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

            var eraser = new DiscEraserClass
            {
                Drive = drive.RawDrive
            };

            eraser.OnProgress += (percent, timeRemaining) =>
            {
                progress?.Report(new EraseProgress(
                    percent,
                    TimeSpan.FromSeconds(timeRemaining),
                    fullErase ? $"Performing secure erase... {percent}%" : $"Performing quick erase... {percent}%"));
            };

            progress?.Report(new EraseProgress(0, TimeSpan.Zero, "Starting erase..."));
            eraser.Erase(fullErase);

            if (ejectAfterErase)
            {
                try { drive.RawDrive.Eject(); } catch (COMException) { }
            }

            progress?.Report(new EraseProgress(100, TimeSpan.Zero, "Disc erased successfully."));
        }, ct);
    }
}
