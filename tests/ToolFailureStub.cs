using System;
using System.IO;
using System.Threading;

// A test-only child process that exits during a request without either protocol marker.
public static class ToolFailureStub
{
    public static void Main()
    {
        using (var stream = new FileStream("commands.args", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        while (true)
        {
            string line = reader.ReadLine();
            if (line == null) { Thread.Sleep(5); continue; }
            if (line.StartsWith("-execute", StringComparison.Ordinal))
            {
                Console.WriteLine("incomplete response");
                Console.Error.WriteLine("simulated child-process failure");
                return;
            }
        }
    }
}
