using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace RootDock.Core.Shell;

/// <summary>
/// Arquivo ou pasta oferecido ao Windows sem existir no disco. <see cref="RelativePath"/> usa "\"
/// (ex.: "pasta\sub\arquivo.txt"); as pastas vêm antes do que está dentro delas.
/// </summary>
public sealed record VirtualFile(string RelativePath, bool IsDirectory, long Size, DateTime Modified,
    Func<Stream, CancellationToken, Task>? WriteContent = null);

/// <summary>Lista montada para o arrastar, com um aviso quando algo ficou de fora.</summary>
public sealed record VirtualFileList(IReadOnlyList<VirtualFile> Files, string? Warning = null);

/// <summary>Como terminou: arquivos entregues, bytes e o primeiro erro (null = deu certo ou nada foi pedido).</summary>
public sealed record VirtualFileDropResult(bool Dropped, int FilesCompleted, long Bytes, string? Error, string? Warning);

/// <summary>
/// Arrastar para o Explorer sem baixar antes: oferece "FileGroupDescriptorW" (nomes, tamanhos,
/// datas e pastas) e "FileContents" (o conteúdo, lido do servidor enquanto o Windows grava). É o
/// mesmo mecanismo dos anexos do Outlook: o Explorer escolhe a pasta, pergunta sobre substituir e
/// mostra o próprio progresso.
/// <para>
/// Suporta cópia assíncrona (IDataObjectAsyncCapability): o Explorer copia numa thread dele e o
/// arrastar termina na hora. Este objeto não é "ágil" (sem IMarshal) e é publicado a partir do
/// MTA (<see cref="ShellDrag"/>): as chamadas do Explorer chegam em threads do MTA, nunca na
/// thread da interface, então ler do servidor não trava a janela.
/// </para>
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComDataObject, IDataObjectAsyncCapability, ICustomQueryInterface
{
    private const int MaxNameChars = 259; // cFileName[MAX_PATH] com o terminador

    private static readonly short CfDescriptor = Format("FileGroupDescriptorW");
    private static readonly short CfContents = Format("FileContents");
    private static readonly short CfPreferredDropEffect = Format("Preferred DropEffect");
    private static readonly short CfIsShowingLayered = Format("IsShowingLayered");

    private readonly object _gate = new();
    private readonly Task<VirtualFileList> _list;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<short, byte[]> _stored = [];
    private readonly List<VirtualFileStream> _streams = [];
    private bool _async;
    private bool _inOperation;
    private bool _finished;
    private int _completed;
    private long _bytes;
    private string? _error;

    /// <param name="load">Monta a lista (pode varrer pastas no servidor); começa na hora, em segundo plano.</param>
    public VirtualFileDataObject(Func<CancellationToken, Task<VirtualFileList>> load)
    {
        var token = _cts.Token;
        _list = Task.Run(() => load(token), token);
    }

    /// <summary>A cópia terminou (ou o arrastar foi cancelado). Chamado fora da thread da interface.</summary>
    public event Action<VirtualFileDropResult>? Finished;

    /// <summary>O Explorer está desenhando a dica "Copiar para..." (o cursor volta a ser a seta).</summary>
    internal bool IsShowingLayered
    {
        get
        {
            lock (_gate)
            {
                return _stored.TryGetValue(CfIsShowingLayered, out var data) && data.Length >= 4 && BitConverter.ToInt32(data) != 0;
            }
        }
    }

    // ------------------------------------------------------------------ IDataObject

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == CfDescriptor && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(Descriptor(WaitList().Files));
            return;
        }

        if (format.cfFormat == CfContents && Has(format, TYMED.TYMED_ISTREAM))
        {
            var files = WaitList().Files;
            if (format.lindex < 0 || format.lindex >= files.Count || files[format.lindex] is not { IsDirectory: false, WriteContent: not null } file)
            {
                throw new COMException("Item sem conteúdo.", Hr.DV_E_LINDEX);
            }

            var stream = new VirtualFileStream(file, this, _cts.Token);
            lock (_gate)
            {
                _streams.Add(stream);
            }

            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)) };
            return;
        }

        if (format.cfFormat == CfPreferredDropEffect && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(BitConverter.GetBytes(ShellDrag.DropEffectCopy));
            return;
        }

        byte[]? stored;
        lock (_gate)
        {
            _stored.TryGetValue(format.cfFormat, out stored);
        }

        if (stored is not null && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(stored);
            return;
        }

        throw new COMException("Formato não oferecido.", Hr.DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("Não suportado.", Hr.E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfContents)
        {
            return Has(format, TYMED.TYMED_ISTREAM) ? Hr.S_OK : Hr.DV_E_TYMED;
        }

        if (format.cfFormat == CfDescriptor || format.cfFormat == CfPreferredDropEffect)
        {
            return Has(format, TYMED.TYMED_HGLOBAL) ? Hr.S_OK : Hr.DV_E_TYMED;
        }

        lock (_gate)
        {
            return _stored.ContainsKey(format.cfFormat) && Has(format, TYMED.TYMED_HGLOBAL) ? Hr.S_OK : Hr.DV_E_FORMATETC;
        }
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return Hr.DATA_S_SAMEFORMATETC;
    }

    /// <summary>
    /// Guarda o que o Windows anota no objeto durante o arrastar (imagem do arrastar, "Copiar para
    /// ...", efeito realizado). Só HGLOBAL, que é o que o shell usa.
    /// </summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
        {
            throw new COMException("Só HGLOBAL.", Hr.E_NOTIMPL);
        }

        var size = (int)Native.GlobalSize(medium.unionmember);
        var data = new byte[size];
        var p = Native.GlobalLock(medium.unionmember);
        try
        {
            Marshal.Copy(p, data, 0, size);
        }
        finally
        {
            Native.GlobalUnlock(medium.unionmember);
        }

        lock (_gate)
        {
            _stored[formatIn.cfFormat] = data;
        }

        if (release)
        {
            Native.ReleaseStgMedium(ref medium);
        }
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            throw new COMException("Não suportado.", Hr.E_NOTIMPL);
        }

        var formats = new List<FORMATETC>
        {
            Etc(CfDescriptor, TYMED.TYMED_HGLOBAL),
            Etc(CfContents, TYMED.TYMED_ISTREAM),
            Etc(CfPreferredDropEffect, TYMED.TYMED_HGLOBAL),
        };
        lock (_gate)
        {
            formats.AddRange(_stored.Keys.Select(cf => Etc(cf, TYMED.TYMED_HGLOBAL)));
        }

        return new FormatEnumerator([.. formats]);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return Hr.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Não suportado.", Hr.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return Hr.OLE_E_ADVISENOTSUPPORTED;
    }

    // ------------------------------------------------------------------ cópia assíncrona

    public int SetAsyncMode(int fDoOpAsync)
    {
        lock (_gate)
        {
            _async = fDoOpAsync != 0;
        }

        return Hr.S_OK;
    }

    public int GetAsyncMode(out int pfIsOpAsync)
    {
        lock (_gate)
        {
            pfIsOpAsync = _async ? 1 : 0;
        }

        return Hr.S_OK;
    }

    public int StartOperation(IntPtr pbcReserved)
    {
        lock (_gate)
        {
            _inOperation = true;
        }

        return Hr.S_OK;
    }

    public int InOperation(out int pfInAsyncOp)
    {
        lock (_gate)
        {
            pfInAsyncOp = _inOperation ? 1 : 0;
        }

        return Hr.S_OK;
    }

    public int EndOperation(int hResult, IntPtr pbcReserved, uint dwEffects)
    {
        lock (_gate)
        {
            _inOperation = false;
            if (hResult < 0 && _error is null && hResult != Hr.E_ABORT && hResult != Hr.HRESULT_ERROR_CANCELLED)
            {
                _error = Marshal.GetExceptionForHR(hResult)?.Message;
            }
        }

        Finish(dropped: true);
        return Hr.S_OK;
    }

    // ------------------------------------------------------------------ objeto não ágil

    /// <summary>
    /// Sem IMarshal/IAgileObject o COM usa o marshaling padrão: o objeto fica no apartamento em
    /// que foi publicado (o MTA) e o Explorer não chama a thread da interface.
    /// </summary>
    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return iid == Native.IID_IMarshal || iid == Native.IID_IAgileObject
            ? CustomQueryInterfaceResult.Failed
            : CustomQueryInterfaceResult.NotHandled;
    }

    // ------------------------------------------------------------------ fim

    /// <summary>O arrastar acabou (chamado por <see cref="ShellDrag"/>): sem cópia assíncrona em andamento, encerra.</summary>
    internal void DragEnded(bool dropped)
    {
        lock (_gate)
        {
            if (_inOperation)
            {
                return; // o Explorer continua copiando; EndOperation encerra
            }
        }

        Finish(dropped);
    }

    internal void FileCompleted(long bytes)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void FileFailed(string message)
    {
        lock (_gate)
        {
            _error ??= message;
        }
    }

    private void Finish(bool dropped)
    {
        VirtualFileStream[] streams;
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            streams = [.. _streams];
            _streams.Clear();
        }

        _cts.Cancel(); // varredura e leituras que ainda estiverem rodando
        foreach (var stream in streams)
        {
            stream.Close();
        }

        var warning = _list.IsCompletedSuccessfully ? _list.Result.Warning : null;
        Finished?.Invoke(new VirtualFileDropResult(dropped, _completed, Interlocked.Read(ref _bytes), _error, warning));
    }

    // ------------------------------------------------------------------ apoio

    private VirtualFileList WaitList()
    {
        try
        {
            return _list.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileFailed(ex.Message);
            throw new COMException(ex.Message, Hr.E_FAIL);
        }
    }

    /// <summary>FILEGROUPDESCRIPTORW: contagem + um FILEDESCRIPTORW (592 bytes) por item.</summary>
    internal static byte[] Descriptor(IReadOnlyList<VirtualFile> files)
    {
        const int Size = 592;
        const int Flags = Native.FD_ATTRIBUTES | Native.FD_FILESIZE | Native.FD_PROGRESSUI | Native.FD_UNICODE;
        var data = new byte[4 + files.Count * Size];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var b = data.AsSpan(4 + i * Size, Size);
            var time = FileTime(f.Modified);
            BitConverter.TryWriteBytes(b[0..4], Flags | (time != 0 ? Native.FD_WRITESTIME : 0));
            BitConverter.TryWriteBytes(b[36..40], f.IsDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL);
            BitConverter.TryWriteBytes(b[56..64], time);
            var size = f.IsDirectory ? 0 : Math.Max(0, f.Size);
            BitConverter.TryWriteBytes(b[64..68], (uint)(size >> 32));
            BitConverter.TryWriteBytes(b[68..72], (uint)(size & 0xFFFFFFFF));
            var name = f.RelativePath.Length > MaxNameChars ? f.RelativePath[..MaxNameChars] : f.RelativePath;
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(b[72..]);
        }

        return data;
    }

    private static long FileTime(DateTime time)
    {
        try
        {
            return time.Year < 1602 ? 0 : time.Kind == DateTimeKind.Utc ? time.ToFileTimeUtc() : time.ToFileTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static STGMEDIUM HGlobal(byte[] data)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, data.Length);
        if (h == IntPtr.Zero)
        {
            throw new COMException("Sem memória.", Hr.E_OUTOFMEMORY);
        }

        var p = Native.GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        Native.GlobalUnlock(h);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    private static bool Has(FORMATETC format, TYMED tymed) => (format.tymed & tymed) != 0;

    private static FORMATETC Etc(short cf, TYMED tymed) =>
        new() { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = IntPtr.Zero, tymed = tymed };

    private static short Format(string name) => unchecked((short)(ushort)Native.RegisterClipboardFormat(name));
}

