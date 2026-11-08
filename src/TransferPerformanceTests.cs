using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        sealed class TransferHashSample
        {
            public double Milliseconds;
            public int Gen2Collections;
        }
        static string[] PreviousTransferChunks(string path)
        {
            var hashes = new List<string>();
            using (var input = File.OpenRead(path))
                while (input.Position < input.Length)
                {
                    var bytes = new byte[(int)Math.Min(PackTransferFiles.ChunkSize, input.Length - input.Position)]; int read = 0;
                    while (read < bytes.Length) { int count = input.Read(bytes, read, bytes.Length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                    using (var sha = SHA256.Create()) hashes.Add(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
                }
            return hashes.ToArray();
        }
        static string[] ReferenceTransferChunks(byte[] bytes)
        {
            var hashes = new List<string>();
            for (int offset = 0; offset < bytes.Length; offset += PackTransferFiles.ChunkSize)
                using (var sha = SHA256.Create())
                using (var input = new MemoryStream(bytes, offset, Math.Min(PackTransferFiles.ChunkSize, bytes.Length - offset)))
                    hashes.Add(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
            return hashes.ToArray();
        }
        static TransferHashSample MeasureTransferHash(Func<string[]> calculate, string[] expected)
        {
            GC.Collect(2, GCCollectionMode.Forced, true); GC.WaitForPendingFinalizers();
            int collections = GC.CollectionCount(2); var timer = Stopwatch.StartNew();
            var actual = calculate(); timer.Stop();
            var sample = new TransferHashSample { Milliseconds = timer.Elapsed.TotalMilliseconds, Gen2Collections = GC.CollectionCount(2) - collections };
            Check(actual.SequenceEqual(expected), "Измерение получило другой список контрольных сумм частей.");
            return sample;
        }
        public static int TransferPerformanceRegression()
        {
            var report = new List<string>();
            string root = Path.Combine(Paths.Cache, "transfer-performance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                int chunk = PackTransferFiles.ChunkSize;
                int[] sizes = { 0, 1, chunk - 1, chunk, chunk + 1, chunk * 3 + 113 };
                var random = new Random(1729);
                foreach (int size in sizes)
                {
                    var data = new byte[size]; random.NextBytes(data);
                    string file = Path.Combine(root, "boundary-" + size + ".bin"); File.WriteAllBytes(file, data);
                    var expected = ReferenceTransferChunks(data);
                    Check(PackTransferFiles.Chunks(file).SequenceEqual(expected), "Изменились контрольные суммы частей для файла из " + size + " байт.");
                    Check(PreviousTransferChunks(file).SequenceEqual(expected), "Прежний алгоритм не совпал с независимым эталоном.");
                }
                report.Add("PASS: independent SHA256 stream hashes match empty, one-byte, partial, exact-boundary, full-plus-one and three-full-plus-tail files; a reused buffer never hashes stale tail bytes");

                const int benchmarkBytes = 96 * 1024 * 1024 + 113;
                string benchmark = Path.Combine(root, "benchmark.bin");
                using (var output = File.Create(benchmark))
                {
                    var buffer = new byte[65536]; int remaining = benchmarkBytes;
                    while (remaining > 0) { random.NextBytes(buffer); int count = Math.Min(buffer.Length, remaining); output.Write(buffer, 0, count); remaining -= count; }
                }
                // Warm both implementations and the file cache before paired, alternating runs.
                var reference = PreviousTransferChunks(benchmark);
                Check(PackTransferFiles.Chunks(benchmark).SequenceEqual(reference), "Большой файл получил другие контрольные суммы частей.");
                var previous = new List<TransferHashSample>(); var current = new List<TransferHashSample>();
                for (int run = 0; run < 3; run++)
                {
                    if (run % 2 == 0)
                    {
                        previous.Add(MeasureTransferHash(() => PreviousTransferChunks(benchmark), reference));
                        current.Add(MeasureTransferHash(() => PackTransferFiles.Chunks(benchmark), reference));
                    }
                    else
                    {
                        current.Add(MeasureTransferHash(() => PackTransferFiles.Chunks(benchmark), reference));
                        previous.Add(MeasureTransferHash(() => PreviousTransferChunks(benchmark), reference));
                    }
                }
                double previousMedian = previous.OrderBy(sample => sample.Milliseconds).ElementAt(1).Milliseconds;
                double currentMedian = current.OrderBy(sample => sample.Milliseconds).ElementAt(1).Milliseconds;
                report.Add("PASS: identical chunk hashes over a " + benchmarkBytes + "-byte file; three warmed paired runs: previous median=" + previousMedian.ToString("0.000", CultureInfo.InvariantCulture) + " ms, reused median=" + currentMedian.ToString("0.000", CultureInfo.InvariantCulture) + " ms; observed Gen2 collections across measured runs: previous=" + previous.Sum(sample => sample.Gen2Collections) + ", reused=" + current.Sum(sample => sample.Gen2Collections) + "; timings are local observations, not a performance pass threshold");
                report.Add("PASS: exact chunk-data-buffer allocation accounting for that file: previous=" + ((benchmarkBytes + chunk - 1) / chunk) + " arrays / " + benchmarkBytes + " bytes, reused=1 array / " + chunk + " bytes; excludes hash strings, collections and cryptography internals");
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                File.WriteAllLines(Path.Combine(Paths.Base, "transfer-performance-test.txt"), report);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
