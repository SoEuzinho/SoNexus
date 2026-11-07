using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SoNexus.Services;

/// <summary>Um app pré-instalado encontrado no PC.</summary>
public sealed record BloatApp(string Name, string Friendly, string Version, string Recommendation, bool Default, bool Winget);

public sealed record DebloatProfile(List<string> Tweaks, List<string> Apps);

/// <summary>
/// Catálogo de ajustes do Windows e de apps removíveis. Os valores de registro e a lista de apps
/// seguem o projeto Win11Debloat (Raphire, licença MIT); veja THIRD-PARTY-NOTICES.txt.
/// </summary>
public static class DebloatService
{
    private const RegistryHive CU = RegistryHive.CurrentUser;
    private const RegistryHive LM = RegistryHive.LocalMachine;
    private const RegistryHive CR = RegistryHive.ClassesRoot;
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string GamesTask = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string ClassicMenu = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ChromePolicy = @"SOFTWARE\Policies\Google\Chrome";
    private const string WindowsAI = @"Software\Policies\Microsoft\Windows\WindowsAI";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static RegValue C(string path, string name, object value, object? def) => new(CU, path, name, value, def);
    private static RegValue L(string path, string name, object value, object? def) => new(LM, path, name, value, def);

    private static TweakAction[] Policies(string path, params (string Name, int Value)[] values) =>
        values.Select(v => (TweakAction)L(path, v.Name, v.Value, null)).ToArray();

