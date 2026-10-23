using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record AdapterInfo(
    string Name, string Description, string Type, string Status, long SpeedBps, string Mac,
    List<string> IPv4, List<string> IPv6, List<string> Gateways, List<string> Dns, int Index, bool IsPrimary)
{
    public string SpeedText => SpeedBps <= 0 ? "—" : SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1e9:0.#} Gbps" : $"{SpeedBps / 1e6:0} Mbps";
}

public sealed record PublicInfo(string Ip, string Isp, string City, string Region, string Country, string Timezone);

public sealed record PingResult(string Target, string Address, double? AvgMs, double? JitterMs, int LossPercent)
{
    public string AvgText => AvgMs is { } a ? $"{a:0} ms" : "sem resposta";
    public string JitterText => JitterMs is { } j ? $"{j:0.0} ms" : "—";
    public string LossText => $"{LossPercent}%";
    public string Quality => AvgMs switch
    {
        null => "Falhou",
        _ when LossPercent > 2 => "Perda de pacotes",
        < 20 => "Excelente",
        < 50 => "Boa",
        < 100 => "Razoável",
        _ => "Ruim",
    };
}

public sealed record TcpConnection(int Pid, string Process, string Local, string Remote, string RemoteIp, int RemotePort, int LocalPort, string State, bool Listening)
{
    public string Host { get; set; } = "";
}

public sealed record NetAlert(string Severity, string Title, string Detail, string Process)
{
    public int Order => Severity switch { "Alta" => 0, "Média" => 1, _ => 2 };
}

public sealed record DnsTest(string Name, string Server, double? Ms)
{
    public string MsText => Ms is { } m ? $"{m:0} ms" : "sem resposta";
}

public sealed record DnsCacheEntry(string Entry, string Data, string Type, int Ttl);

/// <summary>Uso de rede de um aplicativo (somado desde que o monitor começou).</summary>
public sealed class AppTraffic : ObservableObject
{
    private long _sent, _received;
    private double _downRate, _upRate;
    private int _connections;

    public AppTraffic(int pid, string name, string? path)
    {
        Pid = pid;
        Name = name;
        Path = path;
    }

    public int Pid { get; }
    public string Name { get; }
    public string? Path { get; }

    public long Sent { get => _sent; set { if (Set(ref _sent, value)) { Raise(nameof(SentText)); Raise(nameof(Total)); } } }
    public long Received { get => _received; set { if (Set(ref _received, value)) { Raise(nameof(ReceivedText)); Raise(nameof(Total)); } } }
    public long Total => _sent + _received;
    public double DownRate { get => _downRate; set { if (Set(ref _downRate, value)) Raise(nameof(RateText)); } }
    public double UpRate { get => _upRate; set { if (Set(ref _upRate, value)) Raise(nameof(RateText)); } }
    public int Connections { get => _connections; set => Set(ref _connections, value); }

    public string SentText => Format.Bytes((ulong)_sent);
    public string ReceivedText => Format.Bytes((ulong)_received);
    public string RateText => $"↓ {Format.Bytes((ulong)_downRate)}/s   ↑ {Format.Bytes((ulong)_upRate)}/s";
}

