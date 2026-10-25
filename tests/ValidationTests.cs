using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using TakeoutDateRepair;
public static class Tests
{
    static int count;
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    static void Jpeg(string path, Color color) { using (var image = new Bitmap(32, 32)) { using (var g = Graphics.FromImage(image)) g.Clear(color); image.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg); } }
    static void Json(string path, string title, string timestamp) { File.WriteAllText(path, "{\"title\":" + Engine.Serializer().Serialize(title) + ",\"photoTakenTime\":{\"timestamp\":\"" + timestamp + "\"}}"); }
    public static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), exe = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "Тестовый архив"), dest = Path.Combine(root, "Результат"); Directory.CreateDirectory(source);
        string a = Path.Combine(source, "Фото Ёжик.jpg"); Jpeg(a, Color.Crimson); Json(a + ".supplemental-metadata.json", "Фото Ёжик.jpg", "1704067200");
        string b = Path.Combine(source, "foo(1).jpg"); Jpeg(b, Color.Green); Json(Path.Combine(source, "foo.jpg(1).json"), "foo.jpg", "1704153600");
        string c = Path.Combine(source, "без даты.jpg"); Jpeg(c, Color.Blue); File.WriteAllText(c + ".json", "{\"title\":\"без даты.jpg\",\"creationTime\":{\"timestamp\":\"1704067200\"}}");
        string d = Path.Combine(source, "без json.jpg"); Jpeg(d, Color.Yellow);
        Jpeg(Path.Combine(source, "ещё без json.jpg"), Color.Orange);
        string e = Path.Combine(source, "ошибка.jpg"); Jpeg(e, Color.Gray); File.WriteAllText(e + ".json", "{broken");
        Json(Path.Combine(source, "потерян.jpg.json"), "потерян.jpg", "1704067200");
        string album = Path.Combine(source, "Альбом"); Directory.CreateDirectory(album);
        string dup = Path.Combine(album, "Фото Ёжик.jpg"); File.Copy(a, dup); Json(dup + ".json", "Фото Ёжик.jpg", "1704067200");
        string hash = Engine.Hash(a, CancellationToken.None);
        var scan = Engine.Analyze(source, false, CancellationToken.None, s => { });
        Assert(scan.Items.Count == 7, "Recursive media scan");
        Assert(scan.Items.Count(x => x.Problem == null) == 3, "New, legacy and duplicate-number sidecar pairing");
        Assert(scan.OrphanCount == 1, "Orphan JSON counted");
        Assert(scan.Items.Single(x => x.Source == c).Problem != null, "Upload date not silently used as taken date");
        Assert(Engine.Analyze(source, true, CancellationToken.None, s => { }).Items.Single(x => x.Source == c).Metadata.UploadDate, "Explicit upload-date fallback");
        Directory.CreateDirectory(dest); Jpeg(Path.Combine(dest, "Фото Ёжик.jpg"), Color.Black); string collisionHash = Engine.Hash(Path.Combine(dest, "Фото Ёжик.jpg"), CancellationToken.None);
        var stats = Engine.Process(scan, dest, exe, false, true, CancellationToken.None, (s, m, p) => { Console.WriteLine(m); });
        Assert(stats.Success == 2 && stats.Duplicates == 1 && stats.Failed == 4, "Copy, dedupe and per-file failures");
        Assert(Engine.Hash(a, CancellationToken.None) == hash, "Original unchanged");
        Assert(Engine.Hash(Path.Combine(dest, "Фото Ёжик.jpg"), CancellationToken.None) == collisionHash, "Name collision never overwrites");
        string output = Path.Combine(dest, "Фото Ёжик (2).jpg");
        Assert(File.Exists(output), "Unicode output and collision suffix");
        var date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); Engine.VerifyMetadata(exe, output, date); Assert(true, "EXIF, XMP and UTC offset read-back");
        Assert(File.GetLastWriteTimeUtc(output) == date && File.GetCreationTimeUtc(output) == date, "Windows creation and modification times UTC");
        var second = Engine.Process(scan, dest, exe, false, true, CancellationToken.None, (s, m, p) => { });
        Assert(second.Success == 0 && second.Duplicates == 3, "Repeat run uses verified journal");
        bool rejected = false; try { Engine.Process(scan, Path.Combine(source, "output"), exe, false, true, CancellationToken.None, (s, m, p) => { }); } catch(IOException) { rejected = true; }
        Assert(rejected, "Nested destination rejected");
        string moveSource = Path.Combine(root, "move-source"); Directory.CreateDirectory(moveSource); string moveFile = Path.Combine(moveSource, "move.jpg"); Jpeg(moveFile, Color.Purple); Json(moveFile + ".json", "move.jpg", "1704067200");
        var ms = Engine.Analyze(moveSource, false, CancellationToken.None, s => { });
        var moved = Engine.Process(ms, Path.Combine(root, "move-dest"), exe, true, true, CancellationToken.None, (s, m, p) => { });
        Assert(moved.Success == 1 && !File.Exists(moveFile) && File.Exists(moveFile + ".json"), "Move deletes only verified media and preserves JSON");
        var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
        try { Engine.Analyze(source, false, stop.Token, s => { }); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancellation");
        // Same title and different dates must never resolve by first-match order.
        string ambiguous = Path.Combine(root, "ambiguous"); Directory.CreateDirectory(ambiguous); Jpeg(Path.Combine(ambiguous, "x.jpg"), Color.Red);
        Json(Path.Combine(ambiguous, "first.json"), "x.jpg", "1704067200"); Json(Path.Combine(ambiguous, "second.json"), "x.jpg", "1704153600");
        Assert(Engine.Analyze(ambiguous, false, CancellationToken.None, s => { }).Items.Single().Problem != null, "Conflicting title matches rejected");
        Console.WriteLine("ALL " + count + " CHECKS PASSED");
    }
}
