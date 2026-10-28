using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SoNexus.Services;

public sealed record BleLedFound(ulong Address, string Name, short Rssi)
{
    public string AddressText => Address.ToString("X12");
}

/// <summary>
/// Fitas de LED Bluetooth Low Energy do tipo "BJ_LED" (app MohuanLED). Protocolo conferido numa fita real:
/// serviço 0xEEA0, característica 0xEE01 (gravação sem resposta), comandos começando com 69 96:
///   ligar/desligar: 69 96 02 01 01|00     cor: 69 96 05 02 RR GG BB
/// Cada fita aceita uma conexão por vez: com o app do celular conectado, o PC não consegue controlar.
/// </summary>
public sealed class BleLedService : IDisposable
{
    public static BleLedService Instance { get; } = new();

    private static readonly Guid ServiceId = BluetoothUuidHelper.FromShortId(0xEEA0);
    private static readonly Guid CommandId = BluetoothUuidHelper.FromShortId(0xEE01);

    private sealed class Link
    {
        public BluetoothLEDevice? Device;
        public GattCharacteristic? Command;
        public uint LastColor = uint.MaxValue;
        public bool? LastPower;
        public long LastSent;
        public long RetryAfter;
        public string Status = "Desconectada";
    }

    private readonly Dictionary<ulong, Link> _links = new();
    private readonly object _lock = new();

    /// <summary>Procura fitas por alguns segundos. Erro em português quando não há Bluetooth.</summary>
    public static async Task<(List<BleLedFound> Found, string? Error)> ScanAsync(int seconds = 10)
    {
        var found = new Dictionary<ulong, BleLedFound>();
        BluetoothLEAdvertisementWatcher watcher;
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null || !adapter.IsLowEnergySupported) return (new(), "Este PC não tem Bluetooth compatível (precisa de Bluetooth 4.0 ou mais novo).");
            var radio = await adapter.GetRadioAsync();
            if (radio?.State != Windows.Devices.Radios.RadioState.On) return (new(), "O Bluetooth está desligado. Ligue em Configurações do Windows › Bluetooth.");
            watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        }
        catch (Exception ex)
        {
            return (new(), $"Bluetooth indisponível: {ex.Message}");
        }
        watcher.Received += (_, a) =>
        {
            var name = a.Advertisement.LocalName ?? "";
            if (!IsLedName(name)) return;
            lock (found) found[a.BluetoothAddress] = new BleLedFound(a.BluetoothAddress, name, a.RawSignalStrengthInDBm);
        };
        watcher.Start();
        await Task.Delay(seconds * 1000);
        watcher.Stop();
        lock (found) return (found.Values.OrderByDescending(f => f.Rssi).ToList(), null);
    }

    /// <summary>Nomes que esse tipo de controladora anuncia (BJ_LED, BJ_LED_M...).</summary>
    public static bool IsLedName(string name) => name.StartsWith("BJ_LED", StringComparison.OrdinalIgnoreCase);

    public string StatusOf(ulong address)
    {
        lock (_lock) return _links.TryGetValue(address, out var l) ? l.Status : "Desconectada";
    }

    /// <summary>Liga/desliga. Retorna false se a fita não respondeu.</summary>
    public bool SetPower(ulong address, bool on)
    {
        var link = Connected(address);
        if (link == null) return false;
        if (!Write(link, 0x69, 0x96, 0x02, 0x01, (byte)(on ? 1 : 0))) return false;
        link.LastPower = on;
        if (!on) link.LastColor = uint.MaxValue; // ao religar, manda a cor de novo
        return true;
    }

    /// <summary>
    /// Pinta a fita (cor 0xRRGGBB). Só manda se mudou ou a cada 5 s, e no máximo ~15 vezes por segundo:
    /// fita Bluetooth não acompanha 30 quadros por segundo.
    /// </summary>
    public void SetColor(ulong address, byte r, byte g, byte b)
    {
        var link = Connected(address);
        if (link == null) return;
        var color = (uint)(r << 16 | g << 8 | b);
        var now = Environment.TickCount64;
        if (color == link.LastColor && now - link.LastSent < 5000) return;
        if (now - link.LastSent < 66) return;
        if (link.LastPower != true && !SetPower(address, true)) return;
        if (Write(link, 0x69, 0x96, 0x05, 0x02, r, g, b))
        {
            link.LastColor = color;
            link.LastSent = now;
        }
    }

    /// <summary>Conexão em cache; reconecta sozinho (com espera entre tentativas, para não travar os efeitos).</summary>
    private Link? Connected(ulong address)
    {
        Link link;
        lock (_lock)
        {
            if (!_links.TryGetValue(address, out link!)) _links[address] = link = new Link();
        }
        if (link.Command != null) return link;
        if (Environment.TickCount64 < link.RetryAfter) return null;
        try
        {
            link.Device?.Dispose();
            link.Device = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().Wait3s();
            if (link.Device == null) throw new InvalidOperationException("fita não encontrada (desligada ou longe)");
            var services = link.Device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask().Wait3s();
            var service = services?.Services.FirstOrDefault() ?? throw new InvalidOperationException("fita ocupada (feche o app MohuanLED no celular) ou modelo diferente");
            var chars = service.GetCharacteristicsForUuidAsync(CommandId, BluetoothCacheMode.Uncached).AsTask().Wait3s();
            link.Command = chars?.Characteristics.FirstOrDefault() ?? throw new InvalidOperationException("modelo sem o canal de comando esperado");
            link.Status = "Conectada";
            link.LastPower = null;
            link.LastColor = uint.MaxValue;
            return link;
        }
        catch (Exception ex)
        {
            link.Status = $"Sem conexão: {ex.Message}";
            link.Command = null;
            link.RetryAfter = Environment.TickCount64 + 10_000;
            return null;
        }
    }

    private bool Write(Link link, params byte[] data)
    {
        try
        {
            var w = new DataWriter();
            w.WriteBytes(data);
            var result = link.Command!.WriteValueWithResultAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask().Wait3s();
            if (result?.Status == GattCommunicationStatus.Success) return true;
            throw new InvalidOperationException($"a fita respondeu {result?.Status}");
        }
        catch (Exception ex)
        {
            // Caiu a conexão: na próxima tentativa reconecta.
            link.Status = $"Sem conexão: {ex.Message}";
            link.Command = null;
            link.RetryAfter = Environment.TickCount64 + 5_000;
            return false;
        }
    }

    public void Forget(ulong address)
    {
        lock (_lock)
        {
            if (_links.Remove(address, out var l)) l.Device?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var l in _links.Values) l.Device?.Dispose();
            _links.Clear();
        }
    }
}

internal static class TaskTimeout
{
    /// <summary>Espera no máximo 3 s (o Bluetooth às vezes nunca responde; o motor de efeitos não pode travar).</summary>
    public static T? Wait3s<T>(this Task<T> task) where T : class =>
        task.Wait(3000) ? task.Result : throw new TimeoutException("a fita não respondeu a tempo");
}