/// <summary>Informações da conexão, IP público, ping, conexões TCP, DNS e alertas.</summary>
public static class NetworkService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // ---------- Adaptadores e tráfego total ----------

    private static bool Counts(NetworkInterface n) =>
        n.OperationalStatus == OperationalStatus.Up
        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

    /// <summary>Bytes recebidos/enviados por todas as placas de rede desde que o PC ligou.</summary>
    public static (long Rx, long Tx) TotalBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(Counts))
            {
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(Counts))
        {
            var p = n.GetIPProperties();
            var gateways = p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any)).Select(a => a.ToString()).ToList();
            var type = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Cabo (Ethernet)",
                NetworkInterfaceType.Ppp => "Discada/PPP",
                _ => n.NetworkInterfaceType.ToString(),
            };
            var index = 0;
            try { index = p.GetIPv4Properties()?.Index ?? 0; } catch { }
            list.Add(new AdapterInfo(n.Name, n.Description, type, "Conectado", n.Speed,
                string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}").ToList(),
                p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList(),
                gateways,
                p.DnsAddresses.Select(d => d.ToString()).ToList(),
                index,
                gateways.Count > 0));
        }
        return list.OrderByDescending(a => a.IsPrimary).ToList();
    }

    /// <summary>Detalhes do Wi-Fi (rede, sinal, canal...) como o Windows mostra.</summary>
    public static async Task<List<InfoItem>> WifiDetailsAsync()
    {
        var (code, output, _) = await ProcessRunner.RunAsync("netsh.exe", "wlan show interfaces", OemEncoding());
        if (code != 0) return new();
        return output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(" : "))
            .Select(l => l.Split(" : ", 2))
            .Select(p => new InfoItem(p[0].Trim(), p[1].Trim()))
            .Where(i => !i.Key.Contains("GUID", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static System.Text.Encoding OemEncoding()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    /// <summary>IP público e operadora (consulta o serviço ipinfo.io).</summary>
    public static async Task<PublicInfo?> PublicInfoAsync()
    {
        if (!AppSettings.Current.AllowOnline) return null; // consultas na internet desligadas nas Configurações
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipinfo.io/json"));
            string G(string k) => doc.RootElement.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var org = G("org");
            // "AS28573 Claro S.A." -> "Claro S.A."
            var isp = org.StartsWith("AS") && org.Contains(' ') ? org[(org.IndexOf(' ') + 1)..] : org;
            return new PublicInfo(G("ip"), isp, G("city"), G("region"), G("country"), G("timezone"));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<PingResult> PingAsync(string label, string target, int count = 8)
    {
        using var ping = new Ping();
        var times = new List<double>();
        var address = target;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(target, 1000);
                if (reply.Status == IPStatus.Success)
                {
                    times.Add(reply.RoundtripTime);
                    address = reply.Address.ToString();
                }
            }
            catch { }
        }
        double? jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null;
        var loss = (int)Math.Round(100.0 * (count - times.Count) / count);
        return new PingResult(label, address, times.Count > 0 ? times.Average() : null, jitter, loss);
    }

    // ---------- Conexões TCP ----------

    public static List<TcpConnection> Connections()
    {
        var list = new List<TcpConnection>();
        var names = new Dictionary<int, string>();
        string Name(int pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById(pid); n = pid == 4 ? "System" : p.ProcessName; }
            catch { n = pid == 0 ? "Inativo" : $"PID {pid}"; }
            return names[pid] = n;
        }

        foreach (var (pid, local, localPort, remote, remotePort, state) in TcpTable(AF_INET).Concat(TcpTable(AF_INET6)))
        {
            var listening = state == 2;
            list.Add(new TcpConnection(pid, Name(pid), $"{Fmt(local)}:{localPort}",
                listening ? "—" : $"{Fmt(remote)}:{remotePort}", remote.ToString(), remotePort, localPort,
                StateName(state), listening));
        }
        return list;
    }

    private static string Fmt(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString();

    private static string StateName(int s) => s switch
    {
        1 => "Fechada", 2 => "Aguardando (porta aberta)", 3 => "Conectando", 4 => "Conectando",
        5 => "Conectada", 6 => "Fechando", 7 => "Fechando", 8 => "Fechando", 9 => "Fechando",
        10 => "Fechando", 11 => "Encerrando", 12 => "Apagada", _ => s.ToString(),
    };

    private const int AF_INET = 2, AF_INET6 = 23;

    private static IEnumerable<(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State)> TcpTable(int family)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, 5, 0);
        var buffer = Marshal.AllocHGlobal(size);
        var rows = new List<(int, IPAddress, int, IPAddress, int, int)>();
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, family, 5, 0) != 0) return rows;
            var count = Marshal.ReadInt32(buffer);
            var ptr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                if (family == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                    var state = Marshal.ReadInt32(ptr);
                    var local = new IPAddress((uint)Marshal.ReadInt32(ptr + 4));
                    var lport = Port(Marshal.ReadInt32(ptr + 8));
                    var remote = new IPAddress((uint)Marshal.ReadInt32(ptr + 12));
                    var rport = Port(Marshal.ReadInt32(ptr + 16));
                    var pid = Marshal.ReadInt32(ptr + 20);
                    rows.Add((pid, local, lport, remote, rport, state));
                    ptr += 24;
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
                    var lbytes = new byte[16];
                    var rbytes = new byte[16];
                    Marshal.Copy(ptr, lbytes, 0, 16);
                    var lport = Port(Marshal.ReadInt32(ptr + 20));
                    Marshal.Copy(ptr + 24, rbytes, 0, 16);
                    var rport = Port(Marshal.ReadInt32(ptr + 44));
                    var state = Marshal.ReadInt32(ptr + 48);
                    var pid = Marshal.ReadInt32(ptr + 52);
                    rows.Add((pid, new IPAddress(lbytes), lport, new IPAddress(rbytes), rport, state));
                    ptr += 56;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return rows;
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    private static readonly Dictionary<string, string> HostCache = new();

    /// <summary>Nome do servidor de um IP (DNS reverso), com cache.</summary>
    public static async Task<string> HostNameAsync(string ip)
    {
        lock (HostCache) if (HostCache.TryGetValue(ip, out var h)) return h;
        string name;
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            // Se demorar, desistimos da resposta; marca a falha dela como "vista" para não virar erro no log depois.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            name = await Task.WhenAny(task, Task.Delay(1500)) == task ? task.Result.HostName : "";
        }
        catch { name = ""; }
        lock (HostCache) HostCache[ip] = name;
        return name;
    }

    // ---------- Segurança ----------

    private static readonly (string Process, string Name)[] RemoteAccessApps =
    {
        ("AnyDesk", "AnyDesk"), ("TeamViewer", "TeamViewer"), ("TeamViewer_Service", "TeamViewer"),
        ("rustdesk", "RustDesk"), ("ScreenConnect.ClientService", "ScreenConnect"), ("LogMeIn", "LogMeIn"),
        ("winvnc", "VNC"), ("tvnserver", "TightVNC"), ("vncserver", "VNC"), ("remoting_host", "Área de Trabalho Remota do Chrome"),
        ("parsecd", "Parsec"), ("SRService", "Splashtop"), ("AA_v3", "Ammyy Admin"), ("rserver3", "Radmin"),
        ("ultraviewer_service", "UltraViewer"), ("Supremo", "Supremo"),
    };

    // Portas usadas por ferramentas de invasão conhecidas (RATs, backdoors).
    private static readonly HashSet<int> SuspiciousPorts = new() { 1337, 31337, 4444, 5552, 1604, 6666, 6667, 6668, 6669, 12345, 27374, 54321, 9999, 3460 };

    private static readonly HashSet<string> SystemListeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "svchost", "lsass", "wininit", "services", "spoolsv", "Inativo",
    };

    /// <summary>
    /// Pistas de coisas estranhas na rede. Não é antivírus: são sinais para você conferir.
    /// </summary>
    public static List<NetAlert> Analyze(List<TcpConnection> connections, IReadOnlyCollection<AppTraffic> traffic)
    {
        var alerts = new List<NetAlert>();
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (proc, name) in RemoteAccessApps.Where(r => running.Contains(r.Process)).DistinctBy(r => r.Name))
        {
            var active = connections.Any(c => c.Process.Equals(proc, StringComparison.OrdinalIgnoreCase) && c.State == "Conectada");
            alerts.Add(new NetAlert(active ? "Média" : "Baixa", $"Acesso remoto: {name}",
                active
                    ? $"{name} está aberto e conectado. Se não foi você que instalou ou não está em suporte agora, alguém pode estar controlando o PC."
                    : $"{name} está rodando. Se você não usa, desinstale.", proc));
        }

        foreach (var c in connections.Where(c => !c.Listening && SuspiciousPorts.Contains(c.RemotePort)))
            alerts.Add(new NetAlert("Alta", $"Conexão em porta suspeita ({c.RemotePort})",
                $"{c.Process} está conectado a {c.Remote}. Essa porta é muito usada por programas de invasão (RAT).", c.Process));

        foreach (var c in connections.Where(c => c.Listening && !SystemListeners.Contains(c.Process)
                                                  && (c.Local.StartsWith("0.0.0.0") || c.Local.StartsWith("[::]")))
                                     .DistinctBy(c => (c.Process, c.LocalPort)))
            alerts.Add(new NetAlert("Baixa", $"Porta aberta: {c.LocalPort}",
                $"{c.Process} aceita conexões de outros computadores na porta {c.LocalPort}. Normal para jogos, Steam e servidores; estranho se você não reconhece o programa.", c.Process));

        // Programas sem fabricante, rodando de pastas temporárias, usando a internet.
        var temp = Path.GetTempPath();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var pid in connections.Where(c => !c.Listening && c.State == "Conectada").Select(c => c.Pid).Distinct())
        {
            string? path = null;
            try { using var p = Process.GetProcessById(pid); path = p.MainModule?.FileName; } catch { }
            if (path == null) continue;
            var inTemp = path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase);
            if (inTemp && CommandLine.Publisher(path).Length == 0)
                alerts.Add(new NetAlert("Alta", "Programa desconhecido usando a internet",
                    $"{path} não tem fabricante e roda de uma pasta temporária/Downloads. Verifique com o antivírus.", Path.GetFileNameWithoutExtension(path)));
        }

        foreach (var t in traffic.Where(t => t.Sent > 200L << 20 && t.Sent > t.Received * 2))
            alerts.Add(new NetAlert("Média", $"Enviando muitos dados: {t.Name}",
                $"Já enviou {t.SentText} e recebeu só {t.ReceivedText}. Normal em upload, backup na nuvem, stream ou torrent; estranho se o programa não deveria enviar nada.", t.Name));

        return alerts.OrderBy(a => a.Order).ToList();
    }

    // ---------- DNS ----------

    public static readonly (string Name, string Primary, string Secondary)[] PublicDns =
    {
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9 (bloqueia sites maliciosos)", "9.9.9.9", "149.112.112.112"),
        ("OpenDNS", "208.67.222.222", "208.67.220.220"),
    };

    /// <summary>Mede quanto tempo um servidor DNS leva para responder (média de 3 consultas).</summary>
    public static async Task<double?> DnsQueryMsAsync(string server)
    {
        var times = new List<double>();
        foreach (var domain in new[] { "google.com", "youtube.com", "microsoft.com" })
        {
            if (await QueryOnceAsync(IPAddress.Parse(server), domain) is { } ms) times.Add(ms);
        }
        return times.Count > 0 ? times.Average() : null;
    }

    private static async Task<double?> QueryOnceAsync(IPAddress server, string domain)
    {
        // Pacote DNS mínimo: cabeçalho + 1 pergunta do tipo A.
        var packet = new List<byte> { (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        packet.AddRange(new byte[] { 0, 0, 1, 0, 1 });

        using var udp = new UdpClient(server.AddressFamily);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(packet.ToArray(), packet.Count, new IPEndPoint(server, 53));
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(2000)) != receive) return null;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> SetDnsAsync(int interfaceIndex, string? primary, string? secondary)
    {
        var cmd = primary == null
            ? $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop"
            : $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses ('{primary}','{secondary}') -ErrorAction Stop";
        var (code, _, err) = await ProcessRunner.PowerShellAsync(cmd);
        return code == 0 ? null : err.Trim().Split('\n')[0];
    }

    public static async Task FlushDnsAsync() => await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns");

    public static async Task<List<DnsCacheEntry>> DnsCacheAsync()
    {
        var list = await ProcessRunner.PowerShellJsonAsync<CacheDto>(
            """Get-DnsClientCache | Select-Object @{n='Entry';e={"$($_.Entry)"}}, @{n='Data';e={"$($_.Data)"}}, @{n='Type';e={"$($_.Type)"}}, @{n='Ttl';e={[int]$_.TimeToLive}}""");
        return list.Select(c => new DnsCacheEntry(c.Entry, c.Data, c.Type switch { "1" => "A", "28" => "AAAA", "5" => "CNAME", _ => c.Type }, c.Ttl))
            .DistinctBy(c => (c.Entry, c.Data)).OrderBy(c => c.Entry).ToList();
    }

    private sealed class CacheDto
    {
        public string Entry { get; set; } = "";
        public string Data { get; set; } = "";
        public string Type { get; set; } = "";
        public int Ttl { get; set; }
    }
}

