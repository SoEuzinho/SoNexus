using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SoNexus.Services;

internal sealed class BleRequest
{
    public long Id { get; set; }
    public string Op { get; set; } = "";
    public ulong Address { get; set; }
    public bool On { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public int Seconds { get; set; }
}

internal sealed class BleReply
{
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string? Text { get; set; }
    public List<BleLedFound>? Found { get; set; }
}

/// <summary>
/// Processo auxiliar do Bluetooth: o mesmo SoNexus.exe aberto sem administrador (<c>--ble-helper</c>),
/// falando com o app por um pipe que só o usuário do app pode abrir. Fecha sozinho quando o app fecha.
/// </summary>
public static class BleHelper
{
    public const string Arg = "--ble-helper";
    private static readonly Regex PipeName = new(@"^SoNexus\.Ble\.[0-9a-f]{32}$");

    public static void Run(string pipeName, string clientSid)
    {
        if (!PipeName.IsMatch(pipeName)) return;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // O app pode rodar como outro usuário (administrador digitado no aviso do Windows).
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(clientSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        using (var cts = new CancellationTokenSource(30_000))
        {
            try { pipe.WaitForConnectionAsync(cts.Token).Wait(); }
            catch { return; }
        }

        var leds = BleLedService.Instance;
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        void Reply(BleReply r)
        {
            lock (writer)
            {
                try { writer.WriteLine(JsonSerializer.Serialize(r)); } catch { }
            }
        }

        // Cores e liga/desliga numa fila só (em ordem); se a fita demora a responder, fica só a cor mais nova.
        var queue = new BlockingCollection<BleRequest>();
        var worker = new Thread(() =>
        {
            var batch = new List<BleRequest>();
            foreach (var first in queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                while (queue.TryTake(out var more)) batch.Add(more);
                for (var i = 0; i < batch.Count; i++)
                {
                    var q = batch[i];
                    if (q.Op == "color")
                    {
                        if (batch.Skip(i + 1).Any(n => n.Address == q.Address)) continue; // já tem pedido mais novo
                        leds.SetColor(q.Address, q.R, q.G, q.B);
                    }
                    else Reply(new BleReply { Id = q.Id, Ok = leds.SetPower(q.Address, q.On) });
                }
            }
        }) { IsBackground = true, Name = "SoNexus Bluetooth" };
        worker.Start();

        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                BleRequest? q;
                try { q = JsonSerializer.Deserialize<BleRequest>(line); }
                catch { continue; }
                if (q == null) continue;
                switch (q.Op)
                {
                    case "scan":
                        var id = q.Id;
                        var seconds = Math.Clamp(q.Seconds, 1, 30);
                        _ = Task.Run(async () =>
                        {
                            var (found, error) = await leds.ScanAsync(seconds);
                            Reply(new BleReply { Id = id, Ok = error == null, Text = error, Found = found });
                        });
                        break;
                    case "color":
                    case "power":
                        queue.Add(q);
                        break;
                    case "status":
                        Reply(new BleReply { Id = q.Id, Ok = true, Text = leds.StatusOf(q.Address) });
                        break;
                    case "forget":
                        leds.Forget(q.Address);
                        Reply(new BleReply { Id = q.Id, Ok = true });
                        break;
                }
            }
        }
        catch (IOException) { }
        queue.CompleteAdding();
        worker.Join(5000);
        leds.Dispose();
    }
}

/// <summary>Lado do app (administrador): abre o auxiliar quando precisa e repassa os pedidos.</summary>
public sealed class BleHelperClient : IBleLeds
{
    public static BleHelperClient Instance { get; } = new();

    private readonly object _start = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<BleReply?>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private long _nextId, _retryAfter;
    private string _error = "Desconectada";
    private bool _disposed;

