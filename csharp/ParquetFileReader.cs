using System;
using System.IO;
using System.Runtime.InteropServices;
using ParquetSharp.IO;

namespace ParquetSharp
{
    /// <summary>
    /// Opens and reads Parquet files.
    /// </summary>
    public sealed class ParquetFileReader : IDisposable
    {
#pragma warning disable RS0026
#pragma warning disable RS0027

        /// <summary>
        /// Create a new ParquetFileReader for reading from a file at the specified path
        /// </summary>
        /// <param name="path">Path to the Parquet file</param>
        public ParquetFileReader(string path)
            : this(path, null)
        {
        }

        /// <summary>
        /// Create a new ParquetFileReader for reading from a specified <see cref="RandomAccessFile"/>
        /// </summary>
        /// <param name="randomAccessFile">The file to read</param>
        public ParquetFileReader(RandomAccessFile randomAccessFile)
            : this(randomAccessFile, null)
        {
        }

        /// <summary>
        /// Create a new ParquetFileReader for reading from a .NET stream
        /// </summary>
        /// <param name="stream">The stream to read</param>
        /// <param name="leaveOpen">Whether to keep the stream open after the reader is closed</param>
        public ParquetFileReader(Stream stream, bool leaveOpen = false)
            : this(stream, null, leaveOpen)
        {
        }

        /// <summary>
        /// Create a new ParquetFileReader for reading from a file at the specified path
        /// </summary>
        /// <param name="path">Path to the Parquet file</param>
        /// <param name="readerProperties">A <see cref="ReaderProperties"/> object that configures the reader</param>
        /// <exception cref="ArgumentNullException">Thrown if the path is null</exception>
        public ParquetFileReader(string path, ReaderProperties? readerProperties)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            path = LongPath.EnsureLongPathSafe(path);

            using var defaultProperties = readerProperties == null ? ReaderProperties.GetDefaultReaderProperties() : null;
            var properties = readerProperties ?? defaultProperties!;

            ExceptionInfo.Check(ParquetFileReader_OpenFile(path, properties.Handle.IntPtr, out var reader));
            _handle = new ParquetHandle(reader, ParquetFileReader_Free);

            GC.KeepAlive(readerProperties);
        }

        /// <summary>
        /// Create a new ParquetFileReader for reading from a specified <see cref="RandomAccessFile"/>
        /// </summary>
        /// <param name="randomAccessFile">The file to read</param>
        /// <param name="readerProperties">The <see cref="ReaderProperties"/> to use</param>  
        /// <exception cref="ArgumentNullException">Thrown if the file or its handle are null</exception>
        public ParquetFileReader(RandomAccessFile randomAccessFile, ReaderProperties? readerProperties)
        {
            if (randomAccessFile == null) throw new ArgumentNullException(nameof(randomAccessFile));
            if (randomAccessFile.Handle == null) throw new ArgumentNullException(nameof(randomAccessFile.Handle));

            using var defaultProperties = readerProperties == null ? ReaderProperties.GetDefaultReaderProperties() : null;
            var properties = readerProperties ?? defaultProperties!;

            void Free(IntPtr ptr)
            {
                ParquetFileReader_Free(ptr);
                // Capture and keep a handle to the managed file instance so that if we free the last reference to the
                // C++ random access file and trigger a file close, we can ensure the file hasn't been garbage collected.
                // Note that this doesn't protect against the case where the C# side handle is disposed or finalized before
                // the C++ side has finished with it.
                GC.KeepAlive(randomAccessFile);
            }

            _handle = new ParquetHandle(ExceptionInfo.Return<IntPtr, IntPtr>(randomAccessFile.Handle, properties.Handle.IntPtr, ParquetFileReader_Open), Free);
            _randomAccessFile = randomAccessFile;

            GC.KeepAlive(readerProperties);
        }

