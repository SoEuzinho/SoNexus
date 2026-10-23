using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Um alto-falante, fone, microfone ou dispositivo virtual.</summary>
public sealed class AudioDevice : ObservableObject
{
    private double _peak;
    private int _volume;
    private bool _muted;

    internal AudioDevice(MMDevice device, bool isDefault)
    {
        Device = device;
        Id = device.ID;
        Adapter = Safe(() => device.DeviceFriendlyName, "");
        // "Alto-falantes (Realtek(R) Audio)" -> "Alto-falantes"; o adaptador já aparece embaixo.
        var name = Safe(() => device.FriendlyName, "Dispositivo");
        var suffix = $" ({Adapter})";
        Name = Adapter.Length > 0 && name.EndsWith(suffix) ? name[..^suffix.Length] : name;
        IsInput = device.DataFlow == DataFlow.Capture;
        IsDefault = isDefault;
        Format = ReadFormat(device);
        Latency = ReadLatency(device);
        Refresh();
    }

    internal MMDevice Device { get; }
    public string Id { get; }
    public string Name { get; }
    public string Adapter { get; }
    public bool IsInput { get; }
    public bool IsDefault { get; }
    public string Kind => (IsInput ? "Entrada" : "Saída") + (IsDefault ? " · padrão" : "");
    public string Format { get; }
    public string Latency { get; }
    public bool IsVirtual => AudioService.IsVirtualName(Name + " " + Adapter);

    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public int Volume { get => _volume; set => Set(ref _volume, value); }
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) Raise(nameof(VolumeText)); } }
    public string VolumeText => _muted ? "mudo" : $"{_volume}%";

    internal void Refresh()
    {
        try { Peak = Device.AudioMeterInformation.MasterPeakValue * 100; } catch { }
        try
        {
            Volume = (int)Math.Round(Device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            Muted = Device.AudioEndpointVolume.Mute;
            Raise(nameof(VolumeText));
        }
        catch { }
    }

    private static string ReadFormat(MMDevice d)
    {
        try
        {
            // Formato real do dispositivo (o que está em Som > Avançado).
            if (d.Properties.Contains(PropertyKeys.PKEY_AudioEngine_DeviceFormat)
                && d.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value is byte[] wf && wf.Length >= 16)
            {
                int channels = BitConverter.ToUInt16(wf, 2);
                var rate = BitConverter.ToInt32(wf, 4);
                int bits = BitConverter.ToUInt16(wf, 14);
                if (BitConverter.ToUInt16(wf, 0) == 0xFFFE && wf.Length >= 20) bits = BitConverter.ToUInt16(wf, 18);
                return $"{rate / 1000.0:0.#} kHz · {bits} bits · {channels} canais";
            }
            var mix = d.AudioClient.MixFormat;
            return $"{mix.SampleRate / 1000.0:0.#} kHz · {mix.BitsPerSample} bits · {mix.Channels} canais";
        }
        catch
        {
            return "—";
        }
    }

    private static string ReadLatency(MMDevice d)
    {
        try
        {
            var client = d.AudioClient;
            // Períodos em unidades de 100 ns.
            return $"{client.DefaultDevicePeriod / 10000.0:0.#} ms (mín. {client.MinimumDevicePeriod / 10000.0:0.#} ms)";
        }
        catch
        {
            return "—";
        }
    }

    private static string Safe(Func<string> f, string fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}

/// <summary>Um aplicativo tocando som agora (Discord, jogo, navegador...).</summary>
public sealed class AudioApp : ObservableObject
{
    private double _peak;
    private int _volume;

    internal AudioApp(AudioSessionControl session, string device)
    {
        Session = session;
        Device = device;
        var pid = (int)session.GetProcessID;
        Pid = pid;
        if (session.IsSystemSoundsSession) Name = "Sons do sistema";
        else
        {
            try { using var p = Process.GetProcessById(pid); Name = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.ProcessName; }
            catch { Name = session.DisplayName is { Length: > 0 } n ? n : $"PID {pid}"; }
        }
    }

    internal AudioSessionControl Session { get; }
    public int Pid { get; }
    public string Name { get; } = "";
    public string Device { get; }
    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public int Volume { get => _volume; set => Set(ref _volume, value); }

    internal void Refresh()
    {
        try { Peak = Session.AudioMeterInformation.MasterPeakValue * 100; } catch { }
        try { Volume = (int)Math.Round(Session.SimpleAudioVolume.Volume * 100); } catch { }
    }
}

public sealed record AudioSoftware(string Name, string Status, string Detail);

public sealed record AudioDriver(string Device, string Provider, string Version, string Date);

public static class AudioService
{
    private static readonly string[] VirtualKeywords =
    {
        "VoiceMeeter", "VB-Audio", "CABLE", "Virtual", "Sonar", "NVIDIA Broadcast", "Wave Link", "Steam Streaming", "Krisp",
    };

    public static bool IsVirtualName(string text) =>
        VirtualKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static (List<AudioDevice> Devices, List<AudioApp> Apps) Load()
    {
        var devices = new List<AudioDevice>();
        var apps = new List<AudioApp>();
        using var enumerator = new MMDeviceEnumerator();
        string? DefaultId(DataFlow flow)
        {
            try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { return null; }
        }
        var defaultOut = DefaultId(DataFlow.Render);
        var defaultIn = DefaultId(DataFlow.Capture);

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            var device = new AudioDevice(d, d.ID == defaultOut || d.ID == defaultIn);
            devices.Add(device);
            if (device.IsInput) continue;
            try
            {
                var sessions = d.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    // "Sons do sistema" existe em todo dispositivo e só polui a lista.
                    if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    var app = new AudioApp(s, device.Name);
                    if (!apps.Any(a => a.Pid == app.Pid && a.Device == app.Device)) apps.Add(app);
                }
            }
            catch { }
        }
        // Saídas antes de entradas; em cada grupo: padrão, depois físicos, depois virtuais.
        return (devices.OrderBy(d => d.IsInput).ThenByDescending(d => d.IsDefault).ThenBy(d => d.IsVirtual).ThenBy(d => d.Name).ToList(), apps);
    }

    public static int InactiveCount()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Disabled | DeviceState.Unplugged).Count;
        }
        catch
        {
            return 0;
        }
    }

    public static List<AudioDriver> Drivers()
    {
        var list = new List<AudioDriver>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass='MEDIA' OR DeviceClass='AUDIOENDPOINT'");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["DeviceClass"]?.ToString() == "AUDIOENDPOINT") continue;
                    var date = "";
                    if (o["DriverDate"]?.ToString() is { Length: > 0 } raw)
                        try { date = ManagementDateTimeConverter.ToDateTime(raw).ToString("dd/MM/yyyy"); } catch { }
                    list.Add(new AudioDriver(o["DeviceName"]?.ToString() ?? "", o["DriverProviderName"]?.ToString() ?? "",
                        o["DriverVersion"]?.ToString() ?? "", date));
                }
            }
        }
        catch { }
        return list.OrderBy(d => d.Device).ToList();
    }

    private static readonly (string Name, string[] Processes, string[] InstallKeys, string Detail)[] KnownSoftware =
    {
        ("Voicemeeter", new[] { "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64" }, new[] { "Voicemeeter" },
            "Mesa de som virtual da VB-Audio. Veja a aba Voicemeeter."),
        ("VB-Cable", Array.Empty<string>(), new[] { "VB-Audio Virtual Cable", "VB-CABLE" }, "Cabo de áudio virtual para passar som de um programa para outro."),
        ("SteelSeries Sonar", new[] { "SteelSeriesSonar", "SteelSeriesGG" }, new[] { "SteelSeries GG" }, "Mixer e equalizador da SteelSeries (cria dispositivos virtuais)."),
        ("NVIDIA Broadcast", new[] { "NVIDIA Broadcast" }, new[] { "NVIDIA Broadcast" }, "Remove ruído do microfone usando a placa de vídeo."),
        ("Elgato Wave Link", new[] { "WaveLink" }, new[] { "Wave Link" }, "Mixer de áudio da Elgato."),
        ("Equalizer APO", Array.Empty<string>(), new[] { "Equalizer APO" }, "Equalizador do sistema inteiro."),
        ("Nahimic", new[] { "NahimicSvc64", "NahimicSvc32", "Nahimic3" }, new[] { "Nahimic" },
            "Efeitos de som de placas MSI/ASUS. Conhecido por causar travamentos e latência; desinstale se tiver problemas."),
        ("Realtek Audio Console", new[] { "RtkUWP", "RAVBg64" }, new[] { "Realtek" }, "Painel do driver de som Realtek da placa-mãe."),
        ("Dolby Atmos / Dolby Access", Array.Empty<string>(), new[] { "Dolby" }, "Som espacial da Dolby."),
        ("DTS Sound Unbound", Array.Empty<string>(), new[] { "DTS Sound", "DTS:X" }, "Som espacial da DTS."),
        ("Krisp", new[] { "krisp" }, new[] { "Krisp" }, "Remove ruído do microfone."),
        ("FxSound", new[] { "FxSound" }, new[] { "FxSound" }, "Melhorador de som com equalizador."),
        ("ASIO4ALL", Array.Empty<string>(), new[] { "ASIO4ALL" }, "Driver ASIO genérico para baixa latência em produção musical."),
    };

    public static List<AudioSoftware> DetectSoftware(IEnumerable<AudioDevice> devices)
    {
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = HiddenItemsService.GetPrograms().Select(p => p.Name).ToList();
        var deviceNames = devices.Select(d => d.Name + " " + d.Adapter).ToList();

        var list = new List<AudioSoftware>();
        foreach (var (name, processes, keys, detail) in KnownSoftware)
        {
            var isRunning = processes.Any(running.Contains);
            var isInstalled = keys.Any(k => installed.Any(i => i.Contains(k, StringComparison.OrdinalIgnoreCase)))
                              || (name == "Equalizer APO" && Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO")));
            var hasDevice = keys.Any(k => deviceNames.Any(d => d.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            || (name == "Voicemeeter" && deviceNames.Any(d => d.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)));
            if (!isRunning && !isInstalled && !hasDevice) continue;
            var status = isRunning ? "Rodando" : hasDevice ? "Dispositivo ativo" : "Instalado";
            list.Add(new AudioSoftware(name, status, detail));
        }
        return list;
    }
}

// ---------- Voicemeeter Remote API ----------

public sealed class VmChannel : ObservableObject
{
    private string _label = "";
    private string _device = "";
    private double _gain;
    private bool _mute;
    private double _level;

    public VmChannel(int index, bool isBus, bool isVirtual)
    {
        Index = index;
        IsBus = isBus;
        IsVirtual = isVirtual;
    }

    public int Index { get; }
    public bool IsBus { get; }
    public bool IsVirtual { get; }
    public string DefaultName => IsBus
        ? (IsVirtual ? $"B{Index - VoicemeeterRemote.HardwareBuses + 1}" : $"A{Index + 1}")
        : (IsVirtual ? $"Entrada virtual {Index + 1}" : $"Entrada {Index + 1}");

    public string Label { get => _label; set { if (Set(ref _label, value)) Raise(nameof(Title)); } }
    public string Title => _label.Length > 0 ? _label : DefaultName;
    public string Device { get => _device; set => Set(ref _device, value); }
    public double Gain { get => _gain; set { if (Set(ref _gain, value)) Raise(nameof(GainText)); } }
    public string GainText => $"{_gain:+0.0;-0.0;0.0} dB";
    public bool Mute { get => _mute; set { if (Set(ref _mute, value)) Raise(nameof(MuteText)); } }
    public string MuteText => _mute ? "MUDO" : "ativo";
    /// <summary>Nível de 0 a 100 (escala de -60 dB a 0 dB).</summary>
    public double Level { get => _level; set => Set(ref _level, value); }
}

/// <summary>Conversa com o Voicemeeter pela DLL oficial de controle remoto (VoicemeeterRemote64.dll).</summary>
public sealed class VoicemeeterRemote : IDisposable
{
    public static int HardwareBuses { get; private set; } = 1;

    private IntPtr _lib;
    private bool _logged;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LoginFn();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntOutFn(out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DirtyFn();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFloatFn([MarshalAs(UnmanagedType.LPStr)] string name, out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFloatFn([MarshalAs(UnmanagedType.LPStr)] string name, float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetStringWFn([MarshalAs(UnmanagedType.LPStr)] string name, [Out] ushort[] value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLevelFn(int type, int channel, out float value);

    private LoginFn? _login, _logout;
    private IntOutFn? _type, _version;
    private DirtyFn? _dirty;
    private GetFloatFn? _getFloat;
    private SetFloatFn? _setFloat;
    private GetStringWFn? _getString;
    private GetLevelFn? _getLevel;

    public string Status { get; private set; } = "";
    public string Edition { get; private set; } = "";
    public string Version { get; private set; } = "";
    public List<VmChannel> Strips { get; } = new();
    public List<VmChannel> Buses { get; } = new();
    public bool Connected { get; private set; }

    private int _hwStrips, _vStrips, _hwBuses, _vBuses;

    public static string? FindDll()
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames().Where(n => n.StartsWith("VB:Voicemeeter", StringComparison.OrdinalIgnoreCase)))
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("UninstallString") is string u)
                {
                    var dir = Path.GetDirectoryName(u.Trim('"'));
                    var dll = dir == null ? null : Path.Combine(dir, "VoicemeeterRemote64.dll");
                    if (dll != null && File.Exists(dll)) return dll;
                }
            }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"VB\Voicemeeter\VoicemeeterRemote64.dll");
        return File.Exists(fallback) ? fallback : null;
    }

    public bool Connect()
    {
        var dll = FindDll();
        if (dll == null)
        {
            Status = "O Voicemeeter não está instalado. Baixe em vb-audio.com (é gratuito).";
            return false;
        }
        try
        {
            _lib = System.Runtime.InteropServices.NativeLibrary.Load(dll);
            T Fn<T>(string name) where T : Delegate =>
                System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(System.Runtime.InteropServices.NativeLibrary.GetExport(_lib, name));
            _login = Fn<LoginFn>("VBVMR_Login");
            _logout = Fn<LoginFn>("VBVMR_Logout");
            _type = Fn<IntOutFn>("VBVMR_GetVoicemeeterType");
            _version = Fn<IntOutFn>("VBVMR_GetVoicemeeterVersion");
            _dirty = Fn<DirtyFn>("VBVMR_IsParametersDirty");
            _getFloat = Fn<GetFloatFn>("VBVMR_GetParameterFloat");
            _setFloat = Fn<SetFloatFn>("VBVMR_SetParameterFloat");
            _getString = Fn<GetStringWFn>("VBVMR_GetParameterStringW");
            _getLevel = Fn<GetLevelFn>("VBVMR_GetLevel");

            var r = _login();
            if (r < 0)
            {
                Status = $"Não foi possível conectar (código {r}).";
                return false;
            }
            _logged = true;
            Thread.Sleep(100);
            if (_type(out var type) != 0)
            {
                Status = "O Voicemeeter está instalado, mas não está aberto. Abra o Voicemeeter e clique em Conectar.";
                return false;
            }
            (Edition, _hwStrips, _vStrips, _hwBuses, _vBuses) = type switch
            {
                1 => ("Voicemeeter", 2, 1, 1, 1),
                2 => ("Voicemeeter Banana", 3, 2, 3, 2),
                _ => ("Voicemeeter Potato", 5, 3, 5, 3),
            };
            HardwareBuses = _hwBuses;
            if (_version(out var v) == 0) Version = $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

            for (var i = 0; i < _hwStrips + _vStrips; i++) Strips.Add(new VmChannel(i, false, i >= _hwStrips));
            for (var i = 0; i < _hwBuses + _vBuses; i++) Buses.Add(new VmChannel(i, true, i >= _hwBuses));
            Connected = true;
            Status = $"Conectado ao {Edition} {Version}";
            RefreshParameters(force: true);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Erro ao conectar: {ex.Message}";
            return false;
        }
    }

    public void RefreshParameters(bool force = false)
    {
        if (!Connected || _dirty == null) return;
        if (_dirty() != 1 && !force) return;
        foreach (var s in Strips)
        {
            s.Label = GetString($"Strip[{s.Index}].Label");
            if (!s.IsVirtual) s.Device = GetString($"Strip[{s.Index}].device.name");
            s.Gain = GetFloat($"Strip[{s.Index}].Gain");
            s.Mute = GetFloat($"Strip[{s.Index}].Mute") > 0.5;
        }
        foreach (var b in Buses)
        {
            b.Label = GetString($"Bus[{b.Index}].Label");
            if (!b.IsVirtual) b.Device = GetString($"Bus[{b.Index}].device.name");
            b.Gain = GetFloat($"Bus[{b.Index}].Gain");
            b.Mute = GetFloat($"Bus[{b.Index}].Mute") > 0.5;
        }
    }

    public void RefreshLevels()
    {
        if (!Connected || _getLevel == null) return;
        // Entradas: cada entrada física tem 2 canais, cada virtual 8. Saídas: 8 canais por bus.
        var channel = 0;
        foreach (var s in Strips)
        {
            var count = s.IsVirtual ? 8 : 2;
            s.Level = ToPercent(MaxLevel(1, channel, count));
            channel += count;
        }
        foreach (var b in Buses) b.Level = ToPercent(MaxLevel(3, b.Index * 8, 8));
    }

    public void ToggleMute(VmChannel c)
    {
        if (!Connected || _setFloat == null) return;
        var name = c.IsBus ? $"Bus[{c.Index}].Mute" : $"Strip[{c.Index}].Mute";
        _setFloat(name, c.Mute ? 0 : 1);
        c.Mute = !c.Mute;
    }

    private float MaxLevel(int type, int start, int count)
    {
        float max = 0;
        for (var i = 0; i < count; i++)
            if (_getLevel!(type, start + i, out var v) == 0 && v > max) max = v;
        return max;
    }

    private static double ToPercent(float linear)
    {
        if (linear <= 0.000001f) return 0;
        var db = 20 * Math.Log10(linear);
        return Math.Clamp((db + 60) / 60 * 100, 0, 100);
    }

    private float GetFloat(string name) => _getFloat != null && _getFloat(name, out var v) == 0 ? v : 0;

    private string GetString(string name)
    {
        if (_getString == null) return "";
        var buffer = new ushort[512];
        if (_getString(name, buffer) != 0) return "";
        var length = Array.IndexOf(buffer, (ushort)0);
        return new string(buffer.Take(length < 0 ? buffer.Length : length).Select(c => (char)c).ToArray()).Trim();
    }

    public void Dispose()
    {
        try { if (_logged) _logout?.Invoke(); } catch { }
        if (_lib != IntPtr.Zero) System.Runtime.InteropServices.NativeLibrary.Free(_lib);
        _lib = IntPtr.Zero;
        Connected = false;
    }
}
