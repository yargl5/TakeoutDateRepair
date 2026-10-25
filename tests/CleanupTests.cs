using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Threading;
using TakeoutDateRepair;
class CleanupTests
{
    static int count;
    static void Assert(bool test, string message) { if(!test) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
    static void Image(string path) { using(var b = new Bitmap(40,40)) b.Save(path,System.Drawing.Imaging.ImageFormat.Jpeg); }
    static void Json(string path,string title) { File.WriteAllText(path,"{\"title\":"+Engine.Serializer().Serialize(title)+",\"photoTakenTime\":{\"timestamp\":\"1704067200\"}}"); }
    static Scan Scan(string source) { return Engine.Analyze(source,false,CancellationToken.None,s=>{}); }
    static void Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]), tool=args[1]; Directory.CreateDirectory(root);
        string source=Path.Combine(root,"batch"),output=Path.Combine(root,"output"); Directory.CreateDirectory(source);
        string paired=Path.Combine(source,"готово.jpg"), pairJson=paired+".supplemental-metadata.json"; Image(paired);Json(pairJson,"готово.jpg");
        string waitMedia=Path.Combine(source,"следующая часть.jpg");Image(waitMedia);
        string waitJson=Path.Combine(source,"будущее фото.jpg.json");Json(waitJson,"будущее фото.jpg");
        string broken=Path.Combine(source,"сломано.jpg");File.WriteAllText(broken,"not a photo");Json(broken+".json","сломано.jpg");
        var first=Engine.Process(Scan(source),output,tool,true,true,CancellationToken.None,(s,m,b)=>Console.WriteLine(m),true);
        Assert(first.Success==1 && !File.Exists(paired) && !File.Exists(pairJson),"Successful source media and JSON removed after verification");
        Assert(File.Exists(waitMedia) && File.Exists(waitJson),"Unmatched media and orphan JSON survive for next archive");
        Assert(File.Exists(broken) && File.Exists(broken+".json"),"Failed media retains both source and JSON");
        Json(waitMedia+".json","следующая часть.jpg");Image(Path.Combine(source,"будущее фото.jpg"));
        var second=Engine.Process(Scan(source),output,tool,true,true,CancellationToken.None,(s,m,b)=>{},true);
        Assert(!File.Exists(waitMedia) && !File.Exists(waitMedia+".json") && !File.Exists(waitJson) && !File.Exists(Path.Combine(source,"будущее фото.jpg")),"New archive completes old pairs; verified duplicates cleaned up");
        Assert(second.Duplicates==2,"Duplicate cleanup reuses verified output instead of writing another copy");
        string shared=Path.Combine(root,"shared");Directory.CreateDirectory(shared);Directory.CreateDirectory(Path.Combine(shared,"a"));Directory.CreateDirectory(Path.Combine(shared,"b"));
        string left=Path.Combine(shared,"a","shared.jpg"),right=Path.Combine(shared,"b","shared.jpg"),common=Path.Combine(shared,"shared.jpg.json");Image(left);Image(right);Json(common,"shared.jpg");
        var stop=new CancellationTokenSource();
        var partial=Engine.Process(Scan(shared),Path.Combine(root,"shared-output"),tool,true,true,stop.Token,(s,m,b)=>{if(s.Done==1)stop.Cancel();},true);
        Assert(partial.Done==1 && File.Exists(common) && (File.Exists(left)^File.Exists(right)),"Shared JSON retained when stopped after first referencing source");
        var resumed=Engine.Process(Scan(shared),Path.Combine(root,"shared-output"),tool,true,true,CancellationToken.None,(s,m,b)=>{},true);
        Assert(resumed.Duplicates==1 && !File.Exists(left) && !File.Exists(right) && !File.Exists(common),"Resume removes last duplicate and shared JSON only after verification");
        string changed=Path.Combine(root,"changed");Directory.CreateDirectory(changed);string photo=Path.Combine(changed,"photo.jpg");Image(photo);Json(photo+".json","photo.jpg");var scan=Scan(changed);File.AppendAllText(photo+".json"," ");
        var failed=Engine.Process(scan,Path.Combine(root,"changed-output"),tool,true,true,CancellationToken.None,(s,m,b)=>{},true);
        Assert(failed.Failed==1 && File.Exists(photo) && File.Exists(photo+".json"),"Changed JSON never authorizes deleting original pair");
        string copy=Path.Combine(root,"copy");Directory.CreateDirectory(copy);string cp=Path.Combine(copy,"cp.jpg");Image(cp);Json(cp+".json","cp.jpg");
        Engine.Process(Scan(copy),Path.Combine(root,"copy-output"),tool,false,true,CancellationToken.None,(s,m,b)=>{},true);
        Assert(File.Exists(cp) && File.Exists(cp+".json"),"Copy mode never deletes originals even if cleanup flag is checked");
        Console.WriteLine("ALL "+count+" CLEANUP CHECKS PASSED");
    }
}
