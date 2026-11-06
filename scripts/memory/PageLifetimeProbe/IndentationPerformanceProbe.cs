using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using carton.GUI.Controls;

internal static class IndentationPerformanceProbe
{
    internal static int Run()
    {
        var method = typeof(JsonConfigEditor).Assembly.GetType("carton.GUI.Controls.JsonIndentation")!
            .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var count in new[] { 1000, 100000, 1000000 })
        {
            var text = "{\n" + string.Concat(Enumerable.Repeat("    \"item\": 1,\n", count)) + "    \"nested\": {}\n}";
            var lines = new List<JsonLine>();
            JsonSyntax.BuildLines(text, lines, out _); // Existing index: outside timed indentation work.
            var start = text.LastIndexOf("{}", StringComparison.Ordinal) + 1;
            object[] args = { new JsonTextSource(text), start, 0, lines, lines.Count - 2 };
            for (var i = 0; i < 100; i++) method.Invoke(null, args);
            var samples = new List<double>();
            long allocation = 0;
            for (var batch = 0; batch < 5; batch++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 10000; i++) method.Invoke(null, args);
                sw.Stop();
                allocation = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 10000;
                samples.Add(sw.Elapsed.TotalMilliseconds / 10000);
            }
            samples.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new { lines = lines.Count, characters = text.Length, medianIndentMs = samples[2], allocatedBytesPerCallIncludingReflection = allocation }));
        }
        return 0;
    }
}