/// <summary>
/// Mede quanto cada aplicativo envia e recebe, ouvindo o provedor de rede do kernel pelo ETW
/// (o mesmo dado que o Monitor de Recursos usa). Fica ligado depois da primeira vez que a página Rede abre.
/// </summary>
public sealed class NetworkMonitor
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    public static NetworkMonitor Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<int, (long Sent, long Received)> _totals = new();
    private readonly Dictionary<int, (long Sent, long Received)> _previous = new();
    private readonly Dictionary<int, AppTraffic> _apps = new();
    private TraceEventSession? _session;
    private long _lastSample = Stopwatch.GetTimestamp();

    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public DateTime Since { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            _session = new TraceEventSession("SoNexus-Network") { StopOnDispose = true };
            _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x30); // IPv4 + IPv6
            var parser = new RegisteredTraceEventParser(_session.Source);
            parser.All += OnEvent;
            var source = _session.Source;
            new Thread(() => { try { source.Process(); } catch { } }) { IsBackground = true }.Start();
            Running = true;
            Since = DateTime.Now;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        // 10/26 = TCP enviado, 11/27 = TCP recebido, 42/58 = UDP enviado, 43/59 = UDP recebido (IPv4/IPv6)
        var id = (int)e.ID;
        bool send = id is 10 or 26 or 42 or 58, recv = id is 11 or 27 or 43 or 59;
        if (!send && !recv) return;
        try
        {
            var pid = Convert.ToInt32(e.PayloadByName("PID") ?? e.ProcessID);
            var size = Convert.ToInt64(e.PayloadByName("size") ?? 0);
            lock (_lock)
            {
                _totals.TryGetValue(pid, out var t);
                _totals[pid] = send ? (t.Sent + size, t.Received) : (t.Sent, t.Received + size);
            }
        }
        catch { }
    }

    /// <summary>Atualiza a lista de aplicativos (chame uma vez por segundo).</summary>
    public List<AppTraffic> Sample(List<TcpConnection> connections)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.2, (now - _lastSample) / (double)Stopwatch.Frequency);
        _lastSample = now;

        Dictionary<int, (long Sent, long Received)> totals;
        lock (_lock) totals = new(_totals);

        var byPid = connections.Where(c => !c.Listening).GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (pid, t) in totals)
        {
            if (!_apps.TryGetValue(pid, out var app))
            {
                string name = $"PID {pid}";
                string? path = null;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    name = pid == 4 ? "System" : p.ProcessName;
                    try { path = p.MainModule?.FileName; } catch { }
                }
                catch { }
                _apps[pid] = app = new AppTraffic(pid, name, path);
            }
            _previous.TryGetValue(pid, out var prev);
            app.DownRate = (t.Received - prev.Received) / seconds;
            app.UpRate = (t.Sent - prev.Sent) / seconds;
            app.Sent = t.Sent;
            app.Received = t.Received;
            app.Connections = byPid.GetValueOrDefault(pid);
            _previous[pid] = t;
        }
        return _apps.Values.ToList();
    }
}
