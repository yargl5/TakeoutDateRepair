using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using TakeoutDateRepair;

public static class PerformanceTests
{
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Sidecar(string file, int seconds)
    {
        File.WriteAllText(file + ".json", Engine.Serializer().Serialize(new { title = Path.GetFileName(file), photoTakenTime = new { timestamp = (1704067200L + seconds).ToString(CultureInfo.InvariantCulture) } }));
    }
    static void Picture(string path, int index)
    {
        using (var image = new Bitmap(640, 480))
        {
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.FromArgb(20 + index % 200, 75, 140));
                using (var pen = new Pen(Color.White, 3)) for (int x = 0; x < 640; x += 33) g.DrawLine(pen, x, 0, (x + index * 7) % 640, 479);
            }
            image.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
    }
    public static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        int count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 4;
        Assert(count >= 2 && count <= 100, "Benchmark size is bounded");
        string badFirst = Path.Combine(input, "00-invalid.jpg"); File.WriteAllText(badFirst, "not an image"); Sidecar(badFirst, 0);
        for (int i = 0; i < count; i++) { string path = Path.Combine(input, "Фото " + i.ToString("00") + ".jpg"); Picture(path, i); Sidecar(path, i + 1); }
        string badLast = Path.Combine(input, "zz-invalid.png"); File.WriteAllText(badLast, "not a png"); Sidecar(badLast, 20);
        var scan = Engine.Analyze(input, false, CancellationToken.None, s => { });
        // Make error/success/error/success framing deterministic, independently of filesystem order.
        scan.Items = scan.Items.OrderBy(x => x.Source == badFirst ? 0 : x.Source == badLast ? count - 1 : int.Parse(Path.GetFileNameWithoutExtension(x.Source).Substring(5), CultureInfo.InvariantCulture) + 1).ToList();
        var hashes = scan.Items.ToDictionary(x => x.Source, x => Engine.Hash(x.Source, CancellationToken.None));
        string baseline = Path.Combine(root, "one-shot"), optimized = Path.Combine(root, "persistent");
        var timer = Stopwatch.StartNew();
        var old = Engine.Process(scan, baseline, args[1], false, false, CancellationToken.None, (s,m,w) => { }, false, false);
        double oldSeconds = timer.Elapsed.TotalSeconds;
        timer.Restart();
        var fast = Engine.Process(scan, optimized, args[1], false, false, CancellationToken.None, (s,m,w) => { }, false, true);
        double newSeconds = timer.Elapsed.TotalSeconds;
        Assert(old.Success == count && fast.Success == count && old.Failed == 2 && fast.Failed == 2, "Both modes preserve per-file error handling and continue after bad input");
        Assert(fast.ToolProcessStarts == 1 && old.ToolProcessStarts == count * 2 + 2, "One persistent process replaces per-command process startup");
        Assert(fast.Elapsed > TimeSpan.Zero && File.ReadAllText(fast.Report).Contains(fast.Elapsed.ToString()), "Elapsed time is set before the CSV footer is written");
        foreach (var item in scan.Items.Where(x => x.Source != badFirst && x.Source != badLast))
        {
            string name = Path.GetFileName(item.Source);
            Assert(Engine.Hash(Path.Combine(baseline, name), CancellationToken.None) == Engine.Hash(Path.Combine(optimized, name), CancellationToken.None), "Output bytes match both modes: " + name);
            Engine.VerifyMetadata(args[1], Path.Combine(optimized, name), item.Metadata.Utc);
        }
        Assert(hashes.All(x => File.Exists(x.Key) && Engine.Hash(x.Key, CancellationToken.None) == x.Value), "Copy-mode input files remain unchanged");
        string changed = Path.Combine(root, "changed"); Directory.CreateDirectory(changed);
        string edited = Path.Combine(changed, "edited.jpg"); Picture(edited, 90); Sidecar(edited, 0);
        var analyzed = Engine.Analyze(changed, false, CancellationToken.None, s => { });
        DateTime previousTime = File.GetLastWriteTimeUtc(edited + ".json");
        string json = File.ReadAllText(edited + ".json"); File.WriteAllText(edited + ".json", json.Replace("1704067200", "1704067201")); File.SetLastWriteTimeUtc(edited + ".json", previousTime);
        var refused = Engine.Process(analyzed, Path.Combine(root, "changed-output"), args[1], true, true, CancellationToken.None, (s,m,w) => { }, true);
        Assert(refused.Failed == 1 && refused.ToolProcessStarts == 0 && File.Exists(edited) && File.Exists(edited + ".json"), "A changed JSON with identical size and timestamp cannot bypass SHA-256 verification");
        string orphanDir = Path.Combine(root, "orphans"); Directory.CreateDirectory(orphanDir); string orphan = Path.Combine(orphanDir, "later.jpg"); Picture(orphan, 80);
        var missing = Engine.Analyze(orphanDir, false, CancellationToken.None, s => { });
        var skipped = Engine.Process(missing, Path.Combine(root, "orphan-output"), args[1], false, true, CancellationToken.None, (s,m,w) => { });
        Assert(skipped.Failed == 1 && skipped.ToolProcessStarts == 0, "A media file without a JSON never launches ExifTool");
        Sidecar(orphan, 10);
        var found = Engine.Analyze(orphanDir, false, CancellationToken.None, s => { });
        Assert(found.Items.Single().Problem == null, "A fresh scan finds a new sidecar instead of caching the previous missing pair");
        // Cancellation before work starts must not launch a metadata writer or remove anything.
        string protection = Path.Combine(root, "protection"); Directory.CreateDirectory(protection); string original = Path.Combine(protection, "protected.jpg"); Picture(original, 60); Sidecar(original, 30);
        var protectScan = Engine.Analyze(protection, false, CancellationToken.None, s => { });
        var stopped = new CancellationTokenSource(); stopped.Cancel();
        var cancelled = Engine.Process(protectScan, Path.Combine(root, "cancelled"), args[1], true, true, stopped.Token, (s,m,w) => { }, true);
        Assert(cancelled.Done == 0 && cancelled.ToolProcessStarts == 0 && File.Exists(original), "Cancel-before-start creates no worker and never deletes input");
        File.WriteAllText(Path.Combine(root, "benchmark.json"), Engine.Serializer().Serialize(new { files = count, bytes = hashes.Keys.Sum(x => new FileInfo(x).Length), oneShotSeconds = oldSeconds, persistentSeconds = newSeconds, ratio = oldSeconds / newSeconds, oneShotStarts = old.ToolProcessStarts, persistentStarts = fast.ToolProcessStarts, includesTwoInvalidFiles = true }));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "BENCHMARK: {0} photos, one-shot {1:F3}s ({2} starts), persistent {3:F3}s ({4} starts), ratio {5:F2}x", count, oldSeconds, old.ToolProcessStarts, newSeconds, fast.ToolProcessStarts, oldSeconds / newSeconds));
    }
}
