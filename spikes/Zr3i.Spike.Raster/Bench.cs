using System.Diagnostics;
using OSGeo.GDAL;

/// <summary>
/// Throughput probe: how many parcel-sized windows per second can one process read from a remote COG?
/// Windows are random across the 110 km tile, which is a pessimistic case: real farms cluster and share blocks.
/// Usage: dotnet run -- --bench 96
/// </summary>
internal static class Bench
{
    private const int Size = 10980;
    private const int WindowW = 27, WindowH = 26;   // the spike parcel's window

    public static int Run(string sceneBase, int count)
    {
        var rng = new Random(42);
        var all = Enumerable.Range(0, count).Select(_ => (X: rng.Next(0, Size - 40), Y: rng.Next(0, Size - 40))).ToArray();
        var url = $"/vsicurl/{sceneBase}/B04.tif";

        Part("sequential, 1 thread", url, all.Take(Math.Max(8, count / 4)).ToArray(), 1);
        Part("parallel, 8 threads", url, all, 8);
        Console.WriteLine("Note: one network, one run, cold cache. An indication, not a guarantee.");
        return 0;
    }

    private static void Part(string label, string url, (int X, int Y)[] windows, int threads)
    {
        long openMs = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(
            windows,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            () =>
            {
                var t = Stopwatch.StartNew();
                var ds = Gdal.Open(url, Access.GA_ReadOnly);   // one handle per thread
                Interlocked.Add(ref openMs, t.ElapsedMilliseconds);
                return ds;
            },
            (w, _, ds) =>
            {
                var buf = new int[WindowW * WindowH];
                using var band = ds.GetRasterBand(1);
                band.ReadRaster(w.X, w.Y, WindowW, WindowH, buf, WindowW, WindowH, 0, 0);
                return ds;
            },
            ds => ds.Dispose());
        sw.Stop();

        var perSec = windows.Length / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{label}: {windows.Length} windows in {sw.Elapsed.TotalSeconds:F1} s = {perSec:F1} windows/s " +
                          $"({1000.0 / perSec:F0} ms each on average; {openMs} ms of that was opening handles)");
    }
}
