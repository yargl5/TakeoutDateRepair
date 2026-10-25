using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyProduct("TakeoutDateRepair")]

namespace TakeoutDateRepair
{
    public sealed class Sidecar
    {
        public string Path, Title, Error, ContentHash;
        public DateTime Utc;
        public bool UploadDate;
    }
    public sealed class Item
    {
        public string Source, Problem, Note;
        public Sidecar Metadata;
        public List<string> CandidatePaths = new List<string>();
    }
    public sealed class Scan
    {
        public List<Item> Items = new List<Item>();
        public List<string> Issues = new List<string>();
        public int JsonCount, OrphanCount, DirectoryCount;
        public List<FolderSummary> Folders = new List<FolderSummary>();
        public string Root;
    }
    public sealed class FolderSummary
    {
        public string Path;
        public int MediaCount, ReadyCount, ProblemCount;
    }
    public sealed class History
    {
        public string sourceHash, targetHash, target, timestamp;
    }
    public sealed class Stats
    {
        public int Done, Success, Failed, Duplicates, Warnings, Total;
        public TimeSpan Elapsed;
        public int ToolProcessStarts;
        public string Report;
    }
    public static class Engine
    {
        static readonly HashSet<string> Extensions = new HashSet<string>(
            (".jpg .jpeg .jpe .png .heic .heif .tif .tiff .webp .gif .bmp .avif .dng .cr2 .cr3 .nef .nrw .arw .rw2 .orf .raf .pef .srw " +
             ".mp4 .mov .m4v .3gp .3g2 .avi .mkv .mts .m2ts .mpg .mpeg .wmv .webm .vob .mod .tod").Split(' '), StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> QuickTime = new HashSet<string>(new[] { ".mp4", ".mov", ".m4v", ".3gp", ".3g2" }, StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Exif = new HashSet<string>(new[] { ".jpg", ".jpeg", ".jpe", ".tif", ".tiff", ".heic", ".heif", ".png", ".webp", ".avif", ".dng" }, StringComparer.OrdinalIgnoreCase);
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        public static string Full(string path)
        {
            string full = System.IO.Path.GetFullPath(path), root = System.IO.Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        public static bool Within(string child, string parent)
        {
            string fullChild = Full(child), fullParent = Full(parent);
            string prefix = fullParent.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? fullParent : fullParent + System.IO.Path.DirectorySeparatorChar;
            return string.Equals(fullChild, fullParent, StringComparison.OrdinalIgnoreCase) || fullChild.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        static void Check(CancellationToken token) { token.ThrowIfCancellationRequested(); }
        static List<string> Files(Scan scan, CancellationToken token, Action<string> progress)
        {
            var files = new List<string>(); var pending = new Stack<string>(); pending.Push(scan.Root);
            while (pending.Count > 0)
            {
                Check(token); string dir = pending.Pop();
                scan.DirectoryCount++;
                if (scan.DirectoryCount == 1 || scan.DirectoryCount % 25 == 0)
                    progress("Поиск во всех подпапках. Просмотрено папок: " + scan.DirectoryCount + " — " + dir);
                try
                {
                    files.AddRange(Directory.GetFiles(dir));
                    foreach (string sub in Directory.GetDirectories(dir))
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                        else scan.Issues.Add("Пропущена ссылка на папку: " + sub);
                }
                catch (Exception ex) { scan.Issues.Add("Не прочитана папка: " + dir + " — " + ex.Message); }
            }
            return files;
        }
        static string Text(Dictionary<string, object> d, string key)
        { object value; return d.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        static bool Timestamp(Dictionary<string, object> d, string field, out DateTime date)
        {
            date = default(DateTime); object obj;
            if (!d.TryGetValue(field, out obj)) return false;
            var value = obj as Dictionary<string, object>; long seconds;
            if (value == null || !long.TryParse(Text(value, "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return false;
            try { date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds); return date.Year >= 1601 && date.Year <= 9998; }
            catch { return false; }
        }
        static Sidecar ReadSidecar(string path, bool fallback)
        {
            var result = new Sidecar { Path = path };
            try
            {
                byte[] content = File.ReadAllBytes(path);
                using (var sha = SHA256.Create()) result.ContentHash = BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "");
                var d = Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(content).TrimStart('\uFEFF'));
                if (d == null) throw new InvalidDataException("Пустой JSON");
                result.Title = Text(d, "title");
                if (!Timestamp(d, "photoTakenTime", out result.Utc))
                {
                    if (fallback && Timestamp(d, "creationTime", out result.Utc)) result.UploadDate = true;
                    else result.Error = "Нет корректной photoTakenTime";
                }
            }
            catch (Exception ex) { result.Error = "Ошибка JSON: " + ex.Message; }
            return result;
        }
        static void Add(Dictionary<string, List<Sidecar>> index, string key, Sidecar sidecar)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            List<Sidecar> list;
            if (!index.TryGetValue(key, out list)) index[key] = list = new List<Sidecar>();
            if (!list.Contains(sidecar)) list.Add(sidecar);
        }
        static string NameFromSidecar(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            name = Regex.Replace(name, @"\.json$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(name, @"\.supplemental-metadata(\(\d+\))?$", "$1", RegexOptions.IgnoreCase);
        }
        static Sidecar Choose(List<Sidecar> list, out string problem)
        {
            problem = null;
            if (list == null || list.Count == 0) return null;
            // Never choose a convenient date when one candidate is malformed or disagrees.
            if (list.Count > 1 && (list.Any(x => x.Error != null) || list.Select(x => x.Utc.Ticks).Distinct().Count() != 1))
            { problem = "Несколько подходящих JSON с разными или некорректными датами"; return null; }
            return list[0];
        }
        public static Scan Analyze(string root, bool fallback, CancellationToken token, Action<string> progress)
        {
            var scan = new Scan { Root = Full(root) };
            var files = Files(scan, token, progress);
            var byName = new Dictionary<string, List<Sidecar>>(StringComparer.OrdinalIgnoreCase);
            var byTitle = new Dictionary<string, List<Sidecar>>(StringComparer.OrdinalIgnoreCase);
            var global = new Dictionary<string, List<Sidecar>>(StringComparer.OrdinalIgnoreCase);
            var sidecars = new List<Sidecar>(); int count = 0;
            foreach (string path in files.Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                Check(token); var sc = ReadSidecar(path, fallback); string dir = System.IO.Path.GetDirectoryName(path);
                // Album-level metadata has no media title or media extension and is not a photo sidecar.
                string inferred = NameFromSidecar(path);
                if (!Extensions.Contains(System.IO.Path.GetExtension(inferred)) && !Extensions.Contains(System.IO.Path.GetExtension(sc.Title ?? "")) && path.IndexOf(".supplemental-metadata", StringComparison.OrdinalIgnoreCase) < 0) continue;
                sidecars.Add(sc); Add(byName, dir + "\n" + inferred, sc);
                string shifted = Regex.Replace(inferred, @"(\.[^.()]+)(\(\d+\))$", "$2$1");
                if (shifted != inferred) Add(byName, dir + "\n" + shifted, sc);
                if (!string.IsNullOrEmpty(sc.Title)) { Add(byTitle, dir + "\n" + sc.Title, sc); Add(global, sc.Title, sc); }
                if (++count % 300 == 0) progress("Чтение JSON: " + count.ToString("N0"));
            }
            scan.JsonCount = sidecars.Count;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files.Where(x => Extensions.Contains(System.IO.Path.GetExtension(x))))
            {
                Check(token); var item = new Item { Source = path }; List<Sidecar> matches;
                string key = System.IO.Path.GetDirectoryName(path) + "\n" + System.IO.Path.GetFileName(path);
                if (!byName.TryGetValue(key, out matches)) byTitle.TryGetValue(key, out matches);
                if (matches == null)
                {
                    List<Sidecar> remote;
                    if (global.TryGetValue(System.IO.Path.GetFileName(path), out remote) && remote.Count == 1)
                    { matches = remote; item.Note = "JSON найден в другой папке"; }
                }
                item.Metadata = Choose(matches, out item.Problem);
                if (matches != null) foreach (var m in matches) { used.Add(m.Path); item.CandidatePaths.Add(m.Path); }
                if (item.Metadata == null && item.Problem == null) item.Problem = "Не найден подходящий JSON";
                if (item.Metadata != null && item.Metadata.Error != null) item.Problem = item.Metadata.Error;
                if (item.Metadata != null && item.Metadata.UploadDate) item.Note = "Использована дата загрузки creationTime, дата съёмки отсутствует";
                scan.Items.Add(item);
            }
            foreach (var sc in sidecars.Where(x => !used.Contains(x.Path)))
            { scan.OrphanCount++; scan.Issues.Add("JSON без однозначно найденного медиа: " + sc.Path + (sc.Error == null ? "" : " — " + sc.Error)); }
            foreach (var group in scan.Items.GroupBy(x => System.IO.Path.GetDirectoryName(x.Source), StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                scan.Folders.Add(new FolderSummary { Path = group.Key, MediaCount = group.Count(), ReadyCount = group.Count(x => x.Problem == null), ProblemCount = group.Count(x => x.Problem != null) });
            progress("Анализ завершён"); return scan;
        }
        public static string Relative(string path, string root)
        {
            if (string.Equals(Full(path), Full(root), StringComparison.OrdinalIgnoreCase)) return "(корневая папка)";
            return Within(path, root) ? Full(path).Substring(Full(root).Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : path;
        }
        public static string Hash(string path, CancellationToken token)
        {
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(path))
            {
                byte[] buffer = new byte[1024 * 1024]; int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { Check(token); sha.TransformBlock(buffer, 0, n, buffer, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0); return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        static void Copy(string source, string dest, CancellationToken token)
        {
            using (var input = File.OpenRead(source)) using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] buf = new byte[1024 * 1024]; int n; while ((n = input.Read(buf, 0, buf.Length)) > 0) { Check(token); output.Write(buf, 0, n); } output.Flush(true); }
        }
        [ThreadStatic] static ToolWorker activeTool;
        public static string Tool(string exe, IEnumerable<string> args)
        {
            if (activeTool != null && string.Equals(exe, activeTool.Exe, StringComparison.OrdinalIgnoreCase)) return activeTool.Execute(args);
            return OneShotTool(exe, args);
        }
        static ProcessStartInfo ToolStartInfo(string exe, bool persistent)
        {
            var info = new ProcessStartInfo(exe, persistent ? "-stay_open True -@ -" : "-charset filename=UTF8 -@ -") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.EnvironmentVariables["LC_ALL"] = "C"; info.EnvironmentVariables["LC_CTYPE"] = "C"; info.EnvironmentVariables["LANG"] = "C";
            return info;
        }
        static void SendArguments(Process process, IEnumerable<string> args)
        {
            string[] lines = args.ToArray();
            if (lines.Any(x => x == null || x.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) throw new IOException("Недопустимый аргумент ExifTool");
            byte[] bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");
            process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); process.StandardInput.BaseStream.Flush();
        }
        static string OneShotTool(string exe, IEnumerable<string> args)
        {
            using (var p = new Process())
            {
                // Windows Perl argv uses the system code page. UTF-8 stdin preserves Cyrillic paths.
                p.StartInfo = ToolStartInfo(exe, false);
                p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
                SendArguments(p, args); p.StandardInput.Close();
                // A file is a transaction: Stop waits for this metadata operation, never kills a writer mid-file.
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } throw new IOException("ExifTool не ответил за 5 минут; исходник сохранён"); }
                Task.WaitAll(stdout, stderr);
                string result = stdout.Result + "\n" + stderr.Result;
                if (p.ExitCode != 0) throw new IOException("ExifTool: " + result.Trim());
                return args.Contains("-j") ? stdout.Result.Trim() : result.Trim();
            }
        }
        sealed class ToolWorker : IDisposable
        {
            public readonly string Exe;
            public int Starts;
            readonly bool persistent;
            readonly ToolWorker previous;
            Process process;
            int sequence;
            public ToolWorker(string exe, bool reuse)
            { Exe = exe; persistent = reuse; previous = activeTool; activeTool = this; }
            sealed class ErrorFrame { public string Text; public int Status; }
            static string ReadOutput(StreamReader reader, string marker)
            {
                var result = new StringBuilder(); string line;
                while ((line = reader.ReadLine()) != null)
                { if (line == marker) return result.ToString().Trim(); result.AppendLine(line); }
                throw new IOException("ExifTool завершился до ответа; исходник сохранён");
            }
            static ErrorFrame ReadErrors(StreamReader reader, string marker)
            {
                var result = new StringBuilder(); string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(marker, StringComparison.Ordinal))
                    {
                        int status;
                        if (!int.TryParse(line.Substring(marker.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out status)) throw new IOException("Некорректный статус ExifTool");
                        return new ErrorFrame { Text = result.ToString().Trim(), Status = status };
                    }
                    result.AppendLine(line);
                }
                throw new IOException("ExifTool завершился до статуса; исходник сохранён");
            }
            public string Execute(IEnumerable<string> arguments)
            {
                string[] args = arguments.ToArray();
                if (!persistent) { Starts++; return OneShotTool(Exe, args); }
                string output; ErrorFrame errors;
                try
                {
                    if (process == null)
                    {
                        process = new Process { StartInfo = ToolStartInfo(Exe, true) }; process.Start(); Starts++;
                    }
                    int id = ++sequence;
                    string errorMarker = "__TakeoutStatus_" + Guid.NewGuid().ToString("N") + "_";
                    var outputReader = process.StandardOutput; var errorReader = process.StandardError;
                    var stdout = Task.Run(() => ReadOutput(outputReader, "{ready" + id + "}"));
                    var stderr = Task.Run(() => ReadErrors(errorReader, errorMarker));
                    SendArguments(process, args.Concat(new[] { "-echo4", errorMarker + "${status}", "-execute" + id.ToString(CultureInfo.InvariantCulture) }));
                    if (!Task.WaitAll(new Task[] { stdout, stderr }, 300000)) throw new IOException("ExifTool не ответил за 5 минут; исходник сохранён");
                    output = stdout.Result; errors = stderr.Result;
                }
                catch (Exception ex)
                {
                    Stop(false);
                    throw new IOException("Связь с ExifTool прервана; исходник сохранён. " + ex.GetBaseException().Message, ex);
                }
                // Both streams have been drained. A normal per-file error does not corrupt the next response.
                if (errors.Status != 0) throw new IOException("ExifTool: " + output + "\n" + errors.Text);
                return args.Contains("-j") ? output : (output + "\n" + errors.Text).Trim();
            }
            void Stop(bool graceful)
            {
                if (process == null) return;
                try
                {
                    if (graceful && !process.HasExited) { SendArguments(process, new[] { "-stay_open", "False" }); process.StandardInput.Close(); }
                    if (!process.HasExited && (!graceful || !process.WaitForExit(5000))) process.Kill();
                    if (!process.HasExited) process.WaitForExit(5000);
                }
                catch { try { if (!process.HasExited) process.Kill(); } catch { } }
                finally { process.Dispose(); process = null; }
            }
            public void Dispose() { try { Stop(true); } finally { activeTool = previous; } }
        }
        static string Stamp(DateTime utc) { return utc.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); }
        public static string WriteMetadata(string exe, string path, DateTime utc)
        {
            string ext = System.IO.Path.GetExtension(path), stamp = Stamp(utc);
            var args = new List<string> { "-charset", "filename=UTF8", "-overwrite_original", "-api", "QuickTimeUTC=0" };
            if (Exif.Contains(ext))
            {
                args.Add("-EXIF:DateTimeOriginal=" + stamp); args.Add("-EXIF:CreateDate=" + stamp); args.Add("-EXIF:ModifyDate=" + stamp);
                args.Add("-EXIF:OffsetTimeOriginal=+00:00"); args.Add("-EXIF:OffsetTimeDigitized=+00:00"); args.Add("-EXIF:OffsetTime=+00:00");
            }
            if (QuickTime.Contains(ext))
                foreach (string tag in new[] { "CreateDate", "ModifyDate", "TrackCreateDate", "TrackModifyDate", "MediaCreateDate", "MediaModifyDate" }) args.Add("-QuickTime:" + tag + "=" + stamp);
            args.Add("-XMP:DateCreated=" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            args.Add(path); return Tool(exe, args);
        }
        public static void VerifyMetadata(string exe, string path, DateTime utc)
        {
            string ext = System.IO.Path.GetExtension(path);
            var args = new[] { "-charset", "filename=UTF8", "-j", "-G1", "-api", "QuickTimeUTC=0", "-EXIF:DateTimeOriginal", "-EXIF:OffsetTimeOriginal", "-QuickTime:CreateDate", "-XMP:DateCreated", path };
            var rows = Serializer().Deserialize<List<Dictionary<string, object>>>(Tool(exe, args));
            if (rows == null || rows.Count != 1) throw new IOException("Не удалось прочитать записанные метаданные");
            var d = rows[0]; string expected = Stamp(utc); string xmp = Text(d, "XMP-photoshop:DateCreated");
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParseExact(xmp, new[] { "yyyy:MM:dd HH:mm:ssK", "yyyy:MM:dd HH:mm:sszzz" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) || parsed.UtcDateTime != utc)
                throw new IOException("Проверка XMP: дата после записи не совпала с JSON (" + xmp + ")");
            if (Exif.Contains(ext) && (Text(d, "ExifIFD:DateTimeOriginal") != expected || Text(d, "ExifIFD:OffsetTimeOriginal") != "+00:00")) throw new IOException("Проверка EXIF: дата или UTC-смещение не совпали");
            if (QuickTime.Contains(ext) && Text(d, "QuickTime:CreateDate") != expected) throw new IOException("Проверка QuickTime: дата не совпала");
        }
        static string Csv(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        public static void Row(StreamWriter w, params string[] values) { w.WriteLine(string.Join(";", values.Select(Csv))); w.Flush(); }
        static string Unique(string dest, string name)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            string path = System.IO.Path.Combine(dest, name); int n = 2;
            while (File.Exists(path) || Directory.Exists(path)) path = System.IO.Path.Combine(dest, stem + " (" + (n++) + ")" + ext);
            return path;
        }
        public static string SaveScan(Scan scan, string folder)
        {
            Directory.CreateDirectory(folder); string file = Unique(folder, "Анализ_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            using (var w = new StreamWriter(file, false, new UTF8Encoding(true)))
            {
                Row(w, "Статус", "Исходный файл", "JSON", "Дата UTC", "Примечание");
                Row(w, "Сводка анализа", scan.Root, "", "", "Просмотрено папок: " + scan.DirectoryCount + "; папок с медиа: " + scan.Folders.Count);
                foreach (var summary in scan.Folders) Row(w, "Папка", summary.Path, "", "", "Медиа: " + summary.MediaCount + "; готовы: " + summary.ReadyCount + "; проблемы: " + summary.ProblemCount);
                foreach (var item in scan.Items) Row(w, item.Problem == null ? "Готов" : "Проблема", item.Source, item.Metadata == null ? "" : item.Metadata.Path,
                    item.Metadata == null || item.Metadata.Error != null ? "" : item.Metadata.Utc.ToString("o"), item.Problem ?? item.Note);
                foreach (string issue in scan.Issues) Row(w, "Проблема анализа", "", "", "", issue);
            }
            return file;
        }
        static string CleanSidecar(Item item, ILookup<string, Item> users)
        {
            string json = item.Metadata.Path;
            if (users[json].Any(x => File.Exists(x.Source)))
                return "JSON сохранён: ещё нужен необработанным исходным файлам";
            if (!File.Exists(json)) return "";
            try
            {
                if (Hash(json, CancellationToken.None) != item.Metadata.ContentHash) return "JSON изменился после анализа и сохранён";
                File.Delete(json); return "Исходный JSON удалён";
            }
            catch (Exception ex) { return "JSON не удалён: " + ex.Message; }
        }
        public static Stats Process(Scan scan, string dest, string exe, bool move, bool dedupe, CancellationToken token, Action<Stats, string, bool> progress, bool deleteJson = false, bool reuseTool = true)
        {
            if (Within(dest, scan.Root) || Within(scan.Root, dest)) throw new IOException("Исходная и целевая папки должны быть отдельными: ни одна не может содержать другую");
            Directory.CreateDirectory(dest);
            string admin = System.IO.Path.Combine(dest, "_TakeoutDateRepair"); Directory.CreateDirectory(admin);
            // Prevent simultaneous writers racing for names or corrupting the journal.
            using (var sessionLock = new FileStream(System.IO.Path.Combine(admin, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var toolWorker = new ToolWorker(exe, reuseTool))
            {
                string journal = System.IO.Path.Combine(admin, "history.jsonl");
                var history = new Dictionary<string, List<History>>(StringComparer.Ordinal);
                if (File.Exists(journal)) using (var reader = File.OpenText(journal))
                {
                    string line; while ((line = reader.ReadLine()) != null)
                    {
                        Check(token);
                        try
                        {
                            var h = Serializer().Deserialize<History>(line);
                            if (h == null || h.sourceHash == null || h.timestamp == null || h.target == null || h.targetHash == null) continue;
                            string key = h.sourceHash + "|" + h.timestamp; List<History> hs;
                            if (!history.TryGetValue(key, out hs)) history[key] = hs = new List<History>(); hs.Add(h);
                        }
                        catch { /* A partial final journal line never authorizes deleting an original. */ }
                    }
                }
                var stopwatch = Stopwatch.StartNew();
                var users = scan.Items.SelectMany(item => item.CandidatePaths.Select(path => new { path, item })).ToLookup(x => x.path, x => x.item, StringComparer.OrdinalIgnoreCase);
                var stats = new Stats { Total = scan.Items.Count, Report = Unique(admin, "Отчёт_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv") };
                using (var report = new StreamWriter(stats.Report, false, new UTF8Encoding(true)))
                using (var log = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    Row(report, "Статус", "Исходный файл", "Результат", "JSON", "Дата UTC", "Примечание");
                    foreach (var issue in scan.Issues) { Row(report, "Проблема анализа", "", "", "", "", issue); stats.Warnings++; }
                    foreach (var item in scan.Items)
                    {
                        if (token.IsCancellationRequested) break;
                        string target = "", temp = null, status = "Ошибка", note = item.Note ?? ""; bool bad = false, warning = !string.IsNullOrEmpty(item.Note);
                        try
                        {
                            if (item.Problem != null) throw new IOException(item.Problem);
                            if (!File.Exists(item.Metadata.Path) || Hash(item.Metadata.Path, token) != item.Metadata.ContentHash)
                                throw new IOException("JSON изменился или исчез после анализа. Повторите анализ; исходник сохранён");
                            var utc = item.Metadata.Utc; string stamp = utc.ToString("o");
                            progress(stats, "Обработка: " + Relative(item.Source, scan.Root), false);
                            string sourceHash = Hash(item.Source, token); string key = sourceHash + "|" + stamp;
                            History existing = null; List<History> candidates;
                            if (dedupe && history.TryGetValue(key, out candidates))
                                foreach (var h in candidates)
                                    if (Within(h.target, dest) && File.Exists(h.target) && Hash(h.target, token) == h.targetHash) { existing = h; break; }
                            if (existing != null)
                            {
                                VerifyMetadata(exe, existing.target, utc);
                                target = existing.target;
                                status = "Дубликат, исходник сохранён";
                                if (move)
                                {
                                    if (Hash(item.Source, CancellationToken.None) != sourceHash || Hash(target, CancellationToken.None) != existing.targetHash)
                                        throw new IOException("Дубликат изменился во время проверки; исходник сохранён");
                                    File.Delete(item.Source); status = "Дубликат: исходник удалён, результат проверен";
                                }
                                stats.Duplicates++;
                            }
                            else
                            {
                                temp = System.IO.Path.Combine(admin, ".partial-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(item.Source));
                                Copy(item.Source, temp, token);
                                if (Hash(temp, token) != sourceHash) throw new IOException("Контрольная сумма копии не совпала с исходником");
                                string toolResult = WriteMetadata(exe, temp, utc);
                                VerifyMetadata(exe, temp, utc);
                                if (toolResult.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) { note += " " + toolResult; stats.Warnings++; warning = true; }
                                File.SetCreationTimeUtc(temp, utc); File.SetLastWriteTimeUtc(temp, utc);
                                if (Math.Abs((File.GetCreationTimeUtc(temp) - utc).TotalSeconds) > 2 || Math.Abs((File.GetLastWriteTimeUtc(temp) - utc).TotalSeconds) > 2)
                                    throw new IOException("Файловая система не сохранила даты Windows");
                                string outputHash = Hash(temp, token);
                                target = Unique(dest, System.IO.Path.GetFileName(item.Source));
                                // File.Move fails rather than overwriting if another process created the target meanwhile.
                                File.Move(temp, target); temp = null;
                                File.SetCreationTimeUtc(target, utc); File.SetLastWriteTimeUtc(target, utc);
                                var h = new History { sourceHash = sourceHash, targetHash = outputHash, target = target, timestamp = stamp };
                                byte[] line = new UTF8Encoding(false).GetBytes(Serializer().Serialize(h) + Environment.NewLine);
                                log.Write(line, 0, line.Length); log.Flush(true);
                                if (!history.TryGetValue(key, out candidates)) history[key] = candidates = new List<History>(); candidates.Add(h);
                                status = "Скопирован";
                                if (move)
                                {
                                    // Re-hash both files immediately before deleting source, even across volumes.
                                    if (Hash(item.Source, CancellationToken.None) != sourceHash || Hash(target, CancellationToken.None) != outputHash)
                                        throw new IOException("Файл изменился во время обработки; исходник сохранён, результат: " + target);
                                    try { File.Delete(item.Source); status = "Перемещён"; }
                                    catch (Exception ex) { throw new IOException("Результат готов, но исходник не удалён: " + ex.Message); }
                                }
                                stats.Success++;
                            }
                            if (move && deleteJson && !File.Exists(item.Source))
                            {
                                string cleanupNote = CleanSidecar(item, users);
                                if (cleanupNote.Length > 0) note += (note.Length > 0 ? "; " : "") + cleanupNote;
                                if (cleanupNote.IndexOf("не удалён", StringComparison.OrdinalIgnoreCase) >= 0 || cleanupNote.IndexOf("изменился", StringComparison.OrdinalIgnoreCase) >= 0) { stats.Warnings++; warning = true; }
                            }
                            if (!string.IsNullOrEmpty(item.Note)) stats.Warnings++;
                        }
                        catch (OperationCanceledException) { status = "Остановлен, исходник сохранён"; note = "Операция прервана"; bad = true; }
                        catch (Exception ex) { stats.Failed++; bad = true; note = ex.Message; }
                        finally
                        {
                            if (temp != null) try { File.Delete(temp); } catch { }
                        }
                        stats.Done++;
                        Row(report, status, item.Source, target, item.Metadata == null ? "" : item.Metadata.Path,
                            item.Metadata == null || item.Metadata.Error != null ? "" : item.Metadata.Utc.ToString("o"), note);
                        progress(stats, status + ": " + Relative(item.Source, scan.Root) + (note.Length > 0 ? " — " + note : ""), bad || warning);
                        if (token.IsCancellationRequested) break;
                    }
                    stats.Elapsed = stopwatch.Elapsed; stats.ToolProcessStarts = toolWorker.Starts;
                    Row(report, token.IsCancellationRequested ? "Обработка остановлена" : "Обработка завершена", "", "", "", "",
                        "Выполнено " + stats.Done + "/" + stats.Total + "; успешно " + stats.Success + "; дубликатов " + stats.Duplicates + "; ошибок " + stats.Failed + "; время " + stats.Elapsed.ToString() + "; запусков ExifTool " + stats.ToolProcessStarts);
                }
                return stats;
            }
        }
    }
    public sealed class MainForm : Form
    {
        readonly TextBox source = new TextBox(), destination = new TextBox();
        readonly Button pickSource = new Button(), pickDestination = new Button(), analyze = new Button(), start = new Button(), cancel = new Button(), report = new Button();
        readonly CheckBox move = new CheckBox(), cleanup = new CheckBox(), dedupe = new CheckBox(), fallback = new CheckBox();
        readonly Label state = new Label(), counts = new Label();
        readonly ProgressBar bar = new ProgressBar();
        readonly ListView messages = new ListView(), folders = new ListView();
        CancellationTokenSource cancellation;
        Scan scan; string reportPath; bool busy; readonly string exe;
        public MainForm(string tool)
        {
            exe = tool; Text = "TakeoutDateRepair 1.2.0 — все вложенные папки"; MinimumSize = new Size(860, 690); Size = new Size(1040, 820);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 3, RowCount = 13 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = "Вернуть фотографиям их даты", Font = new Font("Segoe UI", 19, FontStyle.Bold), Dock = DockStyle.Fill };
            layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 3);
            layout.Controls.Add(new Label { Text = "Общая папка", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            source.Dock = DockStyle.Fill; layout.Controls.Add(source, 1, 1); pickSource.Text = "Выбрать…"; pickSource.Dock = DockStyle.Fill; layout.Controls.Add(pickSource, 2, 1);
            layout.Controls.Add(new Label { Text = "Результат", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            destination.Dock = DockStyle.Fill; layout.Controls.Add(destination, 1, 2); pickDestination.Text = "Выбрать…"; pickDestination.Dock = DockStyle.Fill; layout.Controls.Add(pickDestination, 2, 2);
            var hint = new Label { Text = "Все вложенные папки включены • результат в одну папку • даты UTC\nВыберите общую папку Google Фото или Takeout — папки по годам будут найдены автоматически.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 80, 95) };
            layout.Controls.Add(hint, 0, 3); layout.SetColumnSpan(hint, 3);
            move.Text = "Перемещать: удалять исходник только после проверки результата"; move.Checked = true; move.AutoSize = true;
            cleanup.Text = "Удалять JSON обработанной пары; общий JSON — после обработки всех связанных файлов"; cleanup.Checked = true; cleanup.AutoSize = true;
            dedupe.Text = "Не создавать повторные копии одинаковых файлов (SHA-256); при перемещении удалять исходные дубликаты"; dedupe.Checked = true; dedupe.AutoSize = true;
            fallback.Text = "Если даты съёмки нет — использовать дату загрузки Google (с предупреждением)"; fallback.AutoSize = true;
            layout.Controls.Add(move, 0, 4); layout.SetColumnSpan(move, 3); layout.Controls.Add(cleanup, 0, 5); layout.SetColumnSpan(cleanup, 3); layout.Controls.Add(dedupe, 0, 6); layout.SetColumnSpan(dedupe, 3); layout.Controls.Add(fallback, 0, 7); layout.SetColumnSpan(fallback, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            analyze.Text = "1. Анализ"; start.Text = "2. Обработать"; cancel.Text = "Остановить"; report.Text = "Открыть отчёт";
            foreach (var b in new[] { analyze, start, cancel, report }) { b.AutoSize = true; b.Height = 36; b.Padding = new Padding(8, 2, 8, 2); buttons.Controls.Add(b); }
            start.BackColor = Color.FromArgb(35, 100, 190); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
            layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 3);
            state.Text = "Выберите общую папку. Все подпапки обрабатываются за один запуск."; state.Dock = DockStyle.Fill;
            layout.Controls.Add(state, 0, 9); layout.SetColumnSpan(state, 3);
            bar.Dock = DockStyle.Fill; layout.Controls.Add(bar, 0, 10); layout.SetColumnSpan(bar, 3);
            counts.Text = "Выполнено: 0 / 0"; counts.Dock = DockStyle.Fill; layout.Controls.Add(counts, 0, 11); layout.SetColumnSpan(counts, 3);
            messages.Dock = DockStyle.Fill; messages.View = View.Details; messages.FullRowSelect = true; messages.GridLines = true;
            messages.Columns.Add("Статус", 120); messages.Columns.Add("Файл / описание проблемы", 780);
            folders.Dock = DockStyle.Fill; folders.View = View.Details; folders.FullRowSelect = true; folders.GridLines = true;
            folders.Columns.Add("Папка внутри выбранной", 580); folders.Columns.Add("Медиа", 100); folders.Columns.Add("Готовы", 100); folders.Columns.Add("Проблемы", 110);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var fileTab = new TabPage("Файлы и проблемы"); fileTab.Controls.Add(messages);
            var folderTab = new TabPage("Все найденные папки"); folderTab.Controls.Add(folders);
            tabs.TabPages.Add(fileTab); tabs.TabPages.Add(folderTab);
            layout.Controls.Add(tabs, 0, 12); layout.SetColumnSpan(tabs, 3); Controls.Add(layout);
            pickSource.Click += delegate { Pick(source); }; pickDestination.Click += delegate { Pick(destination); };
            source.TextChanged += delegate { InvalidateScan(); }; fallback.CheckedChanged += delegate { InvalidateScan(); };
            move.CheckedChanged += delegate { EnableControls(); };
            analyze.Click += async delegate { await Analyze(); }; start.Click += async delegate { await Run(); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); state.Text = "Остановка… Текущая запись метаданных будет завершена безопасно."; cancel.Enabled = false; };
            report.Click += delegate { try { if (File.Exists(reportPath)) Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; cancellation.Cancel(); state.Text = "Дождитесь безопасной остановки, затем закройте окно."; } };
            EnableControls();
        }
        void Pick(TextBox box) { using (var d = new FolderBrowserDialog { Description = box == source ? "Выберите общую папку Google Фото или Takeout. Все вложенные папки по годам будут включены автоматически." : "Выберите отдельную папку для результата всех годов", ShowNewFolderButton = true }) { if (Directory.Exists(box.Text)) d.SelectedPath = box.Text; if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath; } }
        void InvalidateScan()
        {
            scan = null; reportPath = null; messages.Items.Clear(); folders.Items.Clear(); bar.Value = 0;
            counts.Text = "Выполнено: 0 / 0";
            state.Text = "Папка или настройки изменены. Нажмите «1. Анализ».";
            EnableControls();
        }
        void EnableControls()
        {
            source.Enabled = destination.Enabled = pickSource.Enabled = pickDestination.Enabled = move.Enabled = dedupe.Enabled = fallback.Enabled = analyze.Enabled = !busy;
            cleanup.Enabled = !busy && move.Checked;
            start.Enabled = !busy && scan != null && scan.Items.Any(x => x.Problem == null); cancel.Enabled = busy; report.Enabled = !busy && File.Exists(reportPath);
        }
        void Ui(Action a) { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
        void Message(string text, bool error)
        {
            if (messages.Items.Count >= 1500) messages.Items.RemoveAt(0);
            var row = new ListViewItem(error ? "Внимание" : "Информация"); row.SubItems.Add(text); if (error) row.ForeColor = Color.FromArgb(160, 55, 25); messages.Items.Add(row); row.EnsureVisible();
        }
        async Task Analyze()
        {
            if (!Directory.Exists(source.Text)) { MessageBox.Show(this, "Выберите существующую папку с экспортом Google Фото."); return; }
            string root = source.Text; bool useFallback = fallback.Checked;
            busy = true; cancellation = new CancellationTokenSource(); scan = null; EnableControls(); messages.Items.Clear(); folders.Items.Clear(); bar.Style = ProgressBarStyle.Marquee; state.Text = "Поиск фото, видео и JSON во всех вложенных папках…";
            try
            {
                scan = await Task.Run(() => Engine.Analyze(root, useFallback, cancellation.Token, s => Ui(() => state.Text = s)));
                int problems = scan.Items.Count(x => x.Problem != null);
                state.Text = "Папок с медиа: " + scan.Folders.Count + "; медиа: " + scan.Items.Count.ToString("N0") + "; готовы: " + (scan.Items.Count - problems).ToString("N0") + "; проблем: " + problems + "; JSON без пары: " + scan.OrphanCount;
                counts.Text = "Просмотрено папок: " + scan.DirectoryCount + " | С медиа: " + scan.Folders.Count + " | Медиа: " + scan.Items.Count + " | JSON: " + scan.JsonCount;
                foreach (var folder in scan.Folders)
                {
                    var row = new ListViewItem(Engine.Relative(folder.Path, scan.Root)); row.SubItems.Add(folder.MediaCount.ToString()); row.SubItems.Add(folder.ReadyCount.ToString()); row.SubItems.Add(folder.ProblemCount.ToString()); folders.Items.Add(row);
                }
                Message("Все " + scan.Folders.Count + " папок с медиа включены в общую очередь. Список — во вкладке «Все найденные папки».", false);
                foreach (var i in scan.Items.Where(x => x.Problem != null || x.Note != null).Take(1000)) Message(i.Source + " — " + (i.Problem ?? i.Note), true);
                foreach (var issue in scan.Issues.Take(300)) Message(issue, true);
                string reports = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TakeoutDateRepair-reports"); reportPath = Engine.SaveScan(scan, reports);
                Message("Полный отчёт анализа: " + reportPath, false);
            }
            catch (OperationCanceledException) { state.Text = "Анализ остановлен. Файлы не изменены."; }
            catch (Exception ex) { state.Text = "Ошибка анализа"; Message(ex.Message, true); }
            finally { busy = false; bar.Style = ProgressBarStyle.Continuous; bar.Value = 0; EnableControls(); }
        }
        async Task Run()
        {
            if (scan == null) return;
            string dest;
            try
            {
                if (string.IsNullOrWhiteSpace(destination.Text)) throw new IOException("Выберите папку результата.");
                dest = Engine.Full(destination.Text);
                if (Engine.Within(dest, scan.Root) || Engine.Within(scan.Root, dest)) throw new IOException("Выберите отдельную папку: исходная и целевая не должны совпадать или содержать друг друга.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
            bool moving = move.Checked, skipDuplicates = dedupe.Checked, deleteJson = cleanup.Checked;
            if (moving && MessageBox.Show(this, "Обрабатываются все " + scan.Folders.Count + " папок с медиа внутри:\n" + scan.Root + "\n\nПосле проверки результата будут удалены исходные фото и видео, включая дубликаты с проверенной готовой копией.\n\n" + (deleteJson ? "JSON обработанных пар также удалятся. Общий JSON удалится, только когда больше не нужен оставшимся исходникам." : "Исходные JSON останутся на месте.") + "\n\nФайлы без пары и файлы с ошибками сохранятся.\n\nПродолжить перемещение в:\n" + dest + "?", "Подтверждение перемещения всех подпапок", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true; cancellation = new CancellationTokenSource(); EnableControls(); bar.Maximum = Math.Max(scan.Items.Count, 1); bar.Value = 0; state.Text = "Обработка…";
            try
            {
                var result = await Task.Run(() => Engine.Process(scan, dest, exe, moving, skipDuplicates, cancellation.Token, (s, text, error) => {
                    int done = s.Done, total = s.Total, ok = s.Success, failed = s.Failed, dup = s.Duplicates, warn = s.Warnings;
                    Ui(() => { bar.Value = Math.Min(done, bar.Maximum); counts.Text = string.Format("Выполнено: {0:N0} / {1:N0} | Успешно: {2:N0} | Дубликаты: {3:N0} | Ошибки: {4:N0} | Замечания: {5:N0}", done, total, ok, dup, failed, warn); state.Text = text.Length > 130 ? text.Substring(0, 127) + "…" : text; if (!text.StartsWith("Обработка:")) Message(text, error); });
                }, deleteJson));
                reportPath = result.Report; state.Text = cancellation.IsCancellationRequested ? "Остановлено. Необработанные исходники сохранены." : "Готово. Общая очередь из " + scan.Folders.Count + " папок обработана за " + result.Elapsed.ToString(@"hh\:mm\:ss") + ". Подробности — в CSV-отчёте.";
                Message("Отчёт: " + reportPath, false);
                if (moving) scan = null;
            }
            catch (OperationCanceledException) { state.Text = "Остановлено. Исходники сохранены."; }
            catch (Exception ex) { state.Text = "Обработка остановлена из-за ошибки"; Message(ex.Message, true); }
            finally { busy = false; EnableControls(); }
        }
    }
    public static class Program
    {
        public static string ExtractTool()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TakeoutDateRepair-tools-13.59-x64");
            using (var mutex = new Mutex(false, "Local\\TakeoutDateRepairExtract1359"))
            {
                if (!mutex.WaitOne(60000)) throw new IOException("Другой экземпляр распаковывает ExifTool. Повторите запуск.");
                try
                {
                    string exe = System.IO.Path.Combine(folder, "ExifTool.exe"), marker = System.IO.Path.Combine(folder, ".complete");
                    if (File.Exists(marker) && File.Exists(exe)) return exe;
                    Directory.CreateDirectory(folder);
                    using (var stream = typeof(Program).Assembly.GetManifestResourceStream("exiftool.zip"))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                        foreach (var entry in zip.Entries)
                        {
                            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, entry.FullName));
                            if (!Engine.Within(target, folder)) throw new IOException("Небезопасный путь в архиве ExifTool");
                            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                            using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                        }
                    File.WriteAllText(marker, "13.59"); return exe;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 3 && args[0] == "--scan")
                {
                    var scan = Engine.Analyze(args[1], false, CancellationToken.None, s => { });
                    string report = Engine.SaveScan(scan, args[2]);
                    File.WriteAllText(System.IO.Path.Combine(args[2], "scan-summary.json"), Engine.Serializer().Serialize(new { media = scan.Items.Count, ready = scan.Items.Count(x => x.Problem == null), problems = scan.Items.Count(x => x.Problem != null), json = scan.JsonCount, orphan = scan.OrphanCount, issues = scan.Issues.Count, directories = scan.DirectoryCount, mediaFolders = scan.Folders.Count, folders = scan.Folders, report = report }), Encoding.UTF8); return;
                }
                Application.Run(new MainForm(ExtractTool()));
            }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Takeout: ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
