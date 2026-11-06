using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

public sealed class RgbSettings
{
    public string Effect { get; set; } = "Static";
    /// <summary>Cor principal e segunda cor (0xRRGGBB).</summary>
    public int Color { get; set; } = 0xFFFFFF;
    public int Color2 { get; set; } = 0x0040FF;
    /// <summary>0,2 (lento) a 3 (rápido).</summary>
    public double Speed { get; set; } = 1;
    /// <summary>0 a 100%.</summary>
    public double Brightness { get; set; } = 100;
    /// <summary>Nomes dos dispositivos que o usuário tirou dos efeitos.</summary>
    public List<string> Excluded { get; set; } = new();
    public List<int> Favorites { get; set; } = new();
    public bool RestoreOnStart { get; set; } = true;
    /// <summary>Fitas de LED Bluetooth lembradas (ex.: BJ_LED).</summary>
    public List<BleLedInfo> BleDevices { get; set; } = new();
}

public sealed class BleLedInfo
{
    public ulong Address { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Recebe os efeitos do SoNexus.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Preferências do usuário guardadas em %LocalAppData%\SoNexus\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Preço do kWh na conta de luz (R$).</summary>
    public double TariffPerKwh { get; set; } = 0.85;

    /// <summary>Horas por dia que o PC fica ligado (para a estimativa do mês).</summary>
    public double HoursPerDay { get; set; } = 6;

    /// <summary>Plano de energia ativo antes do "Modo VR" (para poder voltar).</summary>
    public Guid? PlanBeforeVr { get; set; }

    /// <summary>Última iluminação escolhida (reaplicada ao abrir o SoNexus, se ligado).</summary>
    public RgbSettings Rgb { get; set; } = new();

    /// <summary>Ao clicar no X, continua rodando na bandeja (ventoinhas, RGB e sensores seguem funcionando).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Já viu a tela de boas-vindas.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Versão dos Termos de Uso e Privacidade aceita (0 = nunca aceitou) e quando.</summary>
    public int TermsVersion { get; set; }
    public string? TermsAcceptedAt { get; set; }

    /// <summary>Já mostrou o aviso "o SoNexus continua na bandeja".</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>O botão minimizar (—) também esconde na bandeja, em vez de deixar na barra de tarefas.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Notificação do Windows quando a CPU/GPU passa do limite (funciona com o app na bandeja).</summary>
    public bool TempAlerts { get; set; } = true;
    public int CpuAlertC { get; set; } = 90;
    public int GpuAlertC { get; set; } = 85;

    /// <summary>De quanto em quanto tempo os sensores são lidos.</summary>
    public int SensorIntervalMs { get; set; } = 1000;

    /// <summary>Notebook na bateria: sensores a cada 5 s e efeitos RGB mais lentos, para gastar menos.</summary>
    public bool BatterySaver { get; set; } = true;

    /// <summary>Aviso quando a RAM do PC passa deste uso (%).</summary>
    public bool RamAlert { get; set; } = true;
    public int RamAlertPct { get; set; } = 90;

    /// <summary>Tamanho da interface (1 = 100%).</summary>
    public double UiScale { get; set; } = 1;

    /// <summary>Ao abrir: a última página usada (true) ou sempre o Início.</summary>
    public bool OpenLastPage { get; set; } = true;

    /// <summary>Atalhos de teclado que funcionam de qualquer lugar (Ctrl+Shift+S e Ctrl+Shift+F).</summary>
    public bool GlobalHotkeys { get; set; } = true;

    /// <summary>
    /// Consultas na internet: requisitos dos jogos (loja Steam), IP público/operadora e lista de servidores da Valve.
    /// Desligado, o SoNexus só usa o que já está no PC.
    /// </summary>
    public bool AllowOnline { get; set; } = true;

    /// <summary>Tamanho, posição e página da janela da última vez.</summary>
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }
    public string LastPage { get; set; } = "NavHome";

    /// <summary>O usuário leu e aceitou as regras de segurança do overclock.</summary>
    public bool OverclockRulesAccepted { get; set; }

    /// <summary>O que aparece sobre o jogo (chaves de <c>FpsView.OverlayItems</c>).</summary>
    public List<string> OverlayItems { get; set; } = new() { "fps", "low", "cpu", "cputemp", "gpu", "gputemp", "ram", "gameram" };

    /// <summary>0 = canto superior esquerdo, 1 = superior direito, 2 = inferior esquerdo, 3 = inferior direito.</summary>
    public int OverlayCorner { get; set; }

    /// <summary>0 = pequeno, 1 = médio, 2 = grande.</summary>
    public int OverlaySize { get; set; } = 1;

    /// <summary>Cor dos números (0 = branco, 1 = verde, 2 = amarelo, 3 = ciano).</summary>
    public int OverlayColor { get; set; }

    /// <summary>0 = uma linha por peça, 1 = tudo numa linha só.</summary>
    public int OverlayFlow { get; set; }

    /// <summary>Fundo do painel, 0 a 100% (0 = só os números).</summary>
    public int OverlayOpacity { get; set; } = 70;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "settings.json");

    public static string Path_ => FilePath;

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private static bool _saveDisabled;

    /// <summary>Depois de importar/restaurar: o app reabre sem gravar as configurações antigas por cima.</summary>
    public static void DisableSave() => _saveDisabled = true;

    public void Save()
    {
        if (_saveDisabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