/// <summary>
/// Conteúdo de um arquivo virtual: um produtor (download do servidor) escreve num Pipe com limite
/// de memória e o Explorer lê. Criado e chamado só em threads do MTA.
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileStream : IStream
{
    private readonly VirtualFile _file;
    private readonly VirtualFileDataObject _owner;
    private readonly CancellationTokenSource _cts;
    private readonly object _gate = new();
    private Pipe? _pipe;
    private Stream? _reader;
    private long _position;
    private bool _done;

    internal VirtualFileStream(VirtualFile file, VirtualFileDataObject owner, CancellationToken ct)
    {
        _file = file;
        _owner = owner;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var total = 0;
        var end = false;
        try
        {
            var reader = Start();
            // Leitura curta significa "fim" para muitos leitores de IStream: completa o pedido.
            while (total < cb)
            {
                var n = reader.Read(pv, total, cb - total);
                if (n == 0)
                {
                    end = true;
                    break;
                }

                total += n;
            }
        }
        catch (Exception ex) when (ex is not COMException)
        {
            var message = ex is OperationCanceledException ? "Cópia cancelada." : $"{Path.GetFileName(_file.RelativePath)}: {ex.Message}";
            if (ex is not OperationCanceledException)
            {
                _owner.FileFailed(message);
            }

            Close();
            throw new COMException(message, Hr.STG_E_READFAULT);
        }

        _position += total;
        if (end || (_file.Size > 0 && _position >= _file.Size))
        {
            Complete(); // quem sabe o tamanho pode parar sem pedir o "fim"
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Somente leitura.", Hr.STG_E_ACCESSDENIED);

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var target = dwOrigin switch
        {
            0 => dlibMove,
            1 => _position + dlibMove,
            2 => _file.Size + dlibMove,
            _ => -1,
        };
        if (target != _position)
        {
            throw new COMException("Leitura só para a frente.", Hr.STG_E_INVALIDFUNCTION);
        }

        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void SetSize(long libNewSize) => throw new COMException("Somente leitura.", Hr.STG_E_ACCESSDENIED);

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        var counter = Marshal.AllocCoTaskMem(4);
        try
        {
            while (done < cb)
            {
                var want = (int)Math.Min(buffer.Length, cb - done);
                Read(buffer, want, counter);
                var n = Marshal.ReadInt32(counter);
                if (n == 0)
                {
                    break;
                }

                pstm.Write(buffer, n, IntPtr.Zero);
                done += n;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(counter);
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbRead, done);
        }

        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbWritten, done);
        }
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => throw new COMException("Não suportado.", Hr.STG_E_INVALIDFUNCTION);

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Não suportado.", Hr.STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Não suportado.", Hr.STG_E_INVALIDFUNCTION);

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = Math.Max(0, _file.Size),
            pwcsName = (grfStatFlag & 1) != 0 ? null! : Path.GetFileName(_file.RelativePath),
        };
    }

    public void Clone(out IStream ppstm) => throw new COMException("Não suportado.", Hr.E_NOTIMPL);

    /// <summary>Começa a baixar no primeiro Read (o Explorer pode pedir o fluxo e não ler, ex.: ao pular um arquivo).</summary>
    private Stream Start()
    {
        lock (_gate)
        {
            if (_reader is not null)
            {
                return _reader;
            }

            _cts.Token.ThrowIfCancellationRequested();
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8 << 20, resumeWriterThreshold: 4 << 20, useSynchronizationContext: false));
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await using (var output = pipe.Writer.AsStream(leaveOpen: true))
                    {
                        await _file.WriteContent!(output, token).ConfigureAwait(false);
                    }

                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
            }, CancellationToken.None);
            _pipe = pipe;
            _reader = pipe.Reader.AsStream();
            return _reader;
        }
    }

    private void Complete()
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }

            _done = true;
        }

        _owner.FileCompleted(_position);
    }

    /// <summary>Para o download (cancelado, erro ou fim do arrastar). Pode ser chamado mais de uma vez.</summary>
    internal void Close()
    {
        _cts.Cancel();
        lock (_gate)
        {
            _pipe?.Reader.Complete();
        }
    }
}