    public static readonly IReadOnlyList<Tweak> Tweaks = new List<Tweak>
    {
        // ---------- Privacidade ----------
        new()
        {
            Id = "telemetry", Category = "Privacidade", Title = "Desativar telemetria", Recommended = true,
            Description = "Reduz ao mínimo os dados de diagnóstico enviados à Microsoft: coleta de digitação/escrita, reconhecimento de voz online, rastreio de apps abertos, pesquisas de feedback e o serviço DiagTrack.",
            Actions = new TweakAction[]
            {
                L(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null),
                L(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, null),
                C(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, 1),
                C(@"Software\Microsoft\Input\TIPC", "Enabled", 0, 1),
                C(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
                C(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
                C(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1),
                C(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1),
                C(Adv, "Start_TrackProgs", 0, 1),
                C(@"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null),
                new ServiceDisable("DiagTrack", "auto"),
                new ServiceDisable("dmwappushservice", "demand"),
            },
        },
        new()
        {
            Id = "ad-id", Category = "Privacidade", Title = "Desativar ID de publicidade", Recommended = true,
            Description = "Impede que apps usem seu ID de anúncios para mostrar propaganda personalizada.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1) },
        },
        new()
        {
            Id = "tailored", Category = "Privacidade", Title = "Desativar experiências personalizadas", Recommended = true,
            Description = "A Microsoft deixa de usar seus dados de diagnóstico para dicas e anúncios personalizados.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1) },
        },
        new()
        {
            Id = "activity", Category = "Privacidade", Title = "Desativar histórico de atividades", Recommended = true,
            Description = "Para de registrar e enviar os apps e arquivos que você abre (Linha do Tempo).",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\System", ("PublishUserActivities", 0), ("UploadUserActivities", 0)),
        },
        new()
        {
            Id = "location", Category = "Privacidade", Title = "Desativar localização", Risk = Risk.Moderado,
            Description = "Nenhum app consegue saber onde você está. Clima, mapas e o fuso horário automático deixam de usar a localização.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", ("DisableLocation", 1)),
        },
        new()
        {
            Id = "find-my-device", Category = "Privacidade", Title = "Desativar \"Encontrar meu dispositivo\"", Risk = Risk.Moderado,
            Description = "O PC para de reportar a localização para a conta Microsoft. Em notebook, você perde a opção de localizar se for roubado.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\FindMyDevice", ("AllowFindMyDevice", 0)),
        },

        // ---------- Anúncios e sugestões ----------
        new()
        {
            Id = "suggestions", Category = "Anúncios e sugestões", Title = "Remover anúncios e sugestões do Windows", Recommended = true,
            Description = "Tira apps sugeridos do Iniciar, dicas, \"termine de configurar o PC\", notificações de conta e backup, anúncios no Explorer e a instalação silenciosa de apps patrocinados.",
            Actions = new TweakAction[]
            {
                C(Cdm, "SubscribedContent-310093Enabled", 0, 1),
                C(Cdm, "SubscribedContent-338388Enabled", 0, 1),
                C(Cdm, "SubscribedContent-338389Enabled", 0, 1),
                C(Cdm, "SubscribedContent-338393Enabled", 0, 1),
                C(Cdm, "SubscribedContent-353694Enabled", 0, 1),
                C(Cdm, "SubscribedContent-353696Enabled", 0, 1),
                C(Cdm, "SubscribedContent-353698Enabled", 0, 1),
                C(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                C(Cdm, "SoftLandingEnabled", 0, 1),
                C(Cdm, "SilentInstalledAppsEnabled", 0, 1),
                C(Adv, "Start_IrisRecommendations", 0, 1),
                C(Adv, "Start_AccountNotifications", 0, null),
                C(Adv, "ShowSyncProviderNotifications", 0, 1),
                C(@"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0, null),
                C(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null),
                C(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0, null),
                C(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.BackupReminder", "Enabled", 0, null),
                C(@"Software\Microsoft\Windows\CurrentVersion\Mobility", "OptedIn", 0, null),
            },
        },
        new()
        {
            Id = "lockscreen-tips", Category = "Anúncios e sugestões", Title = "Remover dicas da tela de bloqueio", Recommended = true,
            Description = "Tira as curiosidades, dicas e anúncios que aparecem sobre a imagem da tela de bloqueio.",
            Actions = new TweakAction[]
            {
                C(Cdm, "SubscribedContent-338387Enabled", 0, 1),
                C(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
            },
        },
        new()
        {
            Id = "settings-365-ads", Category = "Anúncios e sugestões", Title = "Remover anúncios do Microsoft 365 nas Configurações",
            Description = "Tira as propagandas de assinatura (Microsoft 365, Game Pass, OneDrive) da página inicial das Configurações.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", ("DisableConsumerAccountStateContent", 1)),
        },
        new()
        {
            Id = "settings-home", Category = "Anúncios e sugestões", Title = "Esconder a página \"Início\" das Configurações",
            Description = "As Configurações abrem direto em Sistema, sem a página com cartões e ofertas.",
            Actions = new TweakAction[] { L(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:home", null) },
        },

        // ---------- Inteligência artificial ----------
        new()
        {
            Id = "copilot", Category = "Inteligência artificial", Title = "Desativar Copilot", Recommended = true,
            Description = "Desliga o assistente Copilot do Windows e tira o botão dele da barra de tarefas.",
            Actions = new TweakAction[]
            {
                C(Adv, "ShowCopilotButton", 0, null),
                C(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                L(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
            },
        },
        new()
        {
            Id = "recall", Category = "Inteligência artificial", Title = "Desativar Recall", Recommended = true,
            Description = "Impede o Windows de tirar capturas de tela periódicas para análise por IA e de oferecer ativar o Recall (PCs Copilot+).",
            Actions = new TweakAction[]
            {
                C(WindowsAI, "DisableAIDataAnalysis", 1, null),
                L(WindowsAI, "DisableAIDataAnalysis", 1, null),
                L(WindowsAI, "AllowRecallEnablement", 0, null),
                L(WindowsAI, "TurnOffSavingSnapshots", 1, null),
            },
        },
        new()
        {
            Id = "click-to-do", Category = "Inteligência artificial", Title = "Desativar Click to Do", Recommended = true,
            Description = "Desliga a sobreposição de IA que analisa o que está na tela (Windows + clique).",
            Actions = new TweakAction[] { C(WindowsAI, "DisableClickToDo", 1, null), L(WindowsAI, "DisableClickToDo", 1, null) },
        },
        new()
        {
            Id = "ai-service", Category = "Inteligência artificial", Title = "Não iniciar o serviço de IA sozinho", Recommended = true,
            Description = "O serviço de IA do Windows (WSAIFabricSvc) só abre quando algum recurso de IA pedir, em vez de ficar sempre rodando.",
            Actions = new TweakAction[] { new RegValue(LM, @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc", "Start", 3, 2, needsKey: true) },
        },
        new()
        {
            Id = "notepad-ai", Category = "Inteligência artificial", Title = "Desativar IA do Bloco de Notas",
            Description = "Remove reescrever/resumir com IA do Bloco de Notas.",
            Actions = Policies(@"SOFTWARE\Policies\WindowsNotepad", ("DisableAIFeatures", 1)),
        },
        new()
        {
            Id = "paint-ai", Category = "Inteligência artificial", Title = "Desativar IA do Paint",
            Description = "Remove Cocreator, Preenchimento e Apagar generativos, Criador de Imagens e remoção de fundo por IA do Paint.",
            Actions = Policies(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint",
                ("DisableCocreator", 1), ("DisableGenerativeFill", 1), ("DisableImageCreator", 1), ("DisableGenerativeErase", 1), ("DisableRemoveBackground", 1)),
        },

        // ---------- Navegadores ----------
        new()
        {
            Id = "edge-ads", Category = "Navegadores", Title = "Edge: remover anúncios e sugestões", Recommended = true,
            Description = "Tira notícias e atalhos patrocinados da nova guia, assistente de compras, pedidos para ser o navegador padrão, botão do Acrobat e outras ofertas do Edge.",
            Actions = Policies(EdgePolicy,
                ("NewTabPageContentEnabled", 0), ("NewTabPageHideDefaultTopSites", 1), ("EdgeShoppingAssistantEnabled", 0), ("TabServicesEnabled", 0),
                ("AlternateErrorPagesEnabled", 0), ("UserFeedbackAllowed", 0), ("ShowRecommendationsEnabled", 0), ("WalletDonationEnabled", 0),
                ("DefaultBrowserSettingEnabled", 0), ("DefaultBrowserSettingsCampaignEnabled", 0), ("SpotlightExperiencesAndRecommendationsEnabled", 0),
                ("ShowAcrobatSubscriptionButton", 0), ("PersonalizationReportingEnabled", 0), ("DiagnosticData", 0)),
        },
        new()
        {
            Id = "edge-ai", Category = "Navegadores", Title = "Edge: desativar IA",
            Description = "Desliga o Copilot na barra lateral, a leitura da página pelo Copilot, a escrita com IA e a pesquisa no histórico por IA do Edge.",
            Actions = Policies(EdgePolicy,
                ("CopilotCDPPageContext", 0), ("CopilotPageContext", 0), ("HubsSidebarEnabled", 0), ("Microsoft365CopilotChatIconEnabled", 0),
                ("EdgeEntraCopilotPageContext", 0), ("EdgeHistoryAISearchEnabled", 0), ("ComposeInlineEnabled", 0),
                ("GenAILocalFoundationalModelSettings", 1), ("NewTabPageBingChatEnabled", 0)),
        },
        new()
        {
            Id = "chrome-ai", Category = "Navegadores", Title = "Chrome: desativar IA (Gemini)",
            Description = "Desliga o Gemini, \"Me ajude a escrever\", pesquisa no histórico, temas e comparação de guias por IA no Google Chrome.",
            Actions = Policies(ChromePolicy,
                ("AIModeSettings", 1), ("AutofillPredictionSettings", 2), ("ChromeSuggestionsSettings", 1), ("CreateThemesSettings", 2),
                ("DevToolsGenAiSettings", 2), ("FindAndFillWithGeminiSettings", 2), ("GeminiSettings", 1), ("GeminiSparkSettings", 1),
                ("GenAILocalFoundationalModelSettings", 1), ("HelpMeWriteSettings", 2), ("HistorySearchSettings", 2), ("SearchContentSharingSettings", 1),
                ("SmartTabSharingSettings", 1), ("TabCompareSettings", 2), ("ThirdPartyAiChatSettings", 1), ("VoiceTypingSettings", 2)),
        },
        new()
        {
            Id = "brave-bloat", Category = "Navegadores", Title = "Brave: remover extras",
            Description = "Desliga VPN, carteira de cripto, Leo (IA), Rewards, Brave Talk e Brave News do navegador Brave.",
            Actions = Policies(@"SOFTWARE\Policies\BraveSoftware\Brave",
                ("BraveVPNDisabled", 1), ("BraveWalletDisabled", 1), ("BraveAIChatEnabled", 0), ("BraveRewardsDisabled", 1), ("BraveTalkDisabled", 1), ("BraveNewsDisabled", 1)),
        },

        // ---------- Jogos e desempenho ----------
        new()
        {
            Id = "game-dvr", Category = "Jogos e desempenho", Title = "Desativar gravação em segundo plano (Game DVR)",
            Description = "A Xbox Game Bar para de gravar o jogo em segundo plano, o que libera CPU e GPU.",
            Actions = new TweakAction[]
            {
                C(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                C(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, null),
                L(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null),
            },
        },
        new()
        {
            Id = "game-bar-integration", Category = "Jogos e desempenho", Title = "Desativar a Xbox Game Bar (Win+G e botão do controle)", Risk = Risk.Moderado,
            Description = "O botão Xbox do controle e o Win+G deixam de abrir a Game Bar, e some o aviso \"você precisa de um app para abrir ms-gamebar\" depois de remover os apps Xbox.",
            Actions = new TweakAction[]
            {
                C(@"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, null),
                new RegValue(CR, "ms-gamebar", "", "URL:ms-gamebar", null),
                new RegValue(CR, "ms-gamebar", "URL Protocol", "", null),
                new RegValue(CR, "ms-gamebar", "NoOpenWith", "", null),
                new RegValue(CR, @"ms-gamebar\shell\open\command", "", @"%SystemRoot%\System32\systray.exe", null),
                new RegValue(CR, "ms-gamebarservices", "", "URL:ms-gamebarservices", null),
                new RegValue(CR, "ms-gamebarservices", "URL Protocol", "", null),
                new RegValue(CR, "ms-gamebarservices", "NoOpenWith", "", null),
                new RegValue(CR, @"ms-gamebarservices\shell\open\command", "", @"%SystemRoot%\System32\systray.exe", null),
            },
        },
        new()
        {
            Id = "game-mode", Category = "Jogos e desempenho", Title = "Ativar Modo de Jogo",
            Description = "O Windows dá prioridade ao jogo aberto e evita atualizações durante a partida.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null) },
        },
        new()
        {
            Id = "games-priority", Category = "Jogos e desempenho", Title = "Prioridade alta para jogos",
            Description = "Aumenta a prioridade de CPU dos jogos no agendador multimídia e reserva menos CPU para tarefas de fundo.",
            Actions = new TweakAction[]
            {
                L(GamesTask, "Priority", 6, 2),
                L(GamesTask, "Scheduling Category", "High", "Medium"),
                L(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, 20),
            },
        },
        new()
        {
            Id = "mouse-accel", Category = "Jogos e desempenho", Title = "Desativar aceleração do mouse",
            Description = "Movimento do mouse 1:1, sem a \"precisão aprimorada do ponteiro\". Melhor para mira em jogos. Vale na hora.",
            Actions = new TweakAction[]
            {
                C(@"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                C(@"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                new RegValue(CU, @"Control Panel\Mouse", "MouseThreshold2", "0", "10") { AfterChange = ApplyMouseNow },
            },
        },
        new()
        {
            Id = "hags", Category = "Jogos e desempenho", Title = "Agendamento de GPU acelerado por hardware",
            Description = "A placa de vídeo gerencia a própria memória. Pode reduzir latência; necessário para DLSS Frame Generation.",
            Risk = Risk.Moderado, Restart = Restart.Windows,
            Actions = new TweakAction[] { L(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1) },
        },
        new()
        {
            Id = "background-apps", Category = "Jogos e desempenho", Title = "Desativar apps em segundo plano",
            Description = "Apps da Microsoft Store param de rodar escondidos. Alguns podem deixar de mandar notificações.",
            Risk = Risk.Moderado,
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0) },
        },
        new()
        {
            Id = "power-throttling", Category = "Jogos e desempenho", Title = "Desativar Power Throttling",
            Description = "O Windows para de reduzir a CPU de programas em segundo plano. Em notebook, gasta mais bateria.",
            Risk = Risk.Moderado, Restart = Restart.Windows,
            Actions = new TweakAction[] { L(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null) },
        },
        new()
        {
            Id = "sysmain", Category = "Jogos e desempenho", Title = "Desativar SysMain (Superfetch)",
            Description = "Para de pré-carregar programas na RAM. Indicado só se o Windows estiver em SSD.",
            Risk = Risk.Moderado,
            Actions = new TweakAction[] { new ServiceDisable("SysMain", "auto") },
        },
        new()
        {
            Id = "search-indexing", Category = "Jogos e desempenho", Title = "Desativar indexação da pesquisa",
            Description = "Menos uso de disco em segundo plano; em troca, procurar arquivos fica mais lento.",
            Risk = Risk.Moderado,
            Actions = new TweakAction[] { new ServiceDisable("WSearch", "delayed-auto") },
        },
        new()
        {
            Id = "network-throttling", Category = "Jogos e desempenho", Title = "Tirar o limite de rede para multimídia",
            Description = "O Windows segura o tráfego de rede enquanto toca áudio/vídeo. Sem o limite, jogos online e downloads não perdem pacotes por isso.",
            Restart = Restart.Windows,
            Actions = new TweakAction[] { L(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10) },
        },
        new()
        {
            Id = "menu-delay", Category = "Jogos e desempenho", Title = "Menus abrindo mais rápido",
            Description = "Os menus do Windows esperam 0,4 s antes de abrir. Com o ajuste, abrem quase na hora.",
            Restart = Restart.Windows,
            Actions = new TweakAction[] { C(@"Control Panel\Desktop", "MenuShowDelay", "50", "400") },
        },
        new()
        {
            Id = "startup-delay", Category = "Jogos e desempenho", Title = "Programas da inicialização sem espera",
            Description = "O Windows segura os programas da inicialização por alguns segundos depois do login. Sem a espera, tudo fica pronto antes.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null) },
        },

        // ---------- Sistema ----------
        new()
        {
            Id = "drag-tray", Category = "Sistema", Title = "Desativar a bandeja de compartilhar ao arrastar", Recommended = true,
            Description = "Some a barra que aparece no topo da tela ao arrastar um arquivo, oferecendo compartilhar.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\CDP", "DragTrayEnabled", 0, null) },
        },
        new()
        {
            Id = "standby-network", Category = "Sistema", Title = "Desligar a rede durante a suspensão", Recommended = true,
            Description = "Em PCs com \"Modern Standby\", a internet desliga quando o PC dorme. Economiza energia e evita acordar sozinho.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", ("ACSettingIndex", 0), ("DCSettingIndex", 0)),
        },
        new()
        {
            Id = "sticky-keys", Category = "Sistema", Title = "Desativar o atalho das Teclas de Aderência", Restart = Restart.Windows,
            Description = "Apertar Shift 5 vezes (comum em jogos) deixa de abrir o aviso das Teclas de Aderência.",
            Actions = new TweakAction[] { C(@"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510") },
        },
        new()
        {
            Id = "fast-startup", Category = "Sistema", Title = "Desativar Inicialização Rápida", Restart = Restart.Windows,
            Description = "O Windows desliga de verdade. Evita drivers \"presos\", problemas com dual boot e atualizações que não terminam; o boot fica alguns segundos mais lento.",
            Actions = new TweakAction[] { L(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1) },
        },
        new()
        {
            Id = "storage-sense", Category = "Sistema", Title = "Desativar o Sensor de Armazenamento",
            Description = "O Windows para de apagar sozinho arquivos temporários, a Lixeira e downloads antigos.",
            Actions = new TweakAction[] { C(@"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, 1) },
        },
        new()
        {
            Id = "bitlocker-auto", Category = "Sistema", Title = "Impedir criptografia automática (BitLocker)", Risk = Risk.Moderado,
            Description = "Evita que o Windows criptografe o disco sozinho em instalações novas. Não descriptografa um disco que já esteja criptografado.",
            Actions = Policies(@"SYSTEM\CurrentControlSet\Control\BitLocker", ("PreventDeviceEncryption", 1)),
        },

        // ---------- Windows Update ----------
        new()
        {
            Id = "update-asap", Category = "Windows Update", Title = "Não receber novidades assim que saírem",
            Description = "Desliga a opção \"Receba as atualizações mais recentes assim que estiverem disponíveis\": você recebe recursos novos só quando estiverem testados.",
            Actions = new TweakAction[] { L(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "IsContinuousInnovationOptedIn", 0, null) },
        },
        new()
        {
            Id = "no-auto-reboot", Category = "Windows Update", Title = "Não reiniciar sozinho com alguém logado",
            Description = "O Windows Update não reinicia o PC enquanto você estiver usando (nem no meio de um jogo ou download).",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", ("NoAutoRebootWithLoggedOnUsers", 1)),
        },
        new()
        {
            Id = "delivery-optimization", Category = "Windows Update", Title = "Não compartilhar atualizações com outros PCs",
            Description = "O PC para de enviar pedaços de atualizações para outros computadores da rede e da internet (economiza upload).",
            // Política oficial (a chave da conta NETWORK SERVICE costuma negar escrita mesmo para administradores).
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", ("DODownloadMode", 0)),
        },
        new()
        {
            Id = "device-apps", Category = "Windows Update", Title = "Não instalar apps de dispositivos sozinho",
            Description = "Ao conectar mouse, fone, impressora, o Windows não baixa automaticamente o app do fabricante.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", ("PreventDeviceMetadataFromNetwork", 1)),
        },

        // ---------- Aparência ----------
        new()
        {
            Id = "dark-mode", Category = "Aparência", Title = "Modo escuro",
            Description = "Windows e apps no tema escuro (combina com o SoNexus).",
            Actions = new TweakAction[] { C(Personalize, "AppsUseLightTheme", 0, 1), C(Personalize, "SystemUsesLightTheme", 0, 1) },
        },
        new()
        {
            Id = "transparency", Category = "Aparência", Title = "Desativar transparência",
            Description = "Barra de tarefas, Iniciar e janelas sem o efeito de vidro. Gasta um pouco menos de GPU.",
            Actions = new TweakAction[] { C(Personalize, "EnableTransparency", 0, 1) },
        },
        new()
        {
            Id = "animations", Category = "Aparência", Title = "Desativar animações", Restart = Restart.Windows,
            Description = "Janelas e menus abrem sem animação, deixando o Windows com sensação mais rápida.",
            Actions = new TweakAction[] { C(@"Control Panel\Desktop", "UserPreferencesMask", new byte[] { 0x90, 0x12, 0x07, 0x80, 0x10, 0x00, 0x00, 0x00 }, new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 }) },
        },
        new()
        {
            Id = "spotlight", Category = "Aparência", Title = "Desativar o Windows Spotlight na área de trabalho", Restart = Restart.Explorer,
            Description = "A área de trabalho para de trocar de imagem sozinha e some o ícone \"Saiba mais sobre esta imagem\".",
            Actions = new TweakAction[] { C(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableSpotlightCollectionOnDesktop", 1, null) },
        },

        // ---------- Menu Iniciar e pesquisa ----------
        new()
        {
            Id = "bing", Category = "Menu Iniciar e pesquisa", Title = "Desativar Bing na pesquisa do Iniciar", Recommended = true, Restart = Restart.Explorer,
            Description = "A pesquisa do Iniciar passa a procurar só no seu PC, sem resultados da web nem Cortana.",
            Actions = new TweakAction[]
            {
                C(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                L(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, null),
                L(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "CortanaConsent", 0, null),
            },
        },
        new()
        {
            Id = "start-recommended", Category = "Menu Iniciar e pesquisa", Title = "Esconder \"Recomendados\" do Iniciar", Restart = Restart.Explorer,
            Description = "Some a seção de arquivos e apps recomendados do menu Iniciar.",
            // A política de usuário só vale em algumas edições; as duas do dispositivo fazem valer no Home/Pro (como no Win11Debloat).
            Actions = new TweakAction[]
            {
                C(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1, null),
                L(@"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection", 1, null),
                L(@"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "IsEducationEnvironment", 1, null),
            },
        },
        new()
        {
            Id = "start-phone-link", Category = "Menu Iniciar e pesquisa", Title = "Tirar o Vincular ao Celular do Iniciar",
            Description = "Remove o painel do celular que aparece ao lado do menu Iniciar.",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", "IsEnabled", 0, null) },
        },

        // ---------- Barra de tarefas ----------
        new()
        {
            Id = "widgets", Category = "Barra de tarefas", Title = "Desativar Widgets", Recommended = true, Restart = Restart.Explorer,
            Description = "Remove o painel de notícias e clima da barra de tarefas.",
            Actions = Policies(@"SOFTWARE\Policies\Microsoft\Dsh", ("AllowNewsAndInterests", 0)),
        },
        new()
        {
            Id = "chat", Category = "Barra de tarefas", Title = "Esconder Chat/Reunir agora", Recommended = true, Restart = Restart.Explorer,
            Description = "Remove o botão de Chat do Teams e o \"Reunir agora\" da barra de tarefas.",
            Actions = new TweakAction[]
            {
                C(Adv, "TaskbarMn", 0, null),
                C(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "HideSCAMeetNow", 1, null),
            },
        },
        new()
        {
            Id = "taskbar-left", Category = "Barra de tarefas", Title = "Barra de tarefas alinhada à esquerda",
            Description = "Botão Iniciar e ícones no canto esquerdo, como no Windows 10.",
            Actions = new TweakAction[] { C(Adv, "TaskbarAl", 0, 1) },
        },
        new()
        {
            Id = "taskbar-search", Category = "Barra de tarefas", Title = "Esconder a caixa de pesquisa",
            Description = "Tira a pesquisa da barra de tarefas (continue pesquisando apertando Windows e digitando).",
            Actions = new TweakAction[] { C(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, 2) },
        },
        new()
        {
            Id = "taskbar-taskview", Category = "Barra de tarefas", Title = "Esconder o botão Visão de Tarefas",
            Description = "Remove o botão de áreas de trabalho virtuais (o atalho Win+Tab continua funcionando).",
            Actions = new TweakAction[] { C(Adv, "ShowTaskViewButton", 0, 1) },
        },
        new()
        {
            Id = "end-task", Category = "Barra de tarefas", Title = "\"Finalizar tarefa\" no clique direito",
            Description = "Clique direito num programa na barra de tarefas mostra \"Finalizar tarefa\" para fechar o que travou.",
            Actions = new TweakAction[] { C(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null) },
        },
        new()
        {
            Id = "last-active-click", Category = "Barra de tarefas", Title = "Clicar no ícone abre a última janela",
            Description = "Com várias janelas do mesmo programa, um clique vai direto para a última que você usou.",
            Actions = new TweakAction[] { C(Adv, "LastActiveClick", 1, null) },
        },
        new()
        {
            Id = "taskbar-never-combine", Category = "Barra de tarefas", Title = "Nunca agrupar botões da barra",
            Description = "Cada janela aparece separada, com o nome dela, como no Windows 7/10.",
            Actions = new TweakAction[] { C(Adv, "TaskbarGlomLevel", 2, 0) },
        },

        // ---------- Explorador de Arquivos ----------
        new()
        {
            Id = "file-ext", Category = "Explorador de Arquivos", Title = "Mostrar extensões de arquivo", Recommended = true, Restart = Restart.Explorer,
            Description = "Mostra .exe, .pdf, .jpg... no Explorer. Ajuda a identificar arquivos falsos.",
            Actions = new TweakAction[] { C(Adv, "HideFileExt", 0, 1) },
        },
        new()
        {
            Id = "hidden-files", Category = "Explorador de Arquivos", Title = "Mostrar arquivos ocultos", Restart = Restart.Explorer,
            Description = "Arquivos e pastas ocultos aparecem no Explorer.",
            Actions = new TweakAction[] { C(Adv, "Hidden", 1, 2) },
        },
        new()
        {
            Id = "classic-menu", Category = "Explorador de Arquivos", Title = "Menu de contexto clássico (Windows 11)", Restart = Restart.Explorer,
            Description = "O botão direito volta a mostrar o menu completo, sem precisar clicar em \"Mostrar mais opções\".",
            Actions = new TweakAction[] { new RegKey(CU, ClassicMenu + @"\InprocServer32", ClassicMenu) },
        },
        new()
        {
            Id = "explorer-this-pc", Category = "Explorador de Arquivos", Title = "Abrir o Explorer em \"Este Computador\"",
            Description = "O Explorer abre mostrando os discos, em vez da página Início com arquivos recentes.",
            Actions = new TweakAction[] { C(Adv, "LaunchTo", 1, null) },
        },
        new()
        {
            Id = "explorer-hide-home", Category = "Explorador de Arquivos", Title = "Esconder \"Início\" do painel lateral", Restart = Restart.Explorer,
            Description = "Remove o atalho Início (arquivos recentes) da lateral do Explorer.",
            Actions = new TweakAction[] { C(@"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", 0, null) },
        },
        new()
        {
            Id = "explorer-hide-gallery", Category = "Explorador de Arquivos", Title = "Esconder \"Galeria\" do painel lateral", Restart = Restart.Explorer,
            Description = "Remove o atalho Galeria (fotos) da lateral do Explorer.",
            Actions = new TweakAction[] { C(@"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0, null) },
        },
        new()
        {
            Id = "explorer-hide-onedrive", Category = "Explorador de Arquivos", Title = "Esconder o OneDrive do painel lateral", Restart = Restart.Explorer,
            Description = "Remove a pasta do OneDrive da lateral do Explorer (o OneDrive continua funcionando).",
            Actions = new TweakAction[] { C(@"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 0, null) },
        },
        new()
        {
            Id = "explorer-duplicate-drives", Category = "Explorador de Arquivos", Title = "Não mostrar pendrives duas vezes", Restart = Restart.Explorer,
            Description = "Pendrives e cartões de memória aparecem só dentro de \"Este Computador\", não repetidos na lateral.",
            Actions = new TweakAction[]
            {
                new RegKeyRemove("duplicate-drives", (LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\{F5FB2C77-0E2F-4A16-A381-3E560C68BC83}")),
            },
        },
        new()
        {
            Id = "context-menu-clean", Category = "Explorador de Arquivos", Title = "Limpar o menu de contexto",
            Description = "Tira \"Conceder acesso a\", \"Incluir na biblioteca\" e \"Compartilhar\" do botão direito. Antes, o SoNexus guarda um backup dessas entradas.",
            Actions = new TweakAction[]
            {
                new RegKeyRemove("context-sharing",
                    (CR, @"*\shellex\ContextMenuHandlers\Sharing"), (CR, @"Directory\Background\shellex\ContextMenuHandlers\Sharing"),
                    (CR, @"Directory\shellex\ContextMenuHandlers\Sharing"), (CR, @"Drive\shellex\ContextMenuHandlers\Sharing"),
                    (CR, @"LibraryFolder\background\shellex\ContextMenuHandlers\Sharing"), (CR, @"UserLibraryFolder\shellex\ContextMenuHandlers\Sharing")),
                new RegKeyRemove("context-library", (CR, @"Folder\ShellEx\ContextMenuHandlers\Library Location")),
                new RegKeyRemove("context-modern-share", (CR, @"*\shellex\ContextMenuHandlers\ModernSharing")),
            },
        },

        // ---------- Multitarefa ----------
        new()
        {
            Id = "snap-assist", Category = "Multitarefa", Title = "Desativar a Assistência de Ajuste",
            Description = "Ao encaixar uma janela na lateral, o Windows não sugere com qual outra janela preencher o resto da tela.",
            Actions = new TweakAction[] { C(Adv, "SnapAssist", 0, null) },
        },
        new()
        {
            Id = "snap-layouts", Category = "Multitarefa", Title = "Desativar os Layouts de Ajuste",
            Description = "Some o menu de layouts ao passar o mouse no botão maximizar e a barra no topo ao arrastar janelas.",
            Actions = new TweakAction[] { C(Adv, "EnableSnapBar", 0, null), C(Adv, "EnableSnapAssistFlyout", 0, null) },
        },
        new()
        {
            Id = "alt-tab-3", Category = "Multitarefa", Title = "Alt+Tab mostra só 3 abas do Edge",
            Description = "O Alt+Tab fica mais limpo, sem listar cada aba do navegador como se fosse uma janela.",
            Actions = new TweakAction[] { C(Adv, "MultiTaskingAltTabFilter", 2, null) },
        },

        // ---------- Recursos do Windows ----------
        new()
        {
            Id = "sandbox", Category = "Recursos do Windows", Title = "Ativar o Windows Sandbox", Restart = Restart.Windows,
            Description = "Um Windows descartável dentro de uma janela, para testar programas suspeitos sem risco. Some tudo ao fechar. (Windows Pro)",
            Actions = new TweakAction[] { new OptionalFeature("Containers-DisposableClientVM") },
        },
        new()
        {
            Id = "wsl", Category = "Recursos do Windows", Title = "Ativar o WSL (Linux no Windows)", Restart = Restart.Windows,
            Description = "Permite instalar Ubuntu e outras distribuições Linux pela Microsoft Store e rodá-las dentro do Windows.",
            Actions = new TweakAction[] { new OptionalFeature("Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform") },
        },
    };

    /// <summary>Passa os valores do registro para o Windows, sem precisar sair da conta.</summary>
    private static void ApplyMouseNow()
    {
        using var k = Reg.Open(CU, @"Control Panel\Mouse");
        int V(string name, int fallback) => int.TryParse(k?.GetValue(name)?.ToString(), out var v) ? v : fallback;
        var values = new[] { V("MouseThreshold1", 6), V("MouseThreshold2", 10), V("MouseSpeed", 1) };
        SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, values, 0x02 /* SPIF_SENDCHANGE */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);

    // ---------- Ponto de restauração e Explorer ----------

    public static async Task<string?> CreateRestorePointAsync()
    {
        // O Windows só deixa criar 1 ponto a cada 24 h, a menos que essa trava seja zerada.
        using (var k = Reg.Create(LM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
            k.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);

        var (code, _, error) = await ProcessRunner.PowerShellAsync(
            "Checkpoint-Computer -Description 'SoNexus' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop");
        if (code == 0) return null;
        return string.IsNullOrWhiteSpace(error)
            ? "Não foi possível criar o ponto. Verifique se a Proteção do Sistema está ativada no disco C:."
            : error.Trim().Split('\n')[0];
    }

    public static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p) { try { p.Kill(); } catch { } }
        }
        Thread.Sleep(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    // ---------- Apps pré-instalados ----------

    /// <summary>
    /// (identificador do pacote, nome, recomendação, removido no padrão). A identificação casa por "contém",
    /// como no Win11Debloat, porque apps de terceiros têm prefixos variáveis (ex.: "xxxx.CandyCrushSaga").
    /// </summary>
    private static readonly (string Id, string Name, string Rec, bool Default)[] AppCatalog =
    {
        ("Clipchamp.Clipchamp", "Clipchamp (editor de vídeo)", "safe", true),
        ("Microsoft.3DBuilder", "3D Builder", "safe", true),
        ("Microsoft.549981C3F5F10", "Cortana", "safe", true),
        ("Microsoft.BingFinance", "Bing Finanças", "safe", true),
        ("Microsoft.BingFoodAndDrink", "Bing Comida e Bebida", "safe", true),
        ("Microsoft.BingHealthAndFitness", "Bing Saúde e Fitness", "safe", true),
        ("Microsoft.BingNews", "Microsoft Notícias", "safe", true),
        ("Microsoft.BingSports", "Bing Esportes", "safe", true),
        ("Microsoft.BingTranslator", "Bing Tradutor", "safe", true),
        ("Microsoft.BingTravel", "Bing Viagens", "safe", true),
        ("Microsoft.BingWeather", "Clima", "safe", true),
        ("Microsoft.Copilot", "Microsoft Copilot", "safe", true),
        ("Microsoft.Windows.AIHub", "Copilot+ AI Hub", "safe", true),
        ("Microsoft.PCManager", "Microsoft PC Manager", "safe", true),
        ("Microsoft.Getstarted", "Dicas", "safe", true),
        ("Microsoft.Messaging", "Mensagens", "safe", true),
        ("Microsoft.Microsoft3DViewer", "Visualizador 3D", "safe", true),
        ("Microsoft.MicrosoftJournal", "Microsoft Journal", "safe", true),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "safe", true),
        ("Microsoft.MicrosoftPowerBIForWindows", "Power BI", "safe", true),
        ("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "safe", true),
        ("Microsoft.MicrosoftStickyNotes", "Notas Autoadesivas", "safe", true),
        ("Microsoft.MixedReality.Portal", "Portal de Realidade Misturada", "safe", true),
        ("Microsoft.NetworkSpeedTest", "Teste de Velocidade de Rede", "safe", true),
        ("Microsoft.Office.OneNote", "OneNote", "safe", true),
        ("Microsoft.Office.Sway", "Sway", "safe", true),
        ("Microsoft.OneConnect", "One Connect", "safe", true),
        ("Microsoft.Print3D", "Print 3D", "safe", true),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", "safe", true),
        ("Microsoft.SkypeApp", "Skype", "safe", true),
        ("Microsoft.Todos", "Microsoft To Do", "safe", true),
        ("Microsoft.Windows.DevHome", "Dev Home", "safe", true),
        ("Microsoft.WindowsAlarms", "Alarmes e Relógio", "safe", true),
        ("Microsoft.WindowsFeedbackHub", "Hub de Comentários", "safe", true),
        ("Microsoft.WindowsMaps", "Mapas", "safe", true),
        ("Microsoft.WindowsSoundRecorder", "Gravador de Som", "safe", true),
        ("Microsoft.XboxApp", "Xbox Console Companion (antigo)", "safe", true),
        ("Microsoft.ZuneVideo", "Filmes e TV", "safe", true),
        ("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", "safe", true),
        ("MicrosoftCorporationII.QuickAssist", "Assistência Rápida", "safe", true),
        ("MicrosoftTeams", "Microsoft Teams (antigo)", "safe", true),
        ("MSTeams", "Microsoft Teams", "safe", true),
        ("ACGMediaPlayer", "ACG Media Player", "safe", true),
        ("ActiproSoftwareLLC", "Actipro Software", "safe", true),
        ("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Adobe Photoshop Express", "safe", true),
        ("Amazon.com.Amazon", "Amazon", "safe", true),
        ("AmazonVideo.PrimeVideo", "Prime Video", "safe", true),
        ("Asphalt8Airborne", "Asphalt 8", "safe", true),
        ("AutodeskSketchBook", "Autodesk SketchBook", "safe", true),
        ("CaesarsSlotsFreeCasino", "Caesars Slots", "safe", true),
        ("COOKINGFEVER", "Cooking Fever", "safe", true),
        ("CyberLinkMediaSuiteEssentials", "CyberLink Media Suite", "safe", true),
        ("DisneyMagicKingdoms", "Disney Magic Kingdoms", "safe", true),
        ("Disney.37853FC22B2CE", "Disney+", "safe", true),
        ("DrawboardPDF", "Drawboard PDF", "safe", true),
        ("Duolingo-LearnLanguagesforFree", "Duolingo", "safe", true),
        ("FACEBOOK.FACEBOOK", "Facebook", "safe", true),
        ("FarmVille2CountryEscape", "FarmVille 2", "safe", true),
        ("Flipboard", "Flipboard", "safe", true),
        ("HiddenCity", "Hidden City", "safe", true),
        ("HULULLC.HULUPLUS", "Hulu", "safe", true),
        ("iHeartRadio", "iHeartRadio", "safe", true),
        ("Facebook.Instagram", "Instagram", "safe", true),
        ("king.com.BubbleWitch3Saga", "Bubble Witch 3", "safe", true),
        ("king.com.CandyCrushSaga", "Candy Crush Saga", "safe", true),
        ("king.com.CandyCrushSodaSaga", "Candy Crush Soda", "safe", true),
        ("LinkedInforWindows", "LinkedIn", "safe", true),
        ("MarchofEmpires", "March of Empires", "safe", true),
        ("4DF9E0F8.Netflix", "Netflix", "safe", true),
        ("NYTCrossword", "NYT Crossword", "safe", true),
        ("OneCalendar", "One Calendar", "safe", true),
        ("PandoraMediaInc", "Pandora", "safe", true),
        ("PhototasticCollage", "Phototastic Collage", "safe", true),
        ("PicsArt-PhotoStudio", "PicsArt", "safe", true),
        ("PolarrPhotoEditorAcademicEdition", "Polarr Photo Editor", "safe", true),
        ("flaregamesGmbH.RoyalRevolt", "Royal Revolt", "safe", true),
        ("Sidia.LiveWallpaper", "Live Wallpaper", "safe", true),
        ("SlingTV", "Sling TV", "safe", true),
        ("SpotifyAB.SpotifyMusic", "Spotify", "safe", true),
        ("BytedancePte.Ltd.TikTok", "TikTok", "safe", true),
        ("TuneInRadio", "TuneIn Radio", "safe", true),
        ("WinZipUniversal", "WinZip", "safe", true),
        // Não saem no padrão: muita gente usa.
        ("Microsoft.BingSearch", "Pesquisa Bing", "optional", false),
        ("Microsoft.GamingApp", "Xbox (app de jogos/Game Pass)", "optional", false),
        ("Microsoft.GetHelp", "Obter Ajuda", "unsafe", false),
        ("Microsoft.M365Companions", "Microsoft 365 Companions", "optional", false),
        ("Microsoft.MSPaint", "Paint 3D", "optional", false),
        ("Microsoft.OutlookForWindows", "Outlook (novo)", "optional", false),
        ("Microsoft.Paint", "Paint", "optional", false),
        ("Microsoft.People", "Pessoas", "safe", false),
        ("Microsoft.RemoteDesktop", "Área de Trabalho Remota", "optional", false),
        ("Microsoft.ScreenSketch", "Ferramenta de Captura", "optional", false),
        ("Microsoft.StartExperiencesApp", "Experiência de Widgets", "optional", false),
        ("Microsoft.Whiteboard", "Whiteboard", "optional", false),
        ("Microsoft.Windows.Photos", "Fotos", "optional", false),
        ("Microsoft.WindowsCalculator", "Calculadora", "optional", false),
        ("Microsoft.WindowsCamera", "Câmera", "optional", false),
        ("Microsoft.windowscommunicationsapps", "Email e Calendário", "safe", false),
        ("Microsoft.WindowsNotepad", "Bloco de Notas", "optional", false),
        ("Microsoft.WindowsStore", "Microsoft Store", "unsafe", false),
        ("Microsoft.WindowsTerminal", "Terminal do Windows", "unsafe", false),
        ("Microsoft.Xbox.TCUI", "Xbox TCUI (usado por jogos)", "unsafe", false),
        ("Microsoft.XboxGameOverlay", "Xbox Game Overlay", "optional", false),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "optional", false),
        ("Microsoft.XboxIdentityProvider", "Login Xbox (usado por jogos)", "unsafe", false),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox Fala para Texto", "unsafe", false),
        ("Microsoft.YourPhone", "Vincular ao Celular", "optional", false),
        ("Microsoft.ZuneMusic", "Media Player", "optional", false),
        ("MicrosoftWindows.CrossDevice", "Experiência entre Dispositivos", "optional", false),
        ("MicrosoftWindows.Client.WebExperience", "Pacote de Experiência Web (Widgets)", "optional", false),
        ("Microsoft.WidgetsPlatformRuntime", "Runtime de Widgets", "optional", false),
        ("LGElectronics.LGMonitorApp", "LG Monitor App", "optional", false),
        ("AD2F1837.", "Apps da HP", "optional", false),
        ("E046963F.LenovoCompanion", "Lenovo Vantage", "optional", false),
        ("LenovoCompanyLimited.LenovoVantageService", "Lenovo Vantage Service", "optional", false),
        ("DellInc.", "Apps da Dell", "optional", false),
    };

    public static async Task<List<BloatApp>> GetBloatAppsAsync()
    {
        var installed = await ProcessRunner.PowerShellJsonAsync<AppxDto>(
            """Get-AppxPackage -AllUsers | Where-Object { -not $_.NonRemovable -and -not $_.IsFramework } | Select-Object @{n='Name';e={"$($_.Name)"}}, @{n='Version';e={"$($_.Version)"}}""");

        var result = new List<BloatApp>();
        foreach (var app in installed.DistinctBy(a => a.Name))
        {
            var match = AppCatalog.FirstOrDefault(c => app.Name.Equals(c.Id, StringComparison.OrdinalIgnoreCase))
                        is { Id: not null } exact ? exact
                        : AppCatalog.FirstOrDefault(c => app.Name.Contains(c.Id, StringComparison.OrdinalIgnoreCase));
            if (match.Id == null) continue;
            var friendly = match.Id.EndsWith('.') ? $"{match.Name}: {app.Name[match.Id.Length..]}" : match.Name;
            result.Add(new BloatApp(app.Name, friendly, app.Version, match.Rec, match.Default, false));
        }

        // OneDrive não é app da Store: sai pelo winget.
        if (HiddenItemsService.GetPrograms().Any(p => p.Name.StartsWith("Microsoft OneDrive", StringComparison.OrdinalIgnoreCase)))
            result.Add(new BloatApp("Microsoft.OneDrive", "OneDrive", "", "optional", false, true));

        return result.OrderByDescending(a => a.Default).ThenBy(a => a.Friendly).ToList();
    }

    public static Task<bool> RemoveAsync(BloatApp app) => app.Winget ? RemoveWingetAsync(app.Name) : RemoveAppAsync(app.Name);

    /// <summary>Remove para todos os usuários e também o pacote "provisionado" (evita voltar em novas contas).</summary>
    public static async Task<bool> RemoveAppAsync(string name)
    {
        var n = ProcessRunner.Quote(name);
        var (_, output, _) = await ProcessRunner.PowerShellAsync($$"""
            $n = {{n}}
            Get-AppxPackage -AllUsers -Name $n | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
            Get-AppxPackage -Name $n | Remove-AppxPackage -ErrorAction SilentlyContinue
            Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $n | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null
            if (Get-AppxPackage -Name $n) { 'FALHOU' } else { 'OK' }
            """);
        return output.Trim().EndsWith("OK");
    }

    private static async Task<bool> RemoveWingetAsync(string id)
    {
        var (code, _, _) = await ProcessRunner.RunAsync("winget.exe", $"uninstall --id {id} -e --silent --accept-source-agreements");
        return code == 0;
    }

    private sealed class AppxDto
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
    }

    // ---------- Exportar / importar escolhas ----------

    public static void Export(string path, DebloatProfile profile) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));

    public static DebloatProfile Import(string path) =>
        JsonSerializer.Deserialize<DebloatProfile>(File.ReadAllText(path)) ?? new DebloatProfile(new(), new());
}
