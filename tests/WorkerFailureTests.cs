using System;
using System.IO;
using System.Linq;
using System.Threading;
using TakeoutDateRepair;

public static class WorkerFailureTests
{
    public static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        string stub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ToolFailureStub.exe");
        for (int i = 0; i < 2; i++)
        {
            string media = Path.Combine(input, "photo" + i + ".jpg"); File.WriteAllText(media, "source must survive a worker crash");
            File.WriteAllText(media + ".json", Engine.Serializer().Serialize(new { title = Path.GetFileName(media), photoTakenTime = new { timestamp = "1704067200" } }));
        }
        var hashes = Directory.GetFiles(input).ToDictionary(x => x, x => Engine.Hash(x, CancellationToken.None));
        var scan = Engine.Analyze(input, false, CancellationToken.None, s => { });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = Engine.Process(scan, Path.Combine(root, "output"), stub, true, true, CancellationToken.None, (s,m,w) => { }, true);
        if (result.Failed != 2 || result.Success != 0 || result.ToolProcessStarts != 2) throw new Exception("An interrupted worker was trusted or was not restarted for the next item");
        if (!hashes.All(x => File.Exists(x.Key) && Engine.Hash(x.Key, CancellationToken.None) == x.Value)) throw new Exception("A source changed after a worker crash");
        if (Directory.GetFiles(Path.Combine(root, "output"), "*.jpg", SearchOption.TopDirectoryOnly).Length != 0) throw new Exception("An unverified file was published");
        if (timer.Elapsed.TotalSeconds > 60) throw new Exception("Worker EOF was not detected promptly");
        Console.WriteLine("PASS Worker EOF preserves both source pairs, publishes no partial result and restarts cleanly");
        var stopped = Engine.Process(scan, Path.Combine(root, "missing-exe-output"), Path.Combine(root, "not-installed.exe"), true, true, CancellationToken.None, (s,m,w) => { }, true);
        if (stopped.Failed != 2 || !hashes.All(x => Engine.Hash(x.Key, CancellationToken.None) == x.Value)) throw new Exception("Worker startup failure changed an input");
        Console.WriteLine("PASS Worker startup failure preserves all input files");
    }
}
