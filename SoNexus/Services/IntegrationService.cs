using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SoNexus.Services;

public sealed record RgbMode(int Index, string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax, uint ColorsMin, uint ColorsMax,
    uint Speed, uint Direction, uint ColorMode, uint[] Colors)
{
    public const uint PerLed = 1, ModeSpecific = 2;
    private const uint HasSpeed = 1 << 0, ManualSave = 1 << 7;

    public bool SupportsSpeed => (Flags & HasSpeed) != 0 && SpeedMin != SpeedMax;
    public bool SupportsColor => ColorMode == ModeSpecific && ColorsMax > 0;
    /// <summary>O aparelho aceita gravar este modo na própria memória.</summary>
    public bool CanSave => (Flags & ManualSave) != 0;

    /// <summary>Nome em português para os modos mais comuns do OpenRGB.</summary>
    public string Display => Name switch
    {
        "Direct" => "Direto (controlado pelo SoNexus)",
        "Off" => "Desligado",
        "Static" => "Estático",
        "Breathing" => "Respirar",
        "Flashing" => "Piscar",
        "Spectrum Cycle" => "Ciclo de cores",
        "Rainbow" => "Arco-íris",
        "Rainbow Wave" or "Spectrum Cycle Wave" => "Onda arco-íris",
        "Color Wave" => "Onda de cor",
        "Chase" => "Perseguição",
        "Chase Fade" => "Perseguição suave",
        "Random Flicker" => "Cintilar aleatório",
        "Gentle Transition" => "Transição suave",
        "Comet" => "Cometa",
        "Starry Night" => "Noite estrelada",
        "Music" => "Música (do aparelho)",
        "Spectrum Cycle Breathing" => "Ciclo de cores respirando",
        "Spectrum Cycle Chase Fade" => "Ciclo de cores, perseguição suave",
        "Spectrum Cycle Chase" => "Ciclo de cores, perseguição",
        "Chase Rainbow Pulse" => "Pulso arco-íris",
        "Wave Propagation" => "Propagação de onda",
        "Wave Propagation Pause" => "Propagação de onda com pausa",
        "Red Pulse" => "Pulso vermelho",
        _ => Name,
    };

    public override string ToString() => Display;
}

/// <summary>Zona de LEDs. Saídas ARGB da placa-mãe vêm com 0 LEDs até alguém dizer quantos têm (LedsMax &gt; 0 = dá para ajustar).</summary>
public sealed record RgbZone(int Index, string Name, int LedsMin, int LedsMax, int LedsCount)
{
    public bool NeedsSize => LedsCount == 0 && LedsMax > 0;
}

public sealed record RgbDevice(int Index, string Name, string Type, string Location, int Leds, string Zones, string Mode)
{
    public List<RgbMode> Modes { get; init; } = new();
    public List<RgbZone> ZoneList { get; init; } = new();
    public int ActiveMode { get; init; } = -1;
    public RgbMode? Active => ActiveMode >= 0 && ActiveMode < Modes.Count ? Modes[ActiveMode] : null;
    public int EmptyZones => ZoneList.Count(z => z.NeedsSize);

    // Listas e caixas de seleção mostram o nome (o padrão de "record" mostraria todos os campos).
    public override string ToString() => Name;
}

