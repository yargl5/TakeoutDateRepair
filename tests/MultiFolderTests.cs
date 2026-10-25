using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using TakeoutDateRepair;

public static class MultiFolderTests
{
    static int checks;
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++; Console.WriteLine("PASS " + message);
    }
    static void Pair(string folder, string name, Color color, long timestamp)
    {
        Directory.CreateDirectory(folder); string file = Path.Combine(folder, name);
        using (var image = new Bitmap(40, 40))
        {
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
            image.Save(file, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        File.WriteAllText(file + ".supplemental-metadata.json", Engine.Serializer().Serialize(new {
            title = name, photoTakenTime = new { timestamp = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }), new UTF8Encoding(false));
    }
    public static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), input = Path.Combine(root, "Общая папка"), output = Path.Combine(root, "Результат всех годов");
        string driveRoot = Path.GetPathRoot(root);
        Assert(Engine.Full(driveRoot) == driveRoot && Engine.Within(Path.Combine(driveRoot, "Example", "Photos"), driveRoot) && Engine.Relative(Path.Combine(driveRoot, "Example", "Photos"), driveRoot) == Path.Combine("Example", "Photos"), "Drive roots retain their separator and relative paths stay correct");
        string photos = Path.Combine(input, "Takeout", "Google Фото");
        string year2019 = Path.Combine(photos, "Фото 2019 г"), year2020 = Path.Combine(photos, "Фото 2020 г");
        string year2024 = Path.Combine(photos, "Фото 2024 г"), album = Path.Combine(year2024, "Вложенный альбом");
        Pair(year2019, "IMG_0001.jpg", Color.Red, 1546300800);
        Pair(year2020, "IMG_0001.jpg", Color.Green, 1577836800);
        Pair(album, "Nested.jpg", Color.Blue, 1704067200);
        Pair(photos, "Root.jpg", Color.Gold, 1609459200);
        Directory.CreateDirectory(Path.Combine(photos, "Фото 2018 г — пусто"));
        string orphanMedia = Path.Combine(year2020, "Нет JSON.jpg"), orphanJson = Path.Combine(year2024, "Нет фото.jpg.json");
        using (var image = new Bitmap(20, 20)) image.Save(orphanMedia, System.Drawing.Imaging.ImageFormat.Jpeg);
        File.WriteAllText(orphanJson, "{\"title\":\"Нет фото.jpg\",\"photoTakenTime\":{\"timestamp\":\"1704067200\"}}");
        string orphanHash = Engine.Hash(orphanMedia, CancellationToken.None), orphanJsonHash = Engine.Hash(orphanJson, CancellationToken.None);
        var scan = Engine.Analyze(input, false, CancellationToken.None, s => Console.WriteLine(s));
        Assert(scan.DirectoryCount == 8, "One parent selection visits the entire Takeout hierarchy, including empty folders");
        Assert(scan.Items.Count == 5 && scan.Folders.Count == 4, "Root media and three nested media folders share one queue");
        Assert(scan.Items.Count(x => x.Problem == null) == 4 && scan.OrphanCount == 1, "All four pairs are found while unmatched files remain explicit");
        var folder2020 = scan.Folders.Single(x => x.Path == year2020);
        Assert(folder2020.MediaCount == 2 && folder2020.ReadyCount == 1 && folder2020.ProblemCount == 1, "Folder summary reports ready and problem counts separately");
        var direct = Engine.Analyze(photos, false, CancellationToken.None, s => { });
        Assert(direct.DirectoryCount == 6 && direct.Items.Select(x => x.Source).OrderBy(x => x).SequenceEqual(scan.Items.Select(x => x.Source).OrderBy(x => x)), "Selecting Google Photos or its parent finds the same media");
        string analysisReport = Engine.SaveScan(scan, root);
        string reportText = File.ReadAllText(analysisReport);
        Assert(reportText.Contains("Сводка анализа") && reportText.Contains("Папка") && reportText.Contains("Вложенный альбом"), "CSV includes the full folder summary");
        var expected = scan.Items.Where(x => x.Problem == null).Select(x => new {
            item = x, hash = Engine.Hash(x.Source, CancellationToken.None), utc = x.Metadata.Utc
        }).ToArray();
        var progress = new List<string>();
        var result = Engine.Process(scan, output, args[1], true, true, CancellationToken.None, (s, text, problem) => { progress.Add(text); Console.WriteLine(text); }, true);
        Assert(result.Done == 5 && result.Success == 4 && result.Failed == 1, "A single run processes every year, the nested album and root files");
        Assert(progress.Any(x => x.Contains("Фото 2019 г")) && progress.Any(x => x.Contains("Вложенный альбом")), "Progress identifies each current subfolder rather than only the filename");
        var history = File.ReadAllLines(Path.Combine(output, "_TakeoutDateRepair", "history.jsonl")).Select(x => Engine.Serializer().Deserialize<History>(x)).ToArray();
        foreach (var entry in expected)
        {
            var recorded = history.Single(x => x.sourceHash == entry.hash);
            Assert(File.Exists(recorded.target), "Verified output exists for " + Engine.Relative(entry.item.Source, scan.Root));
            Engine.VerifyMetadata(args[1], recorded.target, entry.utc);
            Assert(File.GetCreationTimeUtc(recorded.target) == entry.utc && File.GetLastWriteTimeUtc(recorded.target) == entry.utc, "Date belongs to the matching year-specific JSON");
            Assert(!File.Exists(entry.item.Source) && !File.Exists(entry.item.Metadata.Path), "Only the successfully processed test pair is removed");
        }
        Assert(Directory.GetFiles(output, "*.jpg", SearchOption.TopDirectoryOnly).Length == 4 && File.Exists(Path.Combine(output, "IMG_0001.jpg")) && File.Exists(Path.Combine(output, "IMG_0001 (2).jpg")), "Identical filenames from different years never overwrite each other");
        Assert(Engine.Hash(orphanMedia, CancellationToken.None) == orphanHash && Engine.Hash(orphanJson, CancellationToken.None) == orphanJsonHash, "Unmatched files in different years remain unchanged");
        var repeated = Engine.Analyze(input, false, CancellationToken.None, s => { });
        Assert(repeated.Items.Count == 1 && repeated.Items.All(x => x.Problem != null) && repeated.OrphanCount == 1, "A fresh scan contains only leftovers after all years were processed");
        string newYear = Path.Combine(photos, "Фото 2025 г");
        Pair(newYear, "IMG_0001.jpg", Color.Red, 1546300800);
        var nextBatch = Engine.Analyze(input, false, CancellationToken.None, s => { });
        var resumed = Engine.Process(nextBatch, output, args[1], true, true, CancellationToken.None, (s, text, problem) => { }, true);
        Assert(resumed.Duplicates == 1 && !File.Exists(Path.Combine(newYear, "IMG_0001.jpg")) && !File.Exists(Path.Combine(newYear, "IMG_0001.jpg.supplemental-metadata.json")), "A new year folder is included on the next scan and a verified duplicate is cleaned up");
        Assert(Directory.GetFiles(output, "*.jpg", SearchOption.TopDirectoryOnly).Length == 4, "Repeat processing does not create a redundant output");
        Console.WriteLine("ALL " + checks + " MULTI-FOLDER CHECKS PASSED");
    }
}
