using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoNexus.Services;

/// <summary>
/// Lê a saúde dos discos direto pelo Windows (sem bibliotecas), como o CrystalDiskInfo:
/// log de saúde do NVMe por IOCTL e atributos SMART de SSD SATA/HD pelo WMI. Precisa de administrador.
/// </summary>
internal static class SmartReader
{
    // ---------- NVMe ----------

    public sealed record NvmeHealth(
        byte CriticalWarning, int TemperatureC, byte AvailableSpare, byte SpareThreshold, byte PercentageUsed,
        ulong DataUnitsRead, ulong DataUnitsWritten, ulong PowerCycles, ulong PowerOnHours, ulong UnsafeShutdowns,
        ulong MediaErrors, ulong ErrorLogEntries, uint WarningTempMinutes);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const int NvmeLogHealth = 2;
    private const int ProtocolDataSize = 40; // STORAGE_PROTOCOL_SPECIFIC_DATA

    /// <summary>Página de log 02h (SMART / Health Information) do padrão NVMe.</summary>
    public static NvmeHealth? ReadNvme(int disk)
    {
        using var h = Open(disk);
        if (h.IsInvalid) return null;

        // STORAGE_PROPERTY_QUERY (PropertyId, QueryType) + STORAGE_PROTOCOL_SPECIFIC_DATA + 512 bytes de dados.
        var size = 8 + ProtocolDataSize + 512;
        var buffer = new byte[size];
        BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buffer, 0);
        BitConverter.GetBytes(0).CopyTo(buffer, 4); // PropertyStandardQuery
        var p = 8;
        BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, p);
        BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(buffer, p + 4);
        BitConverter.GetBytes(NvmeLogHealth).CopyTo(buffer, p + 8);   // ProtocolDataRequestValue
        BitConverter.GetBytes(0).CopyTo(buffer, p + 12);              // ProtocolDataRequestSubValue
        BitConverter.GetBytes(ProtocolDataSize).CopyTo(buffer, p + 16); // ProtocolDataOffset
        BitConverter.GetBytes(512).CopyTo(buffer, p + 20);            // ProtocolDataLength

        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buffer, size, buffer, size, out _, IntPtr.Zero)) return null;

        // Resposta: STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version, Size) + dados de protocolo; os 512 bytes começam em 8 + ProtocolDataOffset.
        var offset = 8 + BitConverter.ToInt32(buffer, 8 + 16);
        if (offset + 512 > buffer.Length) return null;
        var d = buffer[offset..(offset + 512)];
        ulong U(int at) => BitConverter.ToUInt64(d, at);
        var kelvin = BitConverter.ToUInt16(d, 1);
        return new NvmeHealth(d[0], kelvin > 0 ? kelvin - 273 : 0, d[3], d[4], d[5],
            U(32), U(48), U(112), U(128), U(144), U(160), U(176), BitConverter.ToUInt32(d, 192));
    }

    // ---------- SATA / HD ----------

    public sealed record AtaAttribute(byte Id, byte Current, byte Worst, long Raw, byte Threshold);

    /// <summary>Atributos SMART pelo WMI do Windows (MSStorageDriver_ATAPISmartData).</summary>
    public static List<AtaAttribute>? ReadAta(int disk)
    {
        var pnp = PnpIdOf(disk);
        if (pnp == null) return null;
        byte[]? data = null, thresholds = null;
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                        if (Matches(o["InstanceName"]?.ToString(), pnp)) data = o["VendorSpecific"] as byte[];
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                        if (Matches(o["InstanceName"]?.ToString(), pnp)) thresholds = o["VendorSpecific"] as byte[];
        }
        catch
        {
            return null;
        }
        if (data == null || data.Length < 362) return null;

        // 30 entradas de 12 bytes a partir do byte 2: id, flags(2), atual, pior, bruto(6), reservado.
        var list = new List<AtaAttribute>();
        for (var i = 0; i < 30; i++)
        {
            var at = 2 + i * 12;
            var id = data[at];
            if (id == 0) continue;
            long raw = 0;
            for (var b = 0; b < 6; b++) raw |= (long)data[at + 5 + b] << (8 * b);
            byte threshold = 0;
            if (thresholds != null)
                for (var t = 0; t < 30 && 2 + t * 12 + 1 < thresholds.Length; t++)
                    if (thresholds[2 + t * 12] == id) threshold = thresholds[2 + t * 12 + 1];
            list.Add(new AtaAttribute(id, data[at + 3], data[at + 4], raw, threshold));
        }
        return list;
    }

    /// <summary>O Windows prevê falha do disco (bit "PredictFailure" do SMART).</summary>
    public static bool? PredictsFailure(int disk)
    {
        var pnp = PnpIdOf(disk);
        if (pnp == null) return null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (Matches(o["InstanceName"]?.ToString(), pnp)) return o["PredictFailure"] is true;
        }
        catch { }
        return null;
    }

    // ---------- Contadores genéricos do Windows (qualquer disco) ----------

    public sealed record Reliability(int? Temperature, int? Wear, ulong? PowerOnHours, ulong? ReadErrorsUncorrected);

    public static Reliability? Counters(int disk)
    {
        try
        {
            var (_, output, _) = ProcessRunner.PowerShellAsync(
                $"$c = Get-PhysicalDisk | Where-Object DeviceId -eq '{disk}' | Get-StorageReliabilityCounter; " +
                "\"$($c.Temperature)|$($c.Wear)|$($c.PowerOnHours)|$($c.ReadErrorsUncorrected)\"").GetAwaiter().GetResult();
            var parts = output.Trim().Split('|');
            if (parts.Length < 4) return null;
            int? I(string s) => int.TryParse(s, out var v) && v > 0 ? v : null;
            ulong? L(string s) => ulong.TryParse(s, out var v) ? v : null;
            return new Reliability(I(parts[0]), int.TryParse(parts[1], out var w) ? w : null, L(parts[2]), L(parts[3]));
        }
        catch
        {
            return null;
        }
    }

    // ---------- Auxiliares ----------

    private static string? PnpIdOf(int disk)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={disk}");
            foreach (ManagementObject o in s.Get())
                using (o) return o["PNPDeviceID"]?.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>InstanceName do WMI = PNPDeviceID + "_0".</summary>
    private static bool Matches(string? instance, string pnp) =>
        instance != null && instance.StartsWith(pnp, StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle Open(int disk) =>
        CreateFileW($@"\\.\PhysicalDrive{disk}", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
