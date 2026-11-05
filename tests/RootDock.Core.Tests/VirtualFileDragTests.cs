using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RootDock.Core.Shell;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace RootDock.Core.Tests;

/// <summary>Arrastar do servidor para o Explorer (arquivos virtuais).</summary>
public class VirtualFileDragTests
{
    private static readonly short CfContents = unchecked((short)(ushort)RegisterClipboardFormat("FileContents"));
    private static readonly short CfDescriptor = unchecked((short)(ushort)RegisterClipboardFormat("FileGroupDescriptorW"));

    [Fact]
    public void Descriptor_has_names_folders_and_64_bit_sizes()
    {
        var files = new[]
        {
            new VirtualFile("logs", true, 0, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            new VirtualFile("logs\\backup ção.tar", false, 5L << 30, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
        };

        var data = VirtualFileDataObject.Descriptor(files);

        Assert.Equal(4 + 2 * 592, data.Length);
        Assert.Equal(2, BitConverter.ToInt32(data, 0));
        var folder = data.AsSpan(4, 592);
        var file = data.AsSpan(4 + 592, 592);
        Assert.Equal(0x10, BitConverter.ToInt32(folder[36..40])); // FILE_ATTRIBUTE_DIRECTORY
        Assert.Equal(0x80, BitConverter.ToInt32(file[36..40]));
        Assert.Equal(1u, BitConverter.ToUInt32(file[64..68])); // 5 GB = 1 × 4 GB + 1 GB
        Assert.Equal(5L << 30, ((long)BitConverter.ToUInt32(file[64..68]) << 32) | BitConverter.ToUInt32(file[68..72]));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc(), BitConverter.ToInt64(file[56..64]));
        Assert.Equal("logs\\backup ção.tar", Name(file));
        var flags = BitConverter.ToInt32(file[0..4]);
        Assert.NotEqual(0, flags & 0x40);   // FD_FILESIZE
        Assert.NotEqual(0, flags & 0x4000); // FD_PROGRESSUI
        Assert.NotEqual(0, flags & unchecked((int)0x80000000)); // FD_UNICODE
    }

    [Fact]
    public async Task Content_is_streamed_in_full_reads_and_reported_at_the_end()
    {
        var finished = new TaskCompletionSource<VirtualFileDropResult>();
        var data = new VirtualFileDataObject(_ => Task.FromResult(new VirtualFileList(
        [
            new VirtualFile("pasta", true, 0, DateTime.Now),
            Generated("pasta\\dados.bin", 300_000, chunk: 7_000),
        ])));
        data.Finished += r => finished.TrySetResult(r);

        var stream = OpenContents(data, 1);
        var received = new List<byte>();
        var buffer = new byte[64 << 10];
        var counter = Marshal.AllocCoTaskMem(4);
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, counter);
                var n = Marshal.ReadInt32(counter);
                if (n == 0)
                {
                    break;
                }

                // Leitura curta só no fim: o Explorer trata leitura curta como fim do arquivo.
                Assert.True(n == buffer.Length || received.Count + n == 300_000, $"leitura curta de {n} bytes no meio");
                received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(counter);
        }

        Assert.Equal(300_000, received.Count);
        Assert.True(received.Select((b, i) => b == (byte)(i % 251)).All(x => x));

        data.DragEnded(dropped: true);
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Dropped);
        Assert.Equal(1, result.FilesCompleted);
        Assert.Equal(300_000, result.Bytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Async_copy_keeps_running_after_the_drag_until_EndOperation()
    {
        var finished = new TaskCompletionSource<VirtualFileDropResult>();
        var data = new VirtualFileDataObject(_ => Task.FromResult(new VirtualFileList([Generated("a.bin", 10)])));
        data.Finished += r => finished.TrySetResult(r);
        data.SetAsyncMode(1);
        data.GetAsyncMode(out var isAsync);
        Assert.Equal(1, isAsync);

        data.StartOperation(IntPtr.Zero); // o Explorer começa a copiar no Drop
        data.DragEnded(dropped: true);   // DoDragDrop retornou
        Assert.False(finished.Task.IsCompleted);

        data.EndOperation(0, IntPtr.Zero, 1);
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Dropped);
    }

    [Fact]
    public async Task Server_error_fails_the_read_and_is_reported()
    {
        var finished = new TaskCompletionSource<VirtualFileDropResult>();
        var data = new VirtualFileDataObject(_ => Task.FromResult(new VirtualFileList(
        [
            new VirtualFile("negado.txt", false, 100, DateTime.Now, (_, _) => throw new IOException("Permission denied")),
        ])));
        data.Finished += r => finished.TrySetResult(r);

        var stream = OpenContents(data, 0);
        var ex = Assert.Throws<COMException>(() => stream.Read(new byte[100], 100, IntPtr.Zero));
        Assert.Contains("Permission denied", ex.Message);

        data.DragEnded(dropped: true);
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("Permission denied", result.Error);
    }

    [Fact]
    public void Data_object_is_not_agile_so_explorer_calls_stay_off_the_ui_thread()
    {
        var data = new VirtualFileDataObject(_ => Task.FromResult(new VirtualFileList([])));
        var unknown = Marshal.GetComInterfaceForObject(data, typeof(ComDataObject));
        try
        {
            var iidMarshal = new Guid("00000003-0000-0000-C000-000000000046");
            Assert.NotEqual(0, Marshal.QueryInterface(unknown, in iidMarshal, out _));
            var iidAsync = typeof(IDataObjectAsyncCapability).GUID;
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iidAsync, out var async));
            Marshal.Release(async);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void Offers_descriptor_contents_and_copy_effect()
    {
        ComDataObject data = new VirtualFileDataObject(_ => Task.FromResult(new VirtualFileList([Generated("a.bin", 1)])));
        var descriptor = new FORMATETC { cfFormat = CfDescriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var contents = new FORMATETC { cfFormat = CfContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = 0, tymed = TYMED.TYMED_ISTREAM };
        var text = new FORMATETC { cfFormat = 13, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };

        Assert.Equal(0, data.QueryGetData(ref descriptor));
        Assert.Equal(0, data.QueryGetData(ref contents));
        Assert.NotEqual(0, data.QueryGetData(ref text)); // sem texto: soltar no Explorer não vira "Recorte"
    }

    private static IStream OpenContents(ComDataObject data, int index)
    {
        var format = new FORMATETC { cfFormat = CfContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = TYMED.TYMED_ISTREAM };
        data.GetData(ref format, out var medium);
        Assert.Equal(TYMED.TYMED_ISTREAM, medium.tymed);
        var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        Marshal.Release(medium.unionmember);
        return stream;
    }

    private static VirtualFile Generated(string name, int size, int chunk = 4096) => new(name, false, size, DateTime.Now, async (output, ct) =>
    {
        var buffer = new byte[chunk];
        var done = 0;
        while (done < size)
        {
            var n = Math.Min(chunk, size - done);
            for (var i = 0; i < n; i++)
            {
                buffer[i] = (byte)((done + i) % 251);
            }

            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
        }
    });

    private static string Name(ReadOnlySpan<byte> descriptor)
    {
        var chars = MemoryMarshal.Cast<byte, char>(descriptor[72..]);
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);
}
