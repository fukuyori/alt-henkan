using System.Text;

namespace AltHenkan;

// Used only by QueuedDiagnosticWriter's worker, never by an input callback.
internal sealed class RotatingDiagnosticWriter : TextWriter
{
    public const int DefaultMaximumBytes = 1024 * 1024;
    public const int DefaultArchiveCount = 4;
    private readonly string _path;
    private readonly int _maximumBytes;
    private readonly int _archiveCount;
    private FileStream? _stream;
    public override Encoding Encoding => new UTF8Encoding(false);

    public RotatingDiagnosticWriter(string path,
        int maximumBytes = DefaultMaximumBytes, int archiveCount = DefaultArchiveCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(archiveCount, 1);
        _path = System.IO.Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _archiveCount = archiveCount;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        Open();
        if (_stream!.Length >= _maximumBytes) Rotate();
    }

    private void Open() => _stream = new FileStream(
        _path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        // Move newest last so each archive is retained until its successor exists.
        for (var index = _archiveCount; index >= 1; index--)
        {
            var source = index == 1 ? _path : $"{_path}.{index - 1}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index}", overwrite: true);
        }
        Open();
    }

    public override Task WriteLineAsync(string? value)
    {
        var text = value ?? string.Empty;
        var newline = Environment.NewLine;
        if (Encoding.GetByteCount(text) + Encoding.GetByteCount(newline) > _maximumBytes)
        {
            const string suffix = " [truncated]";
            // At most three UTF-8 bytes per UTF-16 code unit, including replacement characters.
            var characters = (_maximumBytes - Encoding.GetByteCount(newline + suffix)) / 3;
            text = text[..Math.Min(text.Length, characters)] + suffix;
        }
        var bytes = Encoding.GetBytes(text + newline);
        if (_stream!.Length + bytes.Length > _maximumBytes) Rotate();
        _stream!.Write(bytes);
        _stream.Flush();
        return Task.CompletedTask;
    }

    public override Task FlushAsync()
    {
        _stream?.Flush();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream?.Dispose();
        base.Dispose(disposing);
    }
}
