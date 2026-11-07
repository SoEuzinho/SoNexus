using System.Windows;
using SoNexus.Services;

namespace SoNexus;

public partial class App : Application
{
    // Uma cópia só: abrir de novo traz a janela que já está rodando (às vezes escondida na bandeja).
    private const string InstanceName = @"Local\SoNexus.SoStudio";
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;

    /// <summary>"Fechar completamente": encerra mesmo com a opção de segundo plano ligada.</summary>
    public static bool Exiting { get; private set; }

    public static void ExitCompletely()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Auxiliar do Bluetooth (aberto pelo próprio SoNexus, sem administrador): sem janela nem trava de cópia única.
        if (e.Args.Length == 3 && e.Args[0] == BleHelper.Arg)
        {
            Exiting = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new Thread(() =>
            {
                try { BleHelper.Run(e.Args[1], e.Args[2]); }
                catch (Exception ex) { AppLog.Write($"Auxiliar Bluetooth: {ex.Message}"); }
                Dispatcher.BeginInvoke(() => Shutdown());
            }) { IsBackground = true }.Start();
            return;
        }

        // SONEXUS_INSTANCE: só para testes automáticos (rodar uma segunda cópia separada).
        var name = InstanceName + Environment.GetEnvironmentVariable("SONEXUS_INSTANCE");
        bool first;
        try
        {
            _instance = new Mutex(true, name, out first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
        }
        catch (UnauthorizedAccessException)
        {
            // A cópia aberta roda como administrador e esta não: não dá nem para avisá-la.
            first = false;
        }
        if (!first || _showSignal == null)
        {
            try { _showSignal?.Set(); } catch { }
            Exiting = true;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        SecureStore.MigrateOldBackups();
        AppLog.Write($"SoNexus {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · admin={SensorDriver.IsAdmin} · PawnIO={SensorDriver.IsInstalled}");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"ERRO: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "SoNexus — erro", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // Erros em tarefas de fundo que ninguém esperou: registra em vez de sumir sem aviso.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write($"ERRO em tarefa: {args.Exception}");
            args.SetObserved();
        };

        // Se o programa travar, as ventoinhas não podem ficar presas numa velocidade fixa.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write($"ERRO FATAL: {args.ExceptionObject}");
            FanControlService.Instance.RestoreAll();
        };

        var window = new MainWindow();
        MainWindow = window;
        // Aberto pelo "Iniciar com o Windows": começa direto na bandeja.
        // (Com termos novos ainda não aceitos, abre a janela mesmo assim para pedir o aceite.)
        if (e.Args.Contains("--minimized") && Terms.Accepted) window.HideToTray(showHint: false);
        else
        {
            window.Show();
            // Primeira vez (ou termos novos): apresenta o app e pede o aceite.
            // Testes automáticos pulam, para não travar esperando um clique.
            if ((!AppSettings.Current.WelcomeShown || !Terms.Accepted) && Environment.GetEnvironmentVariable("SONEXUS_NOWELCOME") == null)
                window.Dispatcher.BeginInvoke(() => new Views.WelcomeWindow { Owner = window }.ShowDialog(),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Outra cópia pediu para aparecer.
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();

        // Abrir os sensores leva alguns segundos; não trava a janela.
        Task.Run(() =>
        {
            HardwareService.Instance.Start();
            if (HardwareService.Instance.Error is { } err) AppLog.Write($"Sensores: {err}");
            FanControlService.Instance.Start();
        });
        // Volta a iluminação escolhida da última vez (espera o OpenRGB abrir, se for o caso).
        RgbService.Instance.RestoreOnStartup();
        if (AppSettings.Current.GameBoost && SensorDriver.IsAdmin) GameBoostService.Instance.Start();
    }

    /// <summary>Libera a "trava" de cópia única (para reabrir o app, ex.: depois de instalar o driver de sensores).</summary>
    public void ReleaseInstance()
    {
        try { _instance?.ReleaseMutex(); } catch { }
        _instance?.Dispose();
        _instance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow main)
        {
            FanControlService.Instance.RestoreAll();
            GameBoostService.Instance.Stop(); // devolve as prioridades originais
            HardwareService.Instance.Dispose();
            RgbService.Instance.Dispose();
            main.DisposeTray();
        }
        base.OnExit(e);
    }
}
