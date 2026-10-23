using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoNexus.Services;

/// <summary>
/// Testes na placa de vídeo pelo Direct3D 11 (funciona em NVIDIA, AMD e Intel, sem instalar nada):
/// - Computação: todos os núcleos da GPU repetem um cálculo e o resultado é conferido com a primeira rodada
///   (resultado diferente = placa instável, típico de overclock alto ou placa com defeito), como o OCCT.
/// - VRAM: enche a memória de vídeo com um padrão, lê de volta e conta cada valor errado.
/// </summary>
public sealed class GpuTester : IDisposable
{
    private const int ComputeElements = 1 << 20;   // 1 M threads, 16 MB de float4
    private const int ComputeIterations = 2048;
    private const double FlopsPerThread = ComputeIterations * 4 * 4 * 2.0; // 4 mad × 4 lanes × 2 operações
    private const long VramBlockBytes = 128L << 20; // blocos de 128 MB (limite seguro de recurso do D3D11)

    private const string ComputeHlsl = """
        RWStructuredBuffer<float4> Data : register(u0);
        [numthreads(256, 1, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.x;
            float4 x = float4(i % 97, i % 89, i % 83, i % 79) * 0.01 + 1.0;
            float4 y = x * 0.5;
            [loop] for (uint r = 0; r < ITER; r++)
            {
                x = mad(x, 0.999999, 0.5);
                y = mad(y, 1.0000003, -0.25);
                x = mad(x, 1.0000003, -0.25);
                y = mad(y, 0.999999, 0.5);
            }
            Data[i] = x + y;
        }
        """;

