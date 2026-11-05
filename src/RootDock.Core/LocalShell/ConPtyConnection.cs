using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RootDock.Core.Terminal;
using static RootDock.Core.LocalShell.ConPtyNative;

namespace RootDock.Core.LocalShell;

/// <summary>Terminal local (PowerShell, CMD, WSL, Git Bash) via ConPTY, sem Cygwin.</summary>
public sealed class ConPtyConnection : ITerminalConnection
{
    private readonly string _commandLine;
    private readonly string _workingDirectory;
    private readonly OutboundQueue _outbound = new();
    private readonly object _pcGate = new();

    private IntPtr _pseudoConsole;
    private SafeProcessHandle? _process;
    private FileStream? _input;
    private FileStream? _output;
    private int _closed;
    private string? _closeReason;

    public ConPtyConnection(string commandLine, string? workingDirectory = null)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string?>? Closed;

    public bool IsConnected => _closed == 0 && _process is not null;

    public Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao criar o pipe de entrada do terminal local.");
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao criar o pipe de saída do terminal local.");
        }

        var hr = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
        // O pseudo console duplica as pontas que usa; as nossas cópias podem ser fechadas.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "Falha ao criar o pseudo console (ConPTY).");
        }

        _input = new FileStream(inputWrite, FileAccess.Write, 1, isAsync: false);
        _output = new FileStream(outputRead, FileAccess.Read, 1, isAsync: false);

        try
        {
            _process = StartProcess();
        }
        catch
        {
            ClosePseudoConsoleOnce();
            _input.Dispose();
            _output.Dispose();
            throw;
        }

        _outbound.Start(
            write: data =>
            {
                _input.Write(data, 0, data.Length);
                _input.Flush();
            },
            resize: (c, r) =>
            {
                lock (_pcGate)
                {
                    if (_pseudoConsole != IntPtr.Zero)
                    {
                        ResizePseudoConsole(_pseudoConsole, new Coord(c, r));
                    }
                }
            },
            onError: ex => Close(ex.Message));

        return Task.CompletedTask;
    }

    public void Start()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            Closed?.Invoke(_closeReason);
            return;
        }

        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "conpty-wait" }.Start();
    }

    public void Write(ReadOnlySpan<byte> data) => _outbound.EnqueueData(data);

    public void Resize(int columns, int rows)
    {
        if (columns > 0 && rows > 0)
        {
            _outbound.EnqueueResize(columns, rows);
        }
    }

    public void Dispose() => Close(null);

    private SafeProcessHandle StartProcess()
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributeList = Marshal.AllocHGlobal(size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao preparar atributos do processo.");
            }

            initialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, _pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao associar o pseudo console ao processo.");
            }

            var startup = new StartupInfoEx { lpAttributeList = attributeList };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // Sem herdar stdin/stdout do RootDock: toda E/S passa pelo pseudo console.
            startup.StartupInfo.dwFlags = StartfUseStdHandles;

            var commandLine = (_commandLine + '\0').ToCharArray();
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent,
                    IntPtr.Zero, _workingDirectory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Não foi possível iniciar '{_commandLine}'.");
            }

            CloseHandle(info.hThread);
            return new SafeProcessHandle(info.hProcess, ownsHandle: true);
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            Marshal.FreeHGlobal(attributeList);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        string? reason = null;
        try
        {
            while (true)
            {
                var read = _output!.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            // Pipe fechado: o shell terminou.
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        finally
        {
            _output?.Dispose();
        }

        Close(reason);
    }

    private void WaitForExit()
    {
        // O SafeHandle mantém o handle vivo durante a espera, mesmo que Close o descarte.
        WaitForSingleObject(_process!, Infinite);
        // Com o processo encerrado, fechar o pseudo console libera o EOF para a leitura.
        ClosePseudoConsoleOnce();
    }

    private void ClosePseudoConsoleOnce()
    {
        IntPtr pc;
        lock (_pcGate)
        {
            pc = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
        }

        if (pc != IntPtr.Zero)
        {
            // Em versões antigas do Windows 10 esta chamada bloqueia até a saída ser drenada; a leitura segue ativa.
            Task.Run(() => ClosePseudoConsole(pc));
        }
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _closeReason = reason;
        _outbound.Dispose();
        // Fechar o pseudo console pede ao shell para encerrar.
        ClosePseudoConsoleOnce();
        try
        {
            _input?.Dispose();
        }
        catch (IOException)
        {
        }

        var process = _process;
        if (process is not null)
        {
            _ = Task.Run(() =>
            {
                // Se o shell não sair sozinho em 3 s (aba fechada pelo usuário), encerra o processo.
                if (WaitForSingleObject(process, 3000) != 0)
                {
                    TerminateProcess(process, 1);
                }

                process.Dispose();
            });
        }

        Closed?.Invoke(reason);
    }
}
