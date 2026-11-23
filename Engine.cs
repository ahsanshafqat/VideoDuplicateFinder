using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoDuplicateFinder;

// ============================================================================
//  Everything in this file is UI independent (no WinForms), so it can be tested
//  from a console project.
// ============================================================================

/// <summary>Settings saved to %AppData%\VideoDuplicateFinder\settings.json</summary>
public sealed class AppSettings
{
    public string SourceFolder { get; set; } = @"G:\xxx\Nees Sort out";
    public string TargetFolder { get; set; } = @"X:\torrent\Torrent Completed\Sort out\New folder";
    public string DeleteRoot { get; set; } = @"X:\torrent\Torrent Completed\deleted";
    public string FfmpegFolder { get; set; } = "";          // empty = next to the app, or on PATH

    public int MatchScorePercent { get; set; } = 70;         // >= this = "Duplicate"
    public int PossibleScorePercent { get; set; } = 40;      // >= this = "Possible"
    public int FrameTolerance { get; set; } = 10;            // max differing bits (of 63) for two frames to look the same
    public int DurationTolerancePercent { get; set; } = 3;   // durations may differ by max(2 s, this %)
    public int Threads { get; set; } = 0;                    // 0 = number of CPU cores
    public bool IncludeSubfolders { get; set; } = true;
    public bool ShowPossible { get; set; } = true;
    public bool FolderNamedAfterVideo { get; set; }          // false = folders 1, 2, 3 ...

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoDuplicateFinder");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class Fingerprint
{
    public long Size { get; set; }
    public long Ticks { get; set; }
    public string Key { get; set; } = "";
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ulong?[] Hashes { get; set; } = Array.Empty<ulong?>();

    ulong[]? _valid;
    [JsonIgnore] public ulong[] Valid => _valid ??= Hashes.Where(h => h.HasValue).Select(h => h!.Value).ToArray();
}

public sealed class DuplicatePair
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public long SourceSize { get; set; }
    public long TargetSize { get; set; }
    public double SourceDuration { get; set; }
    public double TargetDuration { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public double Score { get; set; }   // 0..1

    public static List<DuplicatePair> SaveResults(List<DuplicatePair> pairs)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            string resultsPath = Path.Combine(AppSettings.Dir, "results.json");
            File.WriteAllText(resultsPath, JsonSerializer.Serialize(pairs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return pairs;
    }

    public static List<DuplicatePair> LoadResults()
    {
        try
        {
            string resultsPath = Path.Combine(AppSettings.Dir, "results.json");
            if (File.Exists(resultsPath))
            {
                var pairs = JsonSerializer.Deserialize<List<DuplicatePair>>(File.ReadAllText(resultsPath)) ?? new List<DuplicatePair>();

                // Remove pairs where files no longer exist
                pairs.RemoveAll(p => !File.Exists(p.Source) || !File.Exists(p.Target));

                return pairs;
            }
        }
        catch { }
        return new List<DuplicatePair>();
    }
}

public sealed record ScanProgress(string Phase, int Done, int Total, string Current, TimeSpan Elapsed, TimeSpan? Eta);

/// <summary>Thin wrapper around ffmpeg / ffprobe.</summary>
public sealed class FfTools
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }

    public FfTools(string folder)
    {
        Ffmpeg = Resolve(folder, "ffmpeg");
        Ffprobe = Resolve(folder, "ffprobe");
    }