    private const string VramHlsl = """
        cbuffer P : register(b0) { uint Seed; uint Count; uint Stride; uint Pad; };
        RWStructuredBuffer<uint> Mem : register(u0);
        RWStructuredBuffer<uint> Err : register(u1);
        uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }
        [numthreads(256, 1, 1)]
        void Write(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.y * Stride + id.x;
            if (i < Count) Mem[i] = Hash(i ^ Seed);
        }
        [numthreads(256, 1, 1)]
        void Check(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.y * Stride + id.x;
            if (i < Count && Mem[i] != Hash(i ^ Seed)) InterlockedAdd(Err[0], 1);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private long _errors;
    private long _work;

    public string AdapterName { get; }
    public ulong DedicatedVram { get; }
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>Rodadas completas (computação) ou passadas pela VRAM.</summary>
    public long Work => Interlocked.Read(ref _work);
    /// <summary>Velocidade medida na última rodada: GFLOPS (computação) ou GB/s (VRAM).</summary>
    public double Rate { get; private set; }
    public ulong VramTested { get; private set; }

    private GpuTester(ID3D11Device device, ID3D11DeviceContext context, string name, ulong vram)
    {
        _device = device;
        _context = context;
        AdapterName = name;
        DedicatedVram = vram;
    }

    /// <summary>Usa a placa com mais memória de vídeo (a dedicada, em PCs com vídeo integrado + placa).</summary>
    public static GpuTester Create()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? best = null;
        ulong bestVram = 0;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0 || (ulong)desc.DedicatedVideoMemory < bestVram)
            {
                adapter.Dispose();
                continue;
            }
            best?.Dispose();
            best = adapter;
            bestVram = (ulong)desc.DedicatedVideoMemory;
        }
        if (best == null) throw new InvalidOperationException("Nenhuma placa de vídeo com Direct3D 11 encontrada.");

        using (best)
        {
            var result = D3D11.D3D11CreateDevice(best, DriverType.Unknown, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
            if (result.Failure || device == null || context == null)
                throw new InvalidOperationException("A placa de vídeo não aceitou o Direct3D 11 (driver desatualizado?).");
            return new GpuTester(device, context, best.Description1.Description.Trim(), bestVram);
        }
    }

    private ID3D11ComputeShader Shader(string source, string entry)
    {
        // Sem "macros": a lista precisa terminar num item vazio e o D3DCompile falha sem ele.
        var code = Compiler.Compile(source, entry, entry + ".hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
        return _device.CreateComputeShader(code.Span);
    }

    private ID3D11Buffer StructuredBuffer(long elements, int stride) =>
        _device.CreateBuffer(new BufferDescription((uint)(elements * stride), BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));

    private ID3D11Buffer Staging(long bytes) =>
        _device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

    private ID3D11UnorderedAccessView Uav(ID3D11Buffer buffer, long elements) =>
        _device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements },
        });

    // ---------- Computação ----------

    /// <summary>Roda até cancelar. Cada lote leva ~0,3 s para não acionar o "reset" de driver do Windows (TDR).</summary>
    public void RunCompute(CancellationToken ct)
    {
        using var shader = Shader(ComputeHlsl.Replace("ITER", ComputeIterations.ToString()), "Main");
        using var buffer = StructuredBuffer(ComputeElements, 16);
        using var uav = Uav(buffer, ComputeElements);
        using var staging = Staging(ComputeElements * 16L);
        _context.CSSetShader(shader);
        _context.CSSetUnorderedAccessView(0, uav);

        float[]? reference = null;
        var current = new float[ComputeElements * 4];
        var batch = 1;
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < batch; i++) _context.Dispatch(ComputeElements / 256, 1, 1);
            _context.CopyResource(staging, buffer);
            var map = _context.Map(staging, 0, MapMode.Read);
            Marshal.Copy(map.DataPointer, current, 0, current.Length);
            _context.Unmap(staging, 0);
            sw.Stop();

            if (reference == null)
            {
                reference = (float[])current.Clone();
            }
            else
            {
                var wrong = 0;
                for (var i = 0; i < current.Length; i++)
                    if (BitConverter.SingleToInt32Bits(current[i]) != BitConverter.SingleToInt32Bits(reference[i])) wrong++;
                if (wrong > 0) Interlocked.Add(ref _errors, wrong);
            }
            Interlocked.Add(ref _work, batch);
            Rate = batch * (double)ComputeElements * FlopsPerThread / sw.Elapsed.TotalSeconds / 1e9;
            if (sw.Elapsed.TotalMilliseconds < 300 && batch < 256) batch *= 2;
            else if (sw.Elapsed.TotalMilliseconds > 900 && batch > 1) batch /= 2;
        }
        _context.CSSetUnorderedAccessView(0, null);
        _context.CSSetShader(null);
    }

    // ---------- VRAM ----------

    /// <summary>Preenche até 80% da memória de vídeo e confere passada por passada, até cancelar.</summary>
    public void RunVram(CancellationToken ct, Action<string>? progress = null)
    {
        using var write = Shader(VramHlsl, "Write");
        using var check = Shader(VramHlsl, "Check");
        using var parameters = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        using var errBuffer = StructuredBuffer(1, 4);
        using var errUav = Uav(errBuffer, 1);
        using var errStaging = Staging(4);

        // Vai alocando blocos até o limite ou até o driver recusar.
        var target = DedicatedVram > 0 ? (long)(DedicatedVram * 0.8) : 1L << 30;
        var blocks = new List<(ID3D11Buffer Buffer, ID3D11UnorderedAccessView Uav)>();
        try
        {
            while ((blocks.Count + 1) * VramBlockBytes <= target && !ct.IsCancellationRequested)
            {
                try
                {
                    var b = StructuredBuffer(VramBlockBytes / 4, 4);
                    blocks.Add((b, Uav(b, VramBlockBytes / 4)));
                }
                catch { break; }
            }
            if (blocks.Count == 0) throw new InvalidOperationException("Não foi possível reservar memória de vídeo para o teste.");
            VramTested = (ulong)(blocks.Count * VramBlockBytes);
            progress?.Invoke($"Testando {SoNexus.Models.Format.Bytes(VramTested)} de memória de vídeo");

            const uint count = (uint)(VramBlockBytes / 4);
            var groups = (count + 255) / 256;
            var groupsX = Math.Min(groups, 65535u);
            var groupsY = (groups + groupsX - 1) / groupsX;
            uint seed = 0x9E3779B9;
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                seed = seed * 1664525 + 1013904223;
                _context.UpdateSubresource(new[] { seed, count, groupsX * 256, 0u }, parameters);
                _context.UpdateSubresource(new[] { 0u }, errBuffer);
                _context.CSSetConstantBuffer(0, parameters);
                _context.CSSetUnorderedAccessView(1, errUav);

                _context.CSSetShader(write);
                foreach (var (_, uav) in blocks)
                {
                    _context.CSSetUnorderedAccessView(0, uav);
                    _context.Dispatch(groupsX, groupsY, 1);
                }
                _context.CSSetShader(check);
                foreach (var (_, uav) in blocks)
                {
                    _context.CSSetUnorderedAccessView(0, uav);
                    _context.Dispatch(groupsX, groupsY, 1);
                }
                _context.CopyResource(errStaging, errBuffer);
                var map = _context.Map(errStaging, 0, MapMode.Read);
                var wrong = Marshal.ReadInt32(map.DataPointer);
                _context.Unmap(errStaging, 0);
                sw.Stop();

                if (wrong > 0) Interlocked.Add(ref _errors, wrong);
                Interlocked.Increment(ref _work);
                Rate = VramTested * 2.0 / sw.Elapsed.TotalSeconds / 1e9; // escreve e lê tudo
            }
        }
        finally
        {
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetUnorderedAccessView(1, null);
            _context.CSSetShader(null);
            foreach (var (b, u) in blocks)
            {
                u.Dispose();
                b.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _device.Dispose();
    }
}