/// <summary>Enumerador simples de formatos.</summary>
[ComVisible(true)]
internal sealed class FormatEnumerator(FORMATETC[] formats, int start = 0) : IEnumFORMATETC
{
    private int _index = start;

    public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
    {
        var n = 0;
        while (n < celt && _index < formats.Length && n < rgelt.Length)
        {
            rgelt[n++] = formats[_index++];
        }

        if (pceltFetched is { Length: > 0 })
        {
            pceltFetched[0] = n;
        }

        return n == celt ? Hr.S_OK : Hr.S_FALSE;
    }

    public int Skip(int celt)
    {
        _index = Math.Min(formats.Length, _index + celt);
        return _index < formats.Length ? Hr.S_OK : Hr.S_FALSE;
    }

    public int Reset()
    {
        _index = 0;
        return Hr.S_OK;
    }

    public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats, _index);
}

/// <summary>IDataObjectAsyncCapability (antes IAsyncOperation): cópia em segundo plano pelo destino.</summary>
[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    [PreserveSig] int SetAsyncMode(int fDoOpAsync);

    [PreserveSig] int GetAsyncMode(out int pfIsOpAsync);

    [PreserveSig] int StartOperation(IntPtr pbcReserved);

    [PreserveSig] int InOperation(out int pfInAsyncOp);

    [PreserveSig] int EndOperation(int hResult, IntPtr pbcReserved, uint dwEffects);
}

internal static class Hr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int DATA_S_SAMEFORMATETC = 0x00040130;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int DV_E_LINDEX = unchecked((int)0x80040068);
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    public const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    public const int STG_E_READFAULT = unchecked((int)0x8003001E);
}
