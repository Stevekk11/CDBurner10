using System.Diagnostics;
using System.Threading.Channels;
using CDBurner10.Core.Dvd;

namespace CDBurner10.Core.Burning;

public sealed record DiscMovieItem(
    string FullPath,
    string RelativePath,
    string FileName,
    string Subfolder,
    long SizeBytes,
    string Extension,
    IReadOnlyList<string> SidecarSubtitles);

public sealed record MovieCopyProgress(
    int CurrentFileIndex,
    int TotalFiles,
    string CurrentFileName,
    long BytesCopied,
    long TotalBytes,
    int Percent,
    string Message);

public sealed record MovieCopyResult(
    int FilesCopied,
    int SubtitlesCopied,
    long TotalBytesCopied,
    string DestinationFolder,
    IReadOnlyList<string> FailedFiles,
    int FilesConverted = 0);

public sealed class DiscMovieCopier
{
    public static readonly HashSet<string> MovieExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mkv", ".mp4", ".m4v", ".mov", ".wmv",
        ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".vob",
        ".flv", ".webm", ".divx", ".xvid", ".3gp", ".ogm",
        ".asf", ".rm", ".rmvb", ".f4v"
    };

    public static readonly HashSet<string> NativeMp4Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v"
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt"
    };

    public static bool IsMovieFile(string path) =>
        MovieExtensions.Contains(Path.GetExtension(path));

    public static bool IsLegacyFormat(string path)
    {
        string ext = Path.GetExtension(path);
        return MovieExtensions.Contains(ext) && !NativeMp4Extensions.Contains(ext);
    }

    /// <summary>
    /// Recursively scans <paramref name="rootPath"/> (including all subfolders) for movie files,
    /// skipping inaccessible directories gracefully.
    /// </summary>
    public static List<DiscMovieItem> ScanDirectory(string rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return [];

        string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var results = new List<DiscMovieItem>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string currentDir = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            // Index subtitle files in this folder by base filename for fast sidecar matching
            Dictionary<string, List<string>>? subtitlesByStem = null;
            foreach (var file in files)
            {
                if (!SubtitleExtensions.Contains(Path.GetExtension(file)))
                    continue;

                string stem = Path.GetFileNameWithoutExtension(file);
                subtitlesByStem ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (!subtitlesByStem.TryGetValue(stem, out var list))
                {
                    list = [];
                    subtitlesByStem[stem] = list;
                }
                list.Add(file);
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(file);
                if (!MovieExtensions.Contains(ext))
                    continue;

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(normalizedRoot, file);
                string? relDir = Path.GetDirectoryName(relPath);
                string subfolder = string.IsNullOrEmpty(relDir) ? string.Empty : relDir;
                string stem = Path.GetFileNameWithoutExtension(file);

                IReadOnlyList<string> sidecars = Array.Empty<string>();
                if (subtitlesByStem is not null)
                {
                    var matched = new List<string>();
                    foreach (var kvp in subtitlesByStem)
                    {
                        // Match exact stem (e.g. Movie.srt) or language-tagged stem (e.g. Movie.cs.srt)
                        if (kvp.Key.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                            kvp.Key.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                        {
                            matched.AddRange(kvp.Value);
                        }
                    }
                    if (matched.Count > 0)
                        sidecars = matched;
                }

                results.Add(new DiscMovieItem(
                    FullPath: file,
                    RelativePath: relPath,
                    FileName: Path.GetFileName(file),
                    Subfolder: subfolder,
                    SizeBytes: length,
                    Extension: ext.ToLowerInvariant(),
                    SidecarSubtitles: sidecars));
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subdir in subdirs)
                pending.Push(subdir);
        }

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return results;
    }

    public static Task<MovieCopyResult> CopyMoviesAsync(
        IReadOnlyList<DiscMovieItem> items,
        string destinationFolder,
        bool preserveSubfolders = false,
        bool includeSubtitles = true,
        bool overwriteExisting = false,
        IProgress<MovieCopyProgress>? progress = null,
        CancellationToken ct = default,
        bool convertLegacyToMp4 = false)
    {
        return Task.Run(async () =>
        {
            if (items.Count == 0)
                return new MovieCopyResult(0, 0, 0, destinationFolder, Array.Empty<string>(), 0);

            string? ffmpegPath = null;
            if (convertLegacyToMp4 && items.Any(i => IsLegacyFormat(i.FileName)))
            {
                ffmpegPath = DvdVideoConverter.FindFfmpeg()
                    ?? throw new InvalidOperationException(DvdVideoConverter.InstallHint);
            }

            Directory.CreateDirectory(destinationFolder);

            long totalBytes = items.Sum(i => Math.Max(0L, i.SizeBytes));
            long bytesCopied = 0;
            int filesCopied = 0;
            int subtitlesCopied = 0;
            int filesConverted = 0;
            var failedFiles = new List<string>();
            object failedLock = new();
            var buffer = new byte[1024 * 1024]; // 1 MB buffer for optical streaming
            var sw = Stopwatch.StartNew();
            long lastReportMs = -1000;
            string? activeConversionName = null;

            // Pipeline: while the optical drive reads the next movie, FFmpeg converts the previous one in parallel
            // so the user doesn't wait twice.
            Channel<(string SourcePath, string OutputMp4Path, string DisplayName)>? convertChannel = null;
            Task? convertWorker = null;
            if (ffmpegPath is not null)
            {
                convertChannel = Channel.CreateUnbounded<(string, string, string)>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                string ffmpeg = ffmpegPath;
                convertWorker = Task.Run(async () =>
                {
                    await foreach (var job in convertChannel.Reader.ReadAllAsync(ct))
                    {
                        Volatile.Write(ref activeConversionName, job.DisplayName);
                        bool ok = await ConvertToMp4Async(ffmpeg, job.SourcePath, job.OutputMp4Path, ct);
                        if (ok)
                        {
                            Interlocked.Increment(ref filesConverted);
                            try { if (File.Exists(job.SourcePath)) File.Delete(job.SourcePath); } catch { }
                        }
                        else
                        {
                            try { if (File.Exists(job.OutputMp4Path)) File.Delete(job.OutputMp4Path); } catch { }
                            lock (failedLock)
                                failedFiles.Add($"{job.DisplayName}: MP4 conversion failed (original file kept)");
                        }
                    }
                    Volatile.Write(ref activeConversionName, null);
                }, ct);
            }

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[i];

                    string targetDir = preserveSubfolders && !string.IsNullOrEmpty(item.Subfolder)
                        ? Path.Combine(destinationFolder, item.Subfolder)
                        : destinationFolder;
                    Directory.CreateDirectory(targetDir);

                    string targetPath = ResolveTargetPath(targetDir, item.FileName, overwriteExisting);
                    string targetStem = Path.GetFileNameWithoutExtension(targetPath);

                    ReportProgress(i + 1, items.Count, item.FileName, bytesCopied, totalBytes, force: true);

                    bool copiedOk = false;
                    try
                    {
                        using var src = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
                        using var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);

                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            dst.Write(buffer, 0, read);
                            bytesCopied += read;

                            if (sw.ElapsedMilliseconds - lastReportMs >= 200)
                            {
                                ReportProgress(i + 1, items.Count, item.FileName, bytesCopied, totalBytes, force: false);
                            }
                        }

                        copiedOk = true;
                        filesCopied++;
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                        throw;
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                        lock (failedLock)
                            failedFiles.Add($"{item.RelativePath}: {ex.Message}");
                    }

                    if (copiedOk && includeSubtitles && item.SidecarSubtitles.Count > 0)
                    {
                        string origStem = Path.GetFileNameWithoutExtension(item.FileName);
                        foreach (var subPath in item.SidecarSubtitles)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                string subName = Path.GetFileName(subPath);
                                // If the movie was disambiguated (e.g. Movie (2).avi), rename matching subtitle too
                                if (!targetStem.Equals(origStem, StringComparison.OrdinalIgnoreCase) &&
                                    subName.StartsWith(origStem, StringComparison.OrdinalIgnoreCase))
                                {
                                    subName = targetStem + subName[origStem.Length..];
                                }

                                string subTarget = ResolveTargetPath(targetDir, subName, overwriteExisting);
                                File.Copy(subPath, subTarget, overwrite: true);
                                subtitlesCopied++;
                            }
                            catch
                            {
                                // Best-effort subtitle copy
                            }
                        }
                    }

                    if (copiedOk && convertChannel is not null && IsLegacyFormat(targetPath))
                    {
                        string mp4Target = ResolveTargetPath(targetDir, targetStem + ".mp4", overwriteExisting);
                        convertChannel.Writer.TryWrite((targetPath, mp4Target, item.FileName));
                    }
                }
            }
            finally
            {
                convertChannel?.Writer.TryComplete();
            }

            if (convertWorker is not null)
            {
                if (!convertWorker.IsCompleted)
                {
                    string? conv = Volatile.Read(ref activeConversionName);
                    progress?.Report(new MovieCopyProgress(
                        items.Count, items.Count, conv ?? string.Empty,
                        totalBytes > 0 ? totalBytes : bytesCopied, totalBytes, 99,
                        $"Finishing MP4 conversion{(string.IsNullOrEmpty(conv) ? "" : $": {conv}")}..."));
                }
                await convertWorker;
            }

            ReportProgress(items.Count, items.Count, string.Empty, totalBytes > 0 ? totalBytes : bytesCopied, totalBytes, force: true);
            return new MovieCopyResult(filesCopied, subtitlesCopied, bytesCopied, destinationFolder, failedFiles, filesConverted);

            void ReportProgress(int fileIdx, int totalFiles, string fileName, long copied, long total, bool force)
            {
                if (!force && sw.ElapsedMilliseconds - lastReportMs < 200)
                    return;
                lastReportMs = sw.ElapsedMilliseconds;

                int pct = total > 0
                    ? (int)Math.Clamp(copied * 100L / total, 0, 100)
                    : (totalFiles > 0 ? (int)Math.Clamp(fileIdx * 100L / totalFiles, 0, 100) : 100);
                string? conv = Volatile.Read(ref activeConversionName);
                string convSuffix = !string.IsNullOrEmpty(conv) ? $" • Converting: {conv}" : string.Empty;
                string msg = string.IsNullOrEmpty(fileName)
                    ? $"Completed copying {filesCopied} movie(s)."
                    : $"Copying {fileIdx}/{totalFiles}: {fileName} ({pct}%){convSuffix}";

                progress?.Report(new MovieCopyProgress(fileIdx, totalFiles, fileName, copied, total, pct, msg));
            }
        }, ct);
    }

    private static async Task<bool> ConvertToMp4Async(
        string ffmpegPath,
        string sourcePath,
        string outputMp4Path,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("veryfast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("20");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputMp4Path);

        using var proc = Process.Start(psi);
        if (proc is null)
            return false;

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        // Drain stderr so the buffer never blocks FFmpeg
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        try { await Task.WhenAll(stderrTask, stdoutTask); } catch { }

        return proc.ExitCode == 0 && File.Exists(outputMp4Path) && new FileInfo(outputMp4Path).Length > 0;
    }

    internal static string ResolveTargetPath(string targetDir, string fileName, bool overwriteExisting)
    {
        string candidate = Path.Combine(targetDir, fileName);
        if (overwriteExisting || !File.Exists(candidate))
            return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int suffix = 2;
        while (true)
        {
            string next = Path.Combine(targetDir, $"{stem} ({suffix}){ext}");
            if (!File.Exists(next))
                return next;
            suffix++;
        }
    }
}