        /// <summary>
        /// Create a new ParquetFileReader for reading from a .NET stream
        /// </summary>
        /// <param name="stream">The stream to read</param>
        /// <param name="readerProperties">Configures the reader properties</param>
        /// <param name="leaveOpen">Whether to keep the stream open after the reader is closed</param>
        public ParquetFileReader(Stream stream, ReaderProperties? readerProperties, bool leaveOpen = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using var defaultProperties = readerProperties == null ? ReaderProperties.GetDefaultReaderProperties() : null;
            var properties = readerProperties ?? defaultProperties!;
            var randomAccessFile = new ManagedRandomAccessFile(stream, leaveOpen);

            void Free(IntPtr ptr)
            {
                ParquetFileReader_Free(ptr);
                // Capture and keep a handle to the managed file instance so that if we free the last reference to the
                // C++ random access file and trigger a file close, we can ensure the file hasn't been garbage collected.
                // Note that this doesn't protect against the case where the C# side handle is disposed or finalized before
                // the C++ side has finished with it.
                GC.KeepAlive(randomAccessFile);
            }

            _handle = new ParquetHandle(ExceptionInfo.Return<IntPtr, IntPtr>(randomAccessFile.Handle!, properties.Handle.IntPtr, ParquetFileReader_Open), Free);
            _randomAccessFile = randomAccessFile;
            _ownedFile = true;

            GC.KeepAlive(readerProperties);
        }

#pragma warning restore RS0026
#pragma warning restore RS0027

        internal ParquetFileReader(INativeHandle handle)
        {
            _handle = handle;
        }

        public void Dispose()
        {
            _fileMetaData?.Dispose();
            _handle.Dispose();
            if (_ownedFile)
            {
                _randomAccessFile?.Dispose();
            }
        }

        public void Close()
        {
            ExceptionInfo.Check(ParquetFileReader_Close(_handle.IntPtr));
            GC.KeepAlive(_handle);
        }

        /// <summary>
        /// The <see cref="ParquetSharp.LogicalTypeFactory"/> for handling custom types.
        /// </summary>
        public LogicalTypeFactory LogicalTypeFactory { get; set; } = LogicalTypeFactory.Default; // TODO make this init only at some point when C# 9 is more widespread

        /// <summary>
        /// The <see cref="ParquetSharp.LogicalReadConverterFactory"/> for reading custom types.
        /// </summary>
        public LogicalReadConverterFactory LogicalReadConverterFactory { get; set; } = LogicalReadConverterFactory.Default; // TODO make this init only at some point when C# 9 is more widespread

        /// <summary>
        /// Metadata associated with the Parquet file.
        /// </summary>
        public FileMetaData FileMetaData => _fileMetaData ??= new FileMetaData(ExceptionInfo.Return<IntPtr>(_handle, ParquetFileReader_MetaData));

        /// <summary>
        /// Get a <see cref="RowGroupReader"/> for the specified row group index.
        /// </summary>
        /// <param name="i">The row group index</param>
        /// <returns>A <see cref="RowGroupReader"/> for the specified row group index</returns>
        public RowGroupReader RowGroup(int i)
        {
            return new(ExceptionInfo.Return<int, IntPtr>(_handle, i, ParquetFileReader_RowGroup), this);
        }

        [DllImport(ParquetDll.Name)]
        private static extern IntPtr ParquetFileReader_OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr readerProperties, out IntPtr reader);

        [DllImport(ParquetDll.Name)]
        private static extern IntPtr ParquetFileReader_Open(IntPtr readableFileInterface, IntPtr readerProperties, out IntPtr reader);

        [DllImport(ParquetDll.Name)]
        private static extern void ParquetFileReader_Free(IntPtr reader);

        [DllImport(ParquetDll.Name)]
        private static extern IntPtr ParquetFileReader_Close(IntPtr reader);

        [DllImport(ParquetDll.Name)]
        private static extern IntPtr ParquetFileReader_MetaData(IntPtr reader, out IntPtr fileMetaData);

        [DllImport(ParquetDll.Name)]
        private static extern IntPtr ParquetFileReader_RowGroup(IntPtr reader, int i, out IntPtr rowGroupReader);

        private readonly INativeHandle _handle;
        private FileMetaData? _fileMetaData;
        private readonly RandomAccessFile? _randomAccessFile; // Keep a handle to the input file to prevent GC
        private readonly bool _ownedFile; // Whether this reader created the RandomAccessFile
    }
}
