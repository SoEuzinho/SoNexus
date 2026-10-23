using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SoNexus.Services;

public sealed record DiskBenchResult(string Drive, double SeqReadMBs, double SeqWriteMBs, double RandReadMBs, double RandIops);

/// <summary>
/// Velocidade do disco como o CrystalDiskMark (versão curta): escreve 1 GB, lê sequencial e lê blocos
/// de 4 KB em posições aleatórias. Sem cache do Windows (FILE_FLAG_NO_BUFFERING), senão mediria a RAM.
/// </summary>
public static class DiskBenchService
{
    private const int SeqBlock = 4 << 20;
    private const int RandBlock = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static unsafe DiskBenchResult Run(string folder, Action<string> progress, CancellationToken ct, long size = 1L << 30)
    {
        var file = Path.Combine(folder, $"sonexus-disktest-{Environment.ProcessId}.tmp");
        var buffer = (byte*)NativeMemory.AlignedAlloc(SeqBlock, 4096);
        try
        {
            var span = new Span<byte>(buffer, SeqBlock);
            Random.Shared.NextBytes(span); // dados aleatórios: SSDs que comprimem não "trapaceiam"

            progress("Disco: gravando 1 GB...");
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough | NoBuffering))
            {
                for (long done = 0; done < size; done += SeqBlock)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Write(span);
                }
            }
            var write = size / 1e6 / sw.Elapsed.TotalSeconds;

            progress("Disco: lendo 1 GB...");
            sw.Restart();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 1, NoBuffering))
            {
                while (fs.Read(span) > 0) ct.ThrowIfCancellationRequested();
            }
            var read = size / 1e6 / sw.Elapsed.TotalSeconds;

            progress("Disco: leitura aleatória de 4 KB...");
            var small = new Span<byte>(buffer, RandBlock);
            var blocks = size / RandBlock;
            var reads = 0;
            sw.Restart();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 1, NoBuffering | FileOptions.RandomAccess))
            {
                while (sw.Elapsed.TotalSeconds < 5)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Position = Random.Shared.NextInt64(blocks) * RandBlock;
                    fs.ReadExactly(small);
                    reads++;
                }
            }
            var iops = reads / sw.Elapsed.TotalSeconds;
            return new DiskBenchResult(Path.GetPathRoot(folder) ?? folder, read, write, iops * RandBlock / 1e6, iops);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { File.Delete(file); } catch { }
        }
    }
}