    static string Resolve(string folder, string name)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(folder)) dirs.Add(folder);
        dirs.Add(AppContext.BaseDirectory);
        foreach (var d in dirs)
            foreach (var n in new[] { name + ".exe", name })
            {
                var c = Path.Combine(d, n);
                if (File.Exists(c)) return c;
            }
        return name; // rely on PATH
    }

    public bool IsAvailable(out string error)
    {
        error = "";
        if (Exec(Ffmpeg, new[] { "-version" }, 15000) == null)
            error = $"ffmpeg was not found ('{Ffmpeg}').";
        else if (Exec(Ffprobe, new[] { "-version" }, 15000) == null)
            error = $"ffprobe was not found ('{Ffprobe}').";
        if (error.Length > 0)
            error += "\n\nPut ffmpeg.exe and ffprobe.exe next to this application, add them to PATH, or choose their folder in the 'FFmpeg folder' box.";
        return error.Length == 0;
    }

    public static byte[]? Exec(string exe, IEnumerable<string> args, int timeoutMs = 120_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            try { p.StandardInput.Close(); } catch { }
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var err = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            copy.Wait();
            err.Wait();
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    public (double Duration, int Width, int Height) Probe(string file, CancellationToken ct = default)
    {
        var o = Exec(Ffprobe, new[] { "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration", "-of", "json", file }, 60_000, ct);
        if (o == null) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(o);
            double dur = 0; int w = 0, h = 0;
            if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
            if (doc.RootElement.TryGetProperty("streams", out var ss) && ss.GetArrayLength() > 0)
            {
                var s0 = ss[0];
                if (s0.TryGetProperty("width", out var wv)) w = wv.GetInt32();
                if (s0.TryGetProperty("height", out var hv)) h = hv.GetInt32();
            }
            return (dur, w, h);
        }
        catch { return (0, 0, 0); }
    }

    static string Sec(double s) => s.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Perceptual (DCT) hash of one frame: 32x32 gray -> low-frequency 8x8 -> 63 bits vs median.
    /// Returns null if the frame can't be read or is blank (black screen / fade).</summary>
    public ulong? FrameHash(string file, double second, CancellationToken ct = default)
    {
        var px = Exec(Ffmpeg, new[] { "-nostdin", "-v", "error", "-threads", "1",
            "-ss", Sec(second), "-i", file,
            "-an", "-sn", "-dn", "-frames:v", "1", "-vf", "scale=32:32:flags=area",
            "-pix_fmt", "gray", "-f", "rawvideo", "-" }, 120_000, ct);
        if (px == null || px.Length < 1024) return null;

        double mean = 0; for (int k = 0; k < 1024; k++) mean += px[k]; mean /= 1024;
        double variance = 0; for (int k = 0; k < 1024; k++) variance += (px[k] - mean) * (px[k] - mean);
        if (Math.Sqrt(variance / 1024) < 6) return null;

        var coef = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 32; y++)
                {
                    double rowSum = 0;
                    for (int x = 0; x < 32; x++) rowSum += px[y * 32 + x] * Cos[u, x];
                    sum += rowSum * Cos[v, y];
                }
                coef[v * 8 + u] = sum;
            }

        var sorted = coef.Skip(1).OrderBy(z => z).ToArray(); // exclude DC (overall brightness)
        double median = sorted[sorted.Length / 2];
        ulong hash = 0;
        for (int k = 1; k < 64; k++) if (coef[k] > median) hash |= 1UL << k;
        return hash;
    }

    static readonly double[,] Cos = BuildCos();
    static double[,] BuildCos()
    {
        var t = new double[8, 32];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++)
                t[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64.0);
        return t;
    }

    public byte[]? Thumbnail(string file, double second, int width, CancellationToken ct = default)
    {
        var o = Exec(Ffmpeg, new[] { "-nostdin", "-v", "error", "-threads", "1",
            "-ss", Sec(second), "-i", file,
            "-an", "-sn", "-dn", "-frames:v", "1", "-vf", $"scale={width}:-2",
            "-f", "image2pipe", "-c:v", "png", "-" }, 60_000, ct);
        return o == null || o.Length == 0 ? null : o;
    }

    public byte[]?[] Thumbnails(string file, double duration, double[] points, int width, CancellationToken ct)
    {
        var res = new byte[]?[points.Length];
        Parallel.For(0, points.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            i => res[i] = Thumbnail(file, duration * points[i], width, ct));
        return res;
    }
}

public sealed class Scanner
{
    /// <summary>Frames are taken at these fractions of each video's own length.</summary>
    public static readonly double[] SamplePoints = { 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90 };
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp" };

    const int MinValidFrames = 4;
    const double DurationToleranceSec = 2.0;
    const string CacheVersion = "v2";

    public static string CachePath => Path.Combine(AppSettings.Dir, "fingerprint_cache.json");

    readonly FfTools _tools;
    public Scanner(FfTools tools) => _tools = tools;