    private StreamWriter? Ensure()
    {
        lock (_start)
        {
            if (_pipe is { IsConnected: true } && _writer != null) return _writer;
            if (_disposed || Environment.TickCount64 < _retryAfter) return null;
            Close();
            try
            {
                var name = $"SoNexus.Ble.{Guid.NewGuid():N}";
                var sid = WindowsIdentity.GetCurrent().User!.Value;
                var helper = Unelevated.Start(Environment.ProcessPath!, $"{BleHelper.Arg} {name} {sid}");
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(15_000);
                    // Confere que do outro lado está o processo que acabamos de abrir.
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != helper)
                        throw new InvalidOperationException("o canal do Bluetooth não confere");
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                new Thread(() => ReadLoop(reader, pipe)) { IsBackground = true, Name = "SoNexus Bluetooth" }.Start();
                return _writer;
            }
            catch (Exception ex)
            {
                _error = $"Sem conexão: o auxiliar do Bluetooth não abriu ({ex.Message})";
                AppLog.Write($"Bluetooth: {ex.Message}");
                Close();
                _retryAfter = Environment.TickCount64 + 15_000;
                return null;
            }
        }
    }

    private void ReadLoop(StreamReader reader, NamedPipeClientStream pipe)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var r = JsonSerializer.Deserialize<BleReply>(line);
                if (r != null && _pending.TryRemove(r.Id, out var waiting)) waiting.TrySetResult(r);
            }
        }
        catch { }
        lock (_start)
        {
            if (_pipe == pipe)
            {
                _error = "Sem conexão: o auxiliar do Bluetooth fechou";
                Close();
            }
        }
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiting)) waiting.TrySetResult(null);
    }

    private bool Send(BleRequest q)
    {
        var w = Ensure();
        if (w == null) return false;
        try
        {
            lock (w) w.WriteLine(JsonSerializer.Serialize(q));
            return true;
        }
        catch (Exception)
        {
            lock (_start) Close();
            return false;
        }
    }

    private async Task<BleReply?> CallAsync(BleRequest q, int timeoutMs)
    {
        q.Id = Interlocked.Increment(ref _nextId);
        var waiting = new TaskCompletionSource<BleReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[q.Id] = waiting;
        if (!Send(q))
        {
            _pending.TryRemove(q.Id, out _);
            return null;
        }
        var done = await Task.WhenAny(waiting.Task, Task.Delay(timeoutMs)) == waiting.Task;
        _pending.TryRemove(q.Id, out _);
        return done ? waiting.Task.Result : null;
    }

    private BleReply? Call(BleRequest q, int timeoutMs) => Task.Run(() => CallAsync(q, timeoutMs)).GetAwaiter().GetResult();

    public async Task<(List<BleLedFound> Found, string? Error)> ScanAsync(int seconds = 10)
    {
        var r = await Task.Run(() => CallAsync(new BleRequest { Op = "scan", Seconds = seconds }, seconds * 1000 + 15_000));
        if (r == null) return (new(), _error.Replace("Sem conexão: ", "Bluetooth indisponível: "));
        return (r.Found ?? new(), r.Text);
    }

    public bool SetPower(ulong address, bool on) =>
        Call(new BleRequest { Op = "power", Address = address, On = on }, 15_000)?.Ok == true;

    /// <summary>Sem resposta: o auxiliar fica só com a cor mais nova se a fita atrasar.</summary>
    public void SetColor(ulong address, byte r, byte g, byte b) =>
        Send(new BleRequest { Op = "color", Address = address, R = r, G = g, B = b });

    /// <summary>A tela pergunta a cada 2 s: não abre o auxiliar nem espera muito.</summary>
    public string StatusOf(ulong address)
    {
        lock (_start)
        {
            if (_pipe is not { IsConnected: true }) return _error;
        }
        return Call(new BleRequest { Op = "status", Address = address }, 1000)?.Text ?? _error;
    }

    public void Forget(ulong address) => Call(new BleRequest { Op = "forget", Address = address }, 3000);

    /// <summary>Fechar o canal encerra o auxiliar (e solta a fita).</summary>
    private void Close()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _writer = null;
    }

    public void Dispose()
    {
        lock (_start)
        {
            _disposed = true;
            Close();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

/// <summary>Abre um programa com o usuário comum da área de trabalho (o do Explorer), sem administrador.</summary>
internal static class Unelevated
{
    /// <returns>Id do processo aberto.</returns>
    public static uint Start(string exe, string args)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("a área de trabalho do Windows não está aberta");
        GetWindowThreadProcessId(shell, out var shellPid);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
        if (process == IntPtr.Zero) throw new Win32Exception();
        var env = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenDuplicate, out var token)) throw new Win32Exception();
            try
            {
                const uint access = TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId;
                if (!DuplicateTokenEx(token, access, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var primary))
                    throw new Win32Exception();
                try
                {
                    env = UserEnvironment(primary);
                    var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                    var cmd = new StringBuilder($"\"{exe}\" {args}");
                    if (!CreateProcessWithTokenW(primary, 0, exe, cmd, CreateUnicodeEnvironment | CreateNoWindow, env,
                            Path.GetDirectoryName(exe), ref si, out var pi))
                        throw new Win32Exception();
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                    return (uint)pi.dwProcessId;
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(token); }
        }
        finally
        {
            CloseHandle(process);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
    }

    /// <summary>
    /// Ambiente do usuário + <c>__COMPAT_LAYER=RunAsInvoker</c>: o SoNexus.exe pede administrador no manifesto,
    /// e isso faz o Windows abri-lo sem pedir.
    /// </summary>
    private static IntPtr UserEnvironment(IntPtr token)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (CreateEnvironmentBlock(out var block, token, false))
        {
            try
            {
                var p = block;
                while (Marshal.PtrToStringUni(p) is { Length: > 0 } entry)
                {
                    var eq = entry.IndexOf('=', 1);
                    if (eq > 0) vars[entry[..eq]] = entry[(eq + 1)..];
                    p += (entry.Length + 1) * 2;
                }
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        vars["__COMPAT_LAYER"] = "RunAsInvoker";
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x1, TokenDuplicate = 0x2, TokenQuery = 0x8, TokenAdjustDefault = 0x80, TokenAdjustSessionId = 0x100;
    private const uint CreateUnicodeEnvironment = 0x400, CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string? app, StringBuilder cmd, uint flags,
        IntPtr env, string? dir, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr block);
}
