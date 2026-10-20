using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Uviewer.Services
{
    // Entry.Extract restarts a solid block for every image. Submit the whole
    // block to 7-Zip once, and publish each file only after its CRC succeeds.
    internal sealed class SevenZipBatchExtractor : IDisposable
    {
        private readonly IntPtr _library;
        private readonly FileStream _input;
        private readonly InputStream _inputAdapter;
        private readonly IInArchive _archive;

        public SevenZipBatchExtractor(string path)
        {
            _library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Libs", "7z.dll"));
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateObject>(NativeLibrary.GetExport(_library, "CreateObject"));
                var format = new Guid("23170F69-40C1-278A-1000-000110070000");
                var iid = typeof(IInArchive).GUID;
                Marshal.ThrowExceptionForHR(create(ref format, ref iid, out _archive));
                _input = File.OpenRead(path);
                _inputAdapter = new InputStream(_input);
                ulong limit = 1 << 20;
                Marshal.ThrowExceptionForHR(_archive.Open(_inputAdapter, ref limit, IntPtr.Zero));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public List<uint[]> GetBlocks(IEnumerable<uint> indices)
        {
            var blocks = new Dictionary<ulong, List<uint>>();
            foreach (uint index in indices)
            {
                PropVariant value = default;
                try
                {
                    _archive.GetProperty(index, 27 /* kpidBlock */, ref value);
                    // Empty files have no block. Keep them independent.
                    ulong block = value.Type == 0 ? (1UL << 32) + index : value.Number;
                    if (!blocks.TryGetValue(block, out var entries)) blocks.Add(block, entries = new());
                    entries.Add(index);
                }
                finally { PropVariantClear(ref value); }
            }
            return blocks.Values.Select(entries => entries.OrderBy(index => index).ToArray()).ToList();
        }

        public void Extract(uint[] indices, Func<uint, string?> getPath, Action<uint, string> completed, CancellationToken token, Action<int>? beforeWrite = null)
        {
            using var callback = new ExtractCallback(getPath, completed, token, beforeWrite);
            int result = _archive.Extract(indices, (uint)indices.Length, 0, callback);
            GC.KeepAlive(callback);
            token.ThrowIfCancellationRequested();
            if (callback.Error != null) throw new IOException("7z image extraction failed.", callback.Error);
            Marshal.ThrowExceptionForHR(result);
        }

        public void Dispose()
        {
            if (_archive != null) Marshal.ReleaseComObject(_archive);
            _input?.Dispose();
            GC.KeepAlive(_inputAdapter);
            if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateObject(ref Guid format, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IInArchive archive);

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public ulong Number;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInArchive
        {
            [PreserveSig] int Open(IInStream stream, ref ulong limit, IntPtr callback);
            void Close();
            uint GetNumberOfItems();
            void GetProperty(uint index, uint property, ref PropVariant value);
            [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] indices,
                uint count, int testMode, [MarshalAs(UnmanagedType.Interface)] IExtractCallback callback);
        }

        [ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInStream
        {
            uint Read([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size);
            void Seek(long offset, uint origin, IntPtr position);
        }

        [ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOutStream
        {
            [PreserveSig] int Write([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size, IntPtr written);
        }

        [ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExtractCallback
        {
            void SetTotal(ulong total);
            void SetCompleted(ref ulong completed);
            [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out IOutStream? stream, int mode);
            void PrepareOperation(int mode);
            void SetOperationResult(int result);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class InputStream : IInStream
        {
            private readonly Stream _stream;
            public InputStream(Stream stream) => _stream = stream;
            public uint Read(byte[] data, uint size) => (uint)_stream.Read(data, 0, checked((int)size));
            public void Seek(long offset, uint origin, IntPtr position)
            {
                long result = _stream.Seek(offset, (SeekOrigin)origin);
                if (position != IntPtr.Zero) Marshal.WriteInt64(position, result);
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class OutputStream : IOutStream
        {
            private readonly Stream _stream;
            private readonly CancellationToken _token;
            private readonly Action<int>? _beforeWrite;
            private readonly Action<Exception> _failed;
            public OutputStream(Stream stream, CancellationToken token, Action<int>? beforeWrite, Action<Exception> failed)
                => (_stream, _token, _beforeWrite, _failed) = (stream, token, beforeWrite, failed);
            public int Write(byte[] data, uint size, IntPtr written)
            {
                if (_token.IsCancellationRequested) return unchecked((int)0x80004004); // E_ABORT
                try
                {
                    _beforeWrite?.Invoke(checked((int)size));
                    _stream.Write(data, 0, checked((int)size));
                    if (written != IntPtr.Zero) Marshal.WriteInt32(written, checked((int)size));
                    return 0;
                }
                catch (Exception ex) { _failed(ex); return Marshal.GetHRForException(ex); }
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class ExtractCallback : IExtractCallback, IDisposable
        {
            private readonly Func<uint, string?> _getPath;
            private readonly Action<uint, string> _completed;
            private readonly CancellationToken _token;
            private readonly Action<int>? _beforeWrite;
            private FileStream? _output;
            private string? _path;
            private uint _index;
            public Exception? Error { get; private set; }

            public ExtractCallback(Func<uint, string?> getPath, Action<uint, string> completed, CancellationToken token, Action<int>? beforeWrite)
                => (_getPath, _completed, _token, _beforeWrite) = (getPath, completed, token, beforeWrite);
            public void SetTotal(ulong total) => _token.ThrowIfCancellationRequested();
            public void SetCompleted(ref ulong completed) => _token.ThrowIfCancellationRequested();
            public void PrepareOperation(int mode) => _token.ThrowIfCancellationRequested();
            public int GetStream(uint index, out IOutStream? stream, int mode)
            {
                stream = null;
                if (_token.IsCancellationRequested) return unchecked((int)0x80004004);
                if (mode != 0) return 0;
                try
                {
                    _index = index;
                    _path = _getPath(index);
                    if (_path == null) return 0;
                    _output = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                    stream = new OutputStream(_output, _token, _beforeWrite, ex => Error = ex);
                    return 0;
                }
                catch (Exception ex)
                {
                    Error = ex;
                    return Marshal.GetHRForException(ex);
                }
            }
            public void SetOperationResult(int result)
            {
                _output?.Dispose();
                _output = null;
                if (_path == null) return;
                if (result != 0 || Error != null)
                {
                    Error ??= new InvalidDataException($"7z entry {_index}: extraction result {result}.");
                    Dispose();
                    return;
                }
                _token.ThrowIfCancellationRequested();
                _completed(_index, _path);
                _path = null;
            }
            public void Dispose()
            {
                _output?.Dispose();
                _output = null;
                if (_path != null)
                {
                    try { File.Delete(_path); } catch { }
                    _path = null;
                }
            }
        }
    }
}
