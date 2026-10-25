using System;

// A test-only child process that exits during a request without either protocol marker.
public static class ToolFailureStub
{
    public static void Main()
    {
        string line;
        while ((line = Console.ReadLine()) != null)
            if (line.StartsWith("-execute", StringComparison.Ordinal))
            {
                Console.WriteLine("incomplete response");
                Console.Error.WriteLine("simulated child-process failure");
                return;
            }
    }
}