    public List<DuplicatePair> Scan(AppSettings s, IProgress<ScanProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        void Log(string m) => log?.Report(m);

        if (!Directory.Exists(s.SourceFolder)) throw new InvalidOperationException($"Source folder does not exist:\n{s.SourceFolder}");
        if (!Directory.Exists(s.TargetFolder)) throw new InvalidOperationException($"Target folder does not exist:\n{s.TargetFolder}");
        if (!_tools.IsAvailable(out var err)) throw new InvalidOperationException(err);

        var total = Stopwatch.StartNew();

        // Normalize paths for comparison
        string normalizedSource = Path.GetFullPath(s.SourceFolder).TrimEnd('\\', '/');
        string normalizedTarget = Path.GetFullPath(s.TargetFolder).TrimEnd('\\', '/');
        string? exclude = string.IsNullOrWhiteSpace(s.DeleteRoot) ? null : Path.GetFullPath(s.DeleteRoot).TrimEnd('\\', '/');

        Log("Listing video files...");
        Log($"  Source: {s.SourceFolder}");
        Log($"  Target: {s.TargetFolder}");
        Log($"  Include subfolders: {s.IncludeSubfolders}");

        var sourceFiles = ListVideos(s.SourceFolder, s.IncludeSubfolders, exclude, normalizedSource, normalizedTarget, Log);
        var targetFiles = ListVideos(s.TargetFolder, s.IncludeSubfolders, exclude, normalizedSource, normalizedTarget, Log);
        var allFiles = sourceFiles.Concat(targetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Log($"Source: {sourceFiles.Count} video(s), Target: {targetFiles.Count} video(s).");

        if (allFiles.Count == 0)
        {
            Log($"No video files found with supported extensions: {string.Join(", ", VideoExtensions)}");
            return new List<DuplicatePair>();
        }

        // ---------- Phase 1: fingerprints (parallel + cached) ----------
        string key = CacheVersion + ":" + string.Join(",", SamplePoints.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        var cache = LoadCache();
        var prints = new ConcurrentDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var toCompute = new List<string>();
        foreach (var f in allFiles)
        {
            var fi = new FileInfo(f);
            if (cache.TryGetValue(f, out var c) && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks && c.Key == key)
                prints[f] = c;
            else
                toCompute.Add(f);
        }
        Log($"{allFiles.Count - toCompute.Count} video(s) already fingerprinted (cache), {toCompute.Count} to analyse.");

        int threads = s.Threads > 0 ? s.Threads : Environment.ProcessorCount;
        var sw = Stopwatch.StartNew();
        int done = 0;
        progress?.Report(new ScanProgress("Phase 1/2: analysing videos", 0, toCompute.Count, "", TimeSpan.Zero, null));

        Parallel.ForEach(toCompute, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, file =>
        {
            var fi = new FileInfo(file);
            var (dur, w, h) = _tools.Probe(file, ct);
            var hashes = new ulong?[SamplePoints.Length];
            if (dur > 0)
                for (int i = 0; i < SamplePoints.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    hashes[i] = _tools.FrameHash(file, dur * SamplePoints[i], ct);
                }
            var fp = new Fingerprint { Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Key = key, Duration = dur, Width = w, Height = h, Hashes = hashes };
            prints[file] = fp;
            cache[file] = fp;

            int n = Interlocked.Increment(ref done);
            int left = toCompute.Count - n;
            var eta = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / n * left);
            progress?.Report(new ScanProgress("Phase 1/2: analysing videos", n, toCompute.Count, file, sw.Elapsed, eta));
            int valid = fp.Valid.Length;
            if (dur <= 0) Log($"WARNING unreadable video (no duration): {file}");
            else if (valid < MinValidFrames) Log($"WARNING only {valid} usable frame(s) (mostly black?), will not be matched: {Path.GetFileName(file)}");
            else Log($"Analysed {Path.GetFileName(file)}  ({FormatDuration(dur)}, {w}x{h}, {valid}/{SamplePoints.Length} frames)");
        });

        // drop entries for files that no longer exist, then save
        foreach (var k in cache.Keys.Where(k => !File.Exists(k)).ToList()) cache.TryRemove(k, out _);
        SaveCache(cache);
        Log($"Phase 1 finished in {FormatDuration(sw.Elapsed.TotalSeconds)}.");

        // ---------- Phase 2: compare ----------
        double floor = Math.Min(s.MatchScorePercent, s.PossibleScorePercent) / 100.0;
        var pairs = new List<DuplicatePair>();
        sw.Restart();
        int idx = 0;
        foreach (var src in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            idx++;
            var a = prints[src];
            foreach (var tgt in targetFiles)
            {
                if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase)) continue;
                var b = prints[tgt];
                if (!DurationsClose(a.Duration, b.Duration, s.DurationTolerancePercent)) continue;
                double score = Score(a, b, s.FrameTolerance);
                if (score >= floor)
                {
                    // Check if reverse pair already exists (target→source)
                    if (!pairs.Any(p => string.Equals(p.Source, tgt, StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(p.Target, src, StringComparison.OrdinalIgnoreCase)))
                    {
                        pairs.Add(new DuplicatePair
                        {
                            Source = src, Target = tgt,
                            SourceSize = a.Size, TargetSize = b.Size,
                            SourceDuration = a.Duration, TargetDuration = b.Duration,
                            SourceWidth = a.Width, SourceHeight = a.Height,
                            TargetWidth = b.Width, TargetHeight = b.Height,
                            Score = score
                        });
                    }
                }
            }
            var eta = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / idx * (sourceFiles.Count - idx));
            progress?.Report(new ScanProgress("Phase 2/2: comparing videos", idx, sourceFiles.Count, src, sw.Elapsed, eta));
        }

