using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using TakeoutDateRepair;
class MoreTests
{
    static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        foreach (var name in new[] { "duplicate(1).jpg", "Настоящий PNG.png", "Очень длинное имя фото с усечённым названием JSON.jpg" })
        {
            string path = Path.Combine(source, name);
            using(var image = new Bitmap(40,40)) image.Save(path, name.EndsWith(".png") ? System.Drawing.Imaging.ImageFormat.Png : System.Drawing.Imaging.ImageFormat.Jpeg);
            string sidecar = name.StartsWith("duplicate") ? "duplicate.jpg.supplemental-metadata(1).json" : name.StartsWith("Очень") ? "усечённое.json" : name + ".json";
            File.WriteAllText(Path.Combine(source, sidecar), "{\"title\":" + Engine.Serializer().Serialize(name) + ",\"photoTakenTime\":{\"timestamp\":\"1704067200\"}}");
        }
        var scan = Engine.Analyze(source, false, CancellationToken.None, s=>{});
        if(scan.Items.Count(x=>x.Problem == null) != 3) throw new Exception("Additional JSON matching failed");
        Console.WriteLine("PASS numbered supplemental sidecar and title-based truncated match");
        var stats = Engine.Process(scan, Path.Combine(root,"output"), args[1], false, false, CancellationToken.None, (s,m,b)=>Console.WriteLine(m));
        if(stats.Success != 3 || stats.Failed != 0) throw new Exception("PNG/JPEG metadata write failed");
        Console.WriteLine("PASS real PNG date metadata and read-back");
        string png=Path.Combine(root,"output","Настоящий PNG.png"); File.WriteAllText(png,"corrupted");
        var rerun = Engine.Process(scan, Path.Combine(root,"output"), args[1], false, true, CancellationToken.None, (s,m,b)=>{});
        if(rerun.Success != 1 || rerun.Duplicates != 2 || !File.Exists(Path.Combine(root,"output","Настоящий PNG (2).png"))) throw new Exception("Corrupt output recovery failed");
        Console.WriteLine("PASS damaged output is not trusted; new verified copy created without overwriting");
    }
}
