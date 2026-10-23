using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Um "salto" do caminho até o servidor (roteador, provedor, backbone...), com estatísticas como no WinMTR.</summary>
public sealed class RouteHop : ObservableObject
{
    private readonly List<double> _times = new();
    private int _sent;
    private string _host = "";

    public required int Hop { get; init; }
    public required string Ip { get; init; }
    public string Host { get => _host; set { Set(ref _host, value); Raise(nameof(Name)); } }
    public string Role { get; set; } = "";
    /// <summary>O destino não responde ping (comum em servidores de jogos): medimos pela conexão TCP.</summary>
    public bool TcpFallback { get; set; }

    public string Name => Ip == "*" ? "(não responde)" : Host.Length > 0 && Host != Ip ? $"{Host}  ({Ip})" : Ip;
    public double? Avg => _times.Count > 0 ? _times.Average() : null;
    public double? Best => _times.Count > 0 ? _times.Min() : null;
    public double? Worst => _times.Count > 0 ? _times.Max() : null;
    public int Loss => _sent == 0 ? 0 : (int)Math.Round(100.0 * (_sent - _times.Count) / _sent);
    public string AvgText => Avg is { } a ? $"{a:0} ms" : "—";
    public string RangeText => Best is { } b ? $"{b:0}–{Worst:0}" : "";
    public string LossText => _sent == 0 ? "" : $"{Loss}%";
    public string SentText => _sent > 0 ? $"{_sent}" : "";

    public void Add(double? ms)
    {
        lock (_times)
        {
            _sent++;
            if (ms is { } m) _times.Add(m);
        }
        foreach (var p in new[] { nameof(AvgText), nameof(RangeText), nameof(LossText), nameof(SentText) }) Raise(p);
    }
}

public sealed record GameEndpoint(string Game, string Ip, int Port)
{
    public string Display => $"{Game} → {Ip}:{Port}";
}

public static class RouteService
{
    /// <summary>
    /// Destinos prontos (todos conferidos: resolvem e respondem). Porta = usada quando o servidor bloqueia ping.
    /// Jogos que escolhem o servidor na hora (Valorant, Fortnite...) usam a região da nuvem onde ficam.
    /// </summary>
    public static readonly (string Name, string Host, int Port)[] Presets =
    {
        ("Roblox", "apis.roblox.com", 443),
        ("Minecraft Java · Hypixel (EUA)", "mc.hypixel.net", 25565),
        ("Minecraft · login Microsoft/Mojang", "sessionserver.mojang.com", 443),
        ("Fortnite · login Epic Games", "account-public-service-prod.ol.epicgames.com", 443),
        ("Fortnite, Valorant, LoL e outros · região São Paulo (AWS)", "dynamodb.sa-east-1.amazonaws.com", 443),
        ("Servidores dos EUA · região Virgínia (AWS)", "dynamodb.us-east-1.amazonaws.com", 443),
        ("Servidores da Europa · região Frankfurt (AWS)", "dynamodb.eu-central-1.amazonaws.com", 443),
        ("GTA V Online · Rockstar", "prod.ros.rockstargames.com", 443),
        ("EA FC, Apex, Battlefield · EA", "accounts.ea.com", 443),
        ("Discord (voz e chat)", "discord.com", 443),
        ("Google DNS (referência)", "8.8.8.8", 443),
        ("Cloudflare (referência)", "1.1.1.1", 443),
    };

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Usados se a API da Steam não responder.
    private static readonly (string Code, string Name, string Ip)[] ValveFallback =
    {
        ("gru", "São Paulo (Brasil)", "155.133.227.35"), ("scl", "Santiago (Chile)", "155.133.249.163"),
        ("eze", "Buenos Aires (Argentina)", "155.133.255.98"), ("iad", "Virgínia (EUA)", "162.254.192.88"),
    };