/// <summary>
/// Cliente do servidor SDK do OpenRGB (porta 6742). O OpenRGB controla os LEDs de quase todas as marcas
/// (placa-mãe, RAM, placa de vídeo, fitas, cooler...) e o SoNexus manda as cores para ele.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint SetClientName = 50;
    private const uint ResizeZone = 1000;
    private const uint UpdateLeds = 1050;
    private const uint SetCustomMode = 1100;
    private const uint UpdateMode = 1101;

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public bool Connected => _tcp?.Connected == true;

    public async Task<bool> ConnectAsync(string host = "127.0.0.1", int port = 6742)
    {
        try
        {
            _tcp = new TcpClient();
            var connect = _tcp.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(1500)) != connect || !_tcp.Connected) return false;
            _stream = _tcp.GetStream();
            Send(0, SetClientName, Encoding.ASCII.GetBytes("SoNexus\0"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public List<RgbDevice> Devices()
    {
        Send(0, RequestControllerCount, Array.Empty<byte>());
        var count = BitConverter.ToInt32(Receive(RequestControllerCount), 0);
        var list = new List<RgbDevice>();
        for (var i = 0; i < count; i++)
        {
            Send((uint)i, RequestControllerData, Array.Empty<byte>());
            try { list.Add(Parse(i, Receive(RequestControllerData))); }
            catch { list.Add(new RgbDevice(i, $"Dispositivo {i}", "?", "", 0, "", "")); }
        }
        return list;
    }

    public RgbDevice Device(int index)
    {
        Send((uint)index, RequestControllerData, Array.Empty<byte>());
        return Parse(index, Receive(RequestControllerData));
    }

    /// <summary>
    /// Pinta o dispositivo inteiro de uma cor. Pede o modo "Direto/Personalizado" e confere o que o aparelho aceitou:
    /// com cor por LED manda os LEDs; se só existe modo "Estático" (cor do modo), manda a cor no próprio modo.
    /// Retorna false quando o aparelho não aceita cor por software.
    /// </summary>
    public bool SetColor(RgbDevice device, byte r, byte g, byte b)
    {
        var color = (uint)(r | (g << 8) | (b << 16));
        // Saídas ARGB com 0 LEDs nunca acendem com cor por LED: configura antes (vale também nas próximas vezes).
        if (device.EmptyZones > 0) SizeEmptyZones(device);
        Send((uint)device.Index, SetCustomMode, Array.Empty<byte>());
        var now = Device(device.Index);

        if (now.Active?.ColorMode == RgbMode.PerLed && now.Leds > 0)
        {
            SendLeds(now, color);
            return true;
        }
        // Sem "Direto": procura um modo com cor própria (Estático/Static) e usa ele.
        var mode = now.Active?.ColorMode == RgbMode.ModeSpecific ? now.Active
                 : now.Modes.FirstOrDefault(m => m.ColorMode == RgbMode.ModeSpecific && m.Name.Contains("Static", StringComparison.OrdinalIgnoreCase))
                   ?? now.Modes.FirstOrDefault(m => m.ColorMode == RgbMode.ModeSpecific);
        if (mode != null)
        {
            var count = Math.Max(1, (int)Math.Max(mode.ColorsMin, (uint)mode.Colors.Length));
            SendMode(device.Index, mode with { Colors = Enumerable.Repeat(color, count).ToArray() });
            return true;
        }
        if (now.Leds > 0)
        {
            SendLeds(now, color);
            return true;
        }
        return false;
    }

    private void SendLeds(RgbDevice device, uint color) => SendLeds(device.Index, Enumerable.Repeat(color, device.Leds).ToArray());

    /// <summary>Uma cor por LED (efeitos como onda e arco-íris).</summary>
    public void SendLeds(int device, uint[] colors)
    {
        var payload = new byte[4 + 2 + 4 * colors.Length];
        BitConverter.GetBytes((uint)payload.Length).CopyTo(payload, 0);
        BitConverter.GetBytes((ushort)colors.Length).CopyTo(payload, 4);
        Buffer.BlockCopy(colors, 0, payload, 6, colors.Length * 4);
        Send((uint)device, UpdateLeds, payload);
    }

    /// <summary>Pede o modo "Direto" (cor controlada pelo programa).</summary>
    public void SetCustom(int device) => Send((uint)device, SetCustomMode, Array.Empty<byte>());

    /// <summary>Grava o modo na memória do próprio aparelho: fica mesmo com o PC desligado/sem programas.</summary>
    public void SaveMode(int device, RgbMode m) => SendModePacket(device, m, SaveModeId);

    private const uint SaveModeId = 1102;

    /// <summary>
    /// Descarta avisos que o OpenRGB manda sozinho (ex.: "lista de dispositivos mudou").
    /// Sem isso, rodando por horas, o buffer da conexão enche.
    /// </summary>
    public void Drain()
    {
        try
        {
            var buffer = new byte[4096];
            while (_stream is { DataAvailable: true }) _stream.Read(buffer, 0, buffer.Length);
        }
        catch { }
    }

    /// <summary>Pacote UPDATEMODE no formato do protocolo 0 (o padrão quando o cliente não negocia versão).</summary>
    public void SendMode(int device, RgbMode m) => SendModePacket(device, m, UpdateMode);

    private void SendModePacket(int device, RgbMode m, uint packet)
    {
        var mode = new MemoryStream();
        var w = new BinaryWriter(mode);
        w.Write(m.Index);
        var name = Encoding.UTF8.GetBytes(m.Name + "\0");
        w.Write((ushort)name.Length);
        w.Write(name);
        w.Write(m.Value);
        foreach (var v in new[] { m.Flags, m.SpeedMin, m.SpeedMax, m.ColorsMin, m.ColorsMax, m.Speed, m.Direction, m.ColorMode }) w.Write(v);
        w.Write((ushort)m.Colors.Length);
        foreach (var c in m.Colors) w.Write(c);
        var payload = new byte[4 + mode.Length];
        BitConverter.GetBytes((uint)payload.Length).CopyTo(payload, 0);
        mode.ToArray().CopyTo(payload, 4);
        Send((uint)device, packet, payload);
    }

    /// <summary>
    /// Saídas ARGB (ventoinhas, fitas) com 0 LEDs: define um tamanho para elas passarem a acender.
    /// LED a mais é ignorado pela fita, então usamos o máximo da saída (limitado a 60, ~3 ventoinhas).
    /// </summary>
    public int SizeEmptyZones(RgbDevice device)
    {
        var fixedZones = 0;
        foreach (var z in device.ZoneList.Where(z => z.NeedsSize))
        {
            var size = Math.Clamp(Math.Min(z.LedsMax, 60), Math.Max(1, z.LedsMin), z.LedsMax);
            var payload = new byte[8];
            BitConverter.GetBytes(z.Index).CopyTo(payload, 0);
            BitConverter.GetBytes(size).CopyTo(payload, 4);
            Send((uint)device.Index, ResizeZone, payload);
            fixedZones++;
        }
        return fixedZones;
    }

    private static readonly string[] Types =
    {
        "Placa-mãe", "Memória RAM", "Placa de vídeo", "Cooler", "Fita de LED", "Teclado", "Mouse", "Mousepad", "Headset",
        "Suporte de headset", "Controle", "Luz", "Caixa de som", "Virtual", "Armazenamento", "Gabinete", "Microfone", "Acessório", "Teclado numérico",
    };

    /// <summary>Lê o pacote de dados do controlador (formato do protocolo versão 0).</summary>
    private static RgbDevice Parse(int index, byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data));
        r.ReadUInt32(); // tamanho
        var type = r.ReadInt32();
        var name = Str(r);
        Str(r); // descrição
        Str(r); // versão
        Str(r); // serial
        var location = Str(r);

        var modeCount = r.ReadUInt16();
        var active = r.ReadInt32();
        var modes = new List<RgbMode>();
        for (var m = 0; m < modeCount; m++)
        {
            var modeName = Str(r);
            var value = r.ReadInt32();
            var f = new uint[8]; // flags, speed min/max, colors min/max, speed, direction, color mode
            for (var i = 0; i < 8; i++) f[i] = r.ReadUInt32();
            var colors = new uint[r.ReadUInt16()];
            for (var i = 0; i < colors.Length; i++) colors[i] = r.ReadUInt32();
            modes.Add(new RgbMode(m, modeName, value, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], colors));
        }

        var zoneCount = r.ReadUInt16();
        var zones = new List<RgbZone>();
        for (var z = 0; z < zoneCount; z++)
        {
            var zoneName = Str(r);
            r.ReadInt32(); // tipo
            var min = r.ReadInt32();
            var max = r.ReadInt32();
            var count = r.ReadInt32();
            var matrix = r.ReadUInt16();
            r.ReadBytes(matrix);
            zones.Add(new RgbZone(z, zoneName, min, max, count));
        }

        var leds = r.ReadUInt16();
        var zoneText = string.Join(", ", zones.Select(z => z.NeedsSize ? $"{z.Name} (0 LEDs!)" : z.Name));
        return new RgbDevice(index, name, type >= 0 && type < Types.Length ? Types[type] : "Outro", location, leds,
            zoneText, active >= 0 && active < modes.Count ? modes[active].Name : "")
        {
            Modes = modes, ZoneList = zones, ActiveMode = active,
        };
    }

    private static string Str(BinaryReader r)
    {
        var len = r.ReadUInt16();
        return Encoding.UTF8.GetString(r.ReadBytes(len)).TrimEnd('\0');
    }

    private void Send(uint device, uint id, byte[] payload)
    {
        var header = new byte[16];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(header, 0);
        BitConverter.GetBytes(device).CopyTo(header, 4);
        BitConverter.GetBytes(id).CopyTo(header, 8);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(header, 12);
        _stream!.Write(header);
        if (payload.Length > 0) _stream.Write(payload);
    }

    private byte[] Receive(uint expectedId)
    {
        while (true)
        {
            var header = ReadExact(16);
            var id = BitConverter.ToUInt32(header, 8);
            var size = BitConverter.ToInt32(header, 12);
            var payload = ReadExact(size);
            if (id == expectedId) return payload; // ignora avisos como "lista de dispositivos mudou"
        }
    }

    private byte[] ReadExact(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        _stream!.ReadTimeout = 3000;
        while (read < count)
        {
            var n = _stream.Read(buffer, read, count - read);
            if (n == 0) throw new IOException("O OpenRGB fechou a conexão.");
            read += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}

public sealed record DetectedApp(string Name, string Status, string Detail);

public static class IntegrationService
{
    private static readonly (string Name, string[] Processes, string[] InstallKeys, string Detail)[] RgbApps =
    {
        ("OpenRGB", new[] { "OpenRGB" }, new[] { "OpenRGB" }, "Usado pelo SoNexus para controlar os LEDs."),
        ("ASUS Armoury Crate / Aura", new[] { "ArmouryCrate", "ArmouryCrate.Service", "LightingService", "AacAmbientLighting" }, new[] { "Armoury Crate", "AURA" }, "RGB das placas ASUS."),
        ("Corsair iCUE", new[] { "iCUE" }, new[] { "iCUE" }, "RGB e ventoinhas Corsair."),
        ("SignalRGB", new[] { "SignalRgb", "SignalRgbLauncher" }, new[] { "SignalRGB" }, "Efeitos RGB de várias marcas."),
        ("MSI Center / Mystic Light", new[] { "MSI.CentralServer", "MysticLight" }, new[] { "MSI Center", "Mystic Light" }, "RGB das placas MSI."),
        ("Gigabyte Control Center / RGB Fusion", new[] { "GCC", "RGBFusion" }, new[] { "GIGABYTE Control Center", "RGB Fusion" }, "RGB Gigabyte/Aorus (inclui placas de vídeo Gigabyte)."),
        ("Razer Synapse", new[] { "RazerAppEngine", "Razer Synapse 3" }, new[] { "Razer Synapse" }, "Periféricos Razer."),
        ("Logitech G HUB", new[] { "lghub", "lghub_agent" }, new[] { "G HUB" }, "Periféricos Logitech."),
        ("NZXT CAM", new[] { "NZXT CAM" }, new[] { "NZXT CAM" }, "Coolers, ventoinhas e RGB NZXT."),
        ("Lian Li L-Connect", new[] { "L-Connect 3" }, new[] { "L-Connect" }, "Ventoinhas e RGB Lian Li."),
    };

    public static List<DetectedApp> DetectRgbApps()
    {
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = HiddenItemsService.GetPrograms().Select(p => p.Name).ToList();
        var list = new List<DetectedApp>();
        foreach (var (name, processes, keys, detail) in RgbApps)
        {
            var isRunning = processes.Any(running.Contains);
            var isInstalled = keys.Any(k => installed.Any(i => i.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (isRunning || isInstalled) list.Add(new DetectedApp(name, isRunning ? "Rodando" : "Instalado", detail));
        }
        return list;
    }

    /// <summary>Fecha os programas de RGB dos fabricantes (não desinstala). Voltam no próximo boot.</summary>
    public static List<string> StopVendorRgb()
    {
        var closed = new List<string>();
        foreach (var (name, processes, _, _) in RgbApps.Where(a => a.Name != "OpenRGB"))
        {
            var any = false;
            foreach (var proc in processes)
            foreach (var p in Process.GetProcessesByName(proc))
            {
                using (p)
                {
                    try { p.Kill(true); any = true; } catch { }
                }
            }
            if (any) closed.Add(name);
        }
        // O serviço de iluminação da ASUS reabre o Armoury Crate se ficar rodando.
        if (ProcessRunner.Sc("stop LightingService") == 0 && !closed.Contains("ASUS Armoury Crate / Aura")) closed.Add("ASUS Aura (serviço)");
        return closed;
    }

    // ---------- Iniciar com o Windows ----------

    private const string TaskName = "SoNexus";

    public static async Task<bool> IsStartupEnabledAsync() =>
        (await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"")).ExitCode == 0;

    /// <summary>
    /// Tarefa agendada com privilégio máximo: abre como administrador no login sem pedir confirmação
    /// (necessário para sensores e ventoinhas).
    /// </summary>
    public static async Task<string?> SetStartupAsync(bool enabled)
    {
        var exe = Environment.ProcessPath ?? "";
        // A tarefa abre o .exe como administrador sem perguntar. Se ele estiver numa pasta que qualquer programa
        // altera (Downloads, Área de Trabalho, versão portátil), um vírus trocaria o arquivo e viraria administrador.
        if (enabled && !IsProtectedLocation(exe))
            return "Para iniciar com o Windows, instale o SoNexus pelo instalador (a versão portátil fica numa pasta que outros programas podem alterar).";
        var args = enabled
            ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F"
            : $"/Delete /TN \"{TaskName}\" /F";
        var (code, output, error) = await ProcessRunner.RunAsync("schtasks.exe", args);
        return code == 0 ? null : (error.Length > 0 ? error : output).Trim();
    }

    /// <summary>Arquivos de Programas: só administradores alteram.</summary>
    public static bool IsProtectedLocation(string path)
    {
        var full = Path.GetFullPath(path);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Any(pf => pf.Length > 0 && full.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