        pairs = pairs.OrderByDescending(p => p.Score).ToList();
        int dups = pairs.Count(p => p.Score >= s.MatchScorePercent / 100.0);
        Log($"Comparison finished. {dups} duplicate pair(s), {pairs.Count - dups} possible match(es). Total scan time {FormatDuration(total.Elapsed.TotalSeconds)}.");
        return pairs;
    }

    static List<string> ListVideos(string folder, bool sub, string? exclude, string normalizedSource, string normalizedTarget, Action<string>? log = null)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = sub, IgnoreInaccessible = true };
        var list = new List<string>();

        try
        {
            int allFilesCount = 0;
            int videoFilesCount = 0;
            int excludedCount = 0;
            var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string normalizedFolder = Path.GetFullPath(folder).TrimEnd('\\', '/');

            foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
            {
                allFilesCount++;
                string ext = Path.GetExtension(f).ToLowerInvariant();

                // Track all extensions found
                if (!extensionCounts.ContainsKey(ext))
                    extensionCounts[ext] = 0;
                extensionCounts[ext]++;

                if (!VideoExtensions.Contains(ext))
                    continue;

                videoFilesCount++;

                // Only exclude files in DeleteRoot if they're NOT in Source or Target folders
                if (exclude != null)
                {
                    var full = Path.GetFullPath(f).TrimEnd('\\', '/');

                    // Check if file is in DeleteRoot (but NOT in Source or Target)
                    bool inDeleteRoot = full.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                       full.Equals(exclude, StringComparison.OrdinalIgnoreCase);

                    bool inSource = full.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                   full.Equals(normalizedSource, StringComparison.OrdinalIgnoreCase);

                    bool inTarget = full.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                   full.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase);

                    // Skip only if in DeleteRoot AND not in Source AND not in Target
                    if (inDeleteRoot && !inSource && !inTarget)
                    {
                        excludedCount++;
                        continue;
                    }
                }
                list.Add(f);
            }

            // Log diagnostic info
            if (log != null)
            {
                log($"  [{folder}] Total files: {allFilesCount}, Video files: {videoFilesCount}, Excluded: {excludedCount}, Added to list: {list.Count}");
                if (extensionCounts.Count > 0)
                {
                    var nonVideoExts = extensionCounts.Where(x => !VideoExtensions.Contains(x.Key)).OrderByDescending(x => x.Value);
                    if (nonVideoExts.Any())
                        log($"    File extensions found (non-video): {string.Join(", ", nonVideoExts.Select(x => $"{x.Key}({x.Value})"))}");
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"  [{folder}] ACCESS DENIED: {ex.Message}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"  [{folder}] ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    static bool DurationsClose(double a, double b, int tolPercent)
    {
        if (a <= 0 || b <= 0) return false;
        double tol = Math.Max(DurationToleranceSec, Math.Max(a, b) * tolPercent / 100.0);
        return Math.Abs(a - b) <= tol;
    }

    /// <summary>Share of usable frames in A with a look-alike in B (and vice versa); the lower of the two.</summary>
    static double Score(Fingerprint fa, Fingerprint fb, int tolerance)
    {
        var A = fa.Valid; var B = fb.Valid;
        if (A.Length < MinValidFrames || B.Length < MinValidFrames) return 0;
        int ab = 0, ba = 0;
        foreach (var x in A) { foreach (var y in B) if (BitOperations.PopCount(x ^ y) <= tolerance) { ab++; break; } }
        foreach (var y in B) { foreach (var x in A) if (BitOperations.PopCount(x ^ y) <= tolerance) { ba++; break; } }
        return Math.Min(ab / (double)A.Length, ba / (double)B.Length);
    }

    static ConcurrentDictionary<string, Fingerprint> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, Fingerprint>>(File.ReadAllText(CachePath));
                if (d != null) return new ConcurrentDictionary<string, Fingerprint>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new ConcurrentDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveCache(ConcurrentDictionary<string, Fingerprint> cache)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch { }
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}

public static class PairMover
{
    /// <summary>Moves both files into a new sub-folder of deleteRoot and verifies the result.</summary>
    public static bool Move(string source, string target, string deleteRoot, bool nameAfterVideo, out string destFolder, out string message)
    {
        destFolder = "";
        if (!File.Exists(source)) { message = $"Source no longer exists (already moved or deleted): {source}"; return false; }
        if (!File.Exists(target)) { message = $"Target no longer exists (already moved or deleted): {target}"; return false; }

        Directory.CreateDirectory(deleteRoot);
        int number = NextFolderNumber(deleteRoot);
        string folderName = number.ToString();
        if (nameAfterVideo)
        {
            folderName = Sanitize(Path.GetFileNameWithoutExtension(source));
            if (Directory.Exists(Path.Combine(deleteRoot, folderName))) folderName += "_" + number;
        }
        destFolder = Path.Combine(deleteRoot, folderName);
        Directory.CreateDirectory(destFolder);

        // Keep original filenames, no renaming
        string srcName = Path.GetFileName(source);
        string tgtName = Path.GetFileName(target);

        // If filenames conflict, keep the larger file (higher resolution) and skip the smaller one
        if (string.Equals(srcName, tgtName, StringComparison.OrdinalIgnoreCase))
        {
            var srcAttrs = new System.IO.FileInfo(source);
            var tgtAttrs = new System.IO.FileInfo(target);
            long srcSize = srcAttrs.Length;
            long tgtSize = tgtAttrs.Length;

            // Keep the larger file, delete the smaller one
            if (srcSize >= tgtSize)
            {
                // Keep source, delete target
                try
                {
                    string newSrc = Path.Combine(destFolder, srcName);
                    File.Move(source, newSrc);
                    message = $"Moved (kept higher quality) → {destFolder}";
                    try { File.Delete(target); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    message = $"Could not move {source}: {ex.Message}";
                    TryRemoveEmpty(destFolder);
                    return false;
                }
            }
            else
            {
                // Keep target, delete source
                try
                {
                    string newTgt = Path.Combine(destFolder, tgtName);
                    File.Move(target, newTgt);
                    message = $"Moved (kept higher quality) → {destFolder}";
                    try { File.Delete(source); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    message = $"Could not move {target}: {ex.Message}";
                    TryRemoveEmpty(destFolder);
                    return false;
                }
            }
        }

        // Normal case: different filenames, move both
        string newSrcPath = Path.Combine(destFolder, srcName);
        string newTgtPath = Path.Combine(destFolder, tgtName);

        try
        {
            File.Move(source, newSrcPath);
        }
        catch (Exception ex) { message = $"Could not move {source}: {ex.Message}"; TryRemoveEmpty(destFolder); return false; }

        try
        {
            File.Move(target, newTgtPath);
        }
        catch (Exception ex)
        {
            // roll back the first move so the pair stays together
            try { File.Move(newSrcPath, source); } catch { }
            TryRemoveEmpty(destFolder);
            message = $"Could not move {target}: {ex.Message} (source was put back)";
            return false;
        }

        bool ok = File.Exists(newSrcPath) && File.Exists(newTgtPath) && !File.Exists(source) && !File.Exists(target);
        message = ok ? $"Moved and verified -> {destFolder}" : "Verification failed: check the folders manually.";
        return ok;
    }

    static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    static int NextFolderNumber(string root)
    {
        int max = 0;
        foreach (var d in Directory.GetDirectories(root))
            if (int.TryParse(Path.GetFileName(d), out int n) && n > max) max = n;
        return max + 1;
    }

    static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length == 0 ? "video" : name.Trim();
    }
}