    /// <summary>
    /// Relés da rede da Valve por onde passam as partidas de CS2 e Dota 2 (lista oficial e atual, da API da Steam).
    /// Só as regiões das Américas e Europa mais usadas por brasileiros.
    /// </summary>
    public static async Task<List<(string Name, string Host, int Port)>> ValveRelaysAsync()
    {
        var wanted = new[] { "gru", "scl", "eze", "lim", "iad", "atl", "mia", "lax", "fra", "mad" };
        var list = new List<(string, string, int)>();
        if (AppSettings.Current.AllowOnline)
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1?appid=730"));
            var pops = doc.RootElement.GetProperty("pops");
            foreach (var code in wanted)
            {
                if (!pops.TryGetProperty(code, out var pop) || !pop.TryGetProperty("relays", out var relays) || relays.GetArrayLength() == 0) continue;
                var desc = pop.TryGetProperty("desc", out var d) ? d.GetString() : code;
                var ip = relays[0].GetProperty("ipv4").GetString();
                if (ip != null) list.Add(($"CS2 / Dota 2 · Valve {desc}", ip, 443));
            }
        }
        catch (Exception ex) { AppLog.Write($"Rotas: API da Steam indisponível: {ex.Message}"); }
        if (list.Count == 0) list.AddRange(ValveFallback.Select(v => ($"CS2 / Dota 2 · Valve {v.Name}", v.Ip, 443)));
        return list;
    }

    /// <summary>Servidores com que os jogos abertos estão conectados agora (pela tabela TCP do Windows).</summary>
    public static List<GameEndpoint> OpenGameServers()
    {
        var games = GamesService.ScanLibrary().Where(g => !string.IsNullOrWhiteSpace(g.InstallPath)).ToList();
        var result = new List<GameEndpoint>();
        var paths = new Dictionary<int, string?>();
        foreach (var c in NetworkService.Connections().Where(c => !c.Listening && c.RemotePort > 0 && IsPublic(c.RemoteIp)))
        {
            if (!paths.TryGetValue(c.Pid, out var path))
            {
                try { using var p = Process.GetProcessById(c.Pid); path = p.MainModule?.FileName; }
                catch { path = null; }
                paths[c.Pid] = path;
            }
            if (path == null) continue;
            var game = games.FirstOrDefault(g => path.StartsWith(g.InstallPath!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (game != null) result.Add(new GameEndpoint(game.Name, c.RemoteIp, c.RemotePort));
        }
        return result.DistinctBy(e => e.Ip).ToList();
    }

    private static bool IsPublic(string ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
        !(ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("169.254.") ||
          (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31));

    /// <summary>
    /// Descobre o caminho (TTL 1, 2, 3...) e depois mede cada salto várias vezes.
    /// <paramref name="onHops"/> recebe a lista assim que os saltos são descobertos; os números vão atualizando.
    /// </summary>
    public static async Task TraceAsync(string target, int port, Action<List<RouteHop>> onHops, int rounds, CancellationToken ct)
    {
        var address = (await Dns.GetHostAddressesAsync(target, ct)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? throw new InvalidOperationException("Endereço não encontrado.");
        var hops = new List<RouteHop>();
        var buffer = new byte[32];
        using (var ping = new Ping())
        {
            for (var ttl = 1; ttl <= 30 && !ct.IsCancellationRequested; ttl++)
            {
                PingReply? reply = null;
                for (var attempt = 0; attempt < 2 && reply?.Address == null; attempt++)
                {
                    try { reply = await ping.SendPingAsync(address, 1500, buffer, new PingOptions(ttl, true)); }
                    catch { }
                }
                var ip = reply?.Address?.ToString();
                var reached = reply?.Status == IPStatus.Success;
                hops.Add(new RouteHop { Hop = ttl, Ip = ip ?? "*" });
                if (reached || ip == address.ToString()) break;
                // 4 saltos seguidos sem resposta no fim: o destino bloqueia ping; o último "salto" passa a ser ele.
                if (hops.Count >= 4 && hops.TakeLast(4).All(h => h.Ip == "*"))
                {
                    hops.RemoveRange(hops.Count - 4, 4);
                    break;
                }
                // O mesmo roteador respondendo 3 vezes seguidas = a rota não anda mais (firewall do destino).
                if (ip != null && hops.Count >= 3 && hops.TakeLast(3).All(h => h.Ip == ip))
                {
                    hops.RemoveRange(hops.Count - 2, 2);
                    break;
                }
            }
        }
        if (hops.LastOrDefault()?.Ip != address.ToString())
            hops.Add(new RouteHop { Hop = hops.Count + 1, Ip = address.ToString(), TcpFallback = true });
        Label(hops);
        onHops(hops);

        // Nomes (DNS reverso) em paralelo: ajudam a ver qual é o provedor e em que cidade passa.
        _ = Task.WhenAll(hops.Where(h => h.Ip != "*").Select(async h =>
        {
            try { h.Host = (await Dns.GetHostEntryAsync(h.Ip, ct)).HostName; } catch { }
        }));

        for (var r = 0; r < rounds && !ct.IsCancellationRequested; r++)
        {
            await Task.WhenAll(hops.Where(h => h.Ip != "*").Select(async h =>
            {
                h.Add(h.TcpFallback ? await TcpPingAsync(h.Ip, port, ct) : await PingOnceAsync(h.Ip));
            }));
            await Task.Delay(500, ct).ContinueWith(_ => { });
        }
    }

    private static void Label(List<RouteHop> hops)
    {
        for (var i = 0; i < hops.Count; i++)
        {
            hops[i].Role = i == hops.Count - 1 ? "Destino" : i == 0 ? "Seu roteador" : i <= 3 ? "Seu provedor" : "Internet";
            if (hops[i].Ip.StartsWith("192.168.") || hops[i].Ip.StartsWith("10.")) hops[i].Role = i == 0 ? "Seu roteador" : "Rede local / modem";
        }
    }

    private static async Task<double?> PingOnceAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1500);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Tempo para abrir uma conexão TCP (servidores que bloqueiam ping continuam aceitando conexão).</summary>
    public static async Task<double?> TcpPingAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(IPAddress.Parse(ip), port, timeout.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Diz em português onde está o problema da rota.</summary>
    public static (string Title, string Detail) Diagnose(List<RouteHop> hops)
    {
        var measured = hops.Where(h => h.Avg != null).ToList();
        if (measured.Count == 0) return ("Sem resposta", "Nenhum salto respondeu. Verifique se o PC está conectado à internet.");
        var dest = hops[^1];
        var router = hops[0];
        // Servidor que não aceita nenhum teste (só fala o protocolo do jogo, em UDP): não é perda de verdade.
        var silent = dest.TcpFallback && dest.Avg == null;
        if (silent)
        {
            hops = hops.Take(hops.Count - 1).ToList();
            measured = hops.Where(h => h.Avg != null).ToList();
            if (measured.Count == 0) return ("Sem resposta", "Nem o servidor nem o caminho responderam aos testes.");
            dest = measured[^1];
        }

        if (router.Avg is > 8 || router.Loss > 2)
            return ("Problema dentro de casa", $"Até o seu roteador já leva {router.AvgText} com {router.Loss}% de perda (o normal é menos de 2 ms no cabo e até 5 ms no Wi-Fi). Use cabo, aproxime do roteador ou troque o canal do Wi-Fi.");

        // Perda que começa num salto e continua até o fim = problema real naquele ponto
        // (perda só num salto do meio costuma ser o roteador ignorando ping, não é problema).
        if (dest.Loss >= 5)
        {
            var start = hops.FindIndex(h => h.Ip != "*" && h.Loss >= 5 && hops.SkipWhile(x => x != h).All(x => x.Ip == "*" || x.Loss >= 5));
            var where = start >= 0 ? $"a partir do salto {hops[start].Hop} ({hops[start].Role.ToLowerInvariant()}, {hops[start].Name})" : "no caminho";
            return ("Perda de pacotes", $"{dest.Loss}% dos pacotes se perdem {where}. Em jogo isso vira teleporte/rubber-banding. " +
                                        (start is > 0 and <= 3 ? "Fica no seu provedor: ligue para o suporte com este resultado." : "Fica fora do seu provedor; tente outro servidor/região no jogo ou uma VPN para jogos."));
        }

        // Onde o ping dá o maior salto e não volta a cair.
        double prev = router.Avg ?? 0;
        RouteHop? jump = null;
        double jumpMs = 0;
        foreach (var h in measured.Skip(1))
        {
            var d = (h.Avg ?? 0) - prev;
            if (d > jumpMs && measured.SkipWhile(x => x != h).All(x => (x.Avg ?? 0) >= (h.Avg ?? 0) * 0.8)) { jump = h; jumpMs = d; }
            prev = h.Avg ?? prev;
        }
        var total = dest.Avg ?? measured[^1].Avg ?? 0;
        var jitter = dest.Worst - dest.Best;
        var verdict = total switch { < 30 => "Ótimo para jogos", < 60 => "Bom para jogos", < 100 => "Jogável", _ => "Ping alto" };
        var detail = (silent ? $"O servidor do jogo não responde a testes, então medimos até o último ponto antes dele ({dest.Name}): {total:0} ms." : $"Ping até o destino: {total:0} ms.") +
                     (jump != null && jumpMs > 25 ? $" A maior parte do atraso (+{jumpMs:0} ms) aparece no salto {jump.Hop} ({jump.Name}), " +
                                                    (jump.Hop <= 4 ? "ainda no seu provedor." : "na distância até o servidor (normal para outro estado/país).") : "") +
                     (jitter > 30 ? $" Variação alta ({dest.RangeText} ms): pode causar engasgos." : "");
        return (verdict, detail);
    }
}
