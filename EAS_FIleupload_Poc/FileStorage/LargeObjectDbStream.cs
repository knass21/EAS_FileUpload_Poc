using Npgsql;
using NpgsqlTypes;

namespace EAS_FIleupload_Poc.FileStorage;

public class LargeObjectDbStream : Stream
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction _tx;
    private readonly int _fd;
    private bool _disposed = false;

    public LargeObjectDbStream(NpgsqlConnection conn, NpgsqlTransaction tx, int fd)
    {
        _conn = conn;
        _tx = tx;
        _fd = fd;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", _conn, _tx);
        readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
        readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, count);

        var result = readCmd.ExecuteScalar();
        if (result is not byte[] chunk || chunk.Length == 0)
            return 0;

        Array.Copy(chunk, 0, buffer, offset, chunk.Length);
        return chunk.Length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", _conn, _tx);
        readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
        readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, buffer.Length);

        var result = await readCmd.ExecuteScalarAsync(cancellationToken);
        if (result is not byte[] chunk || chunk.Length == 0)
            return 0;

        chunk.CopyTo(buffer[..chunk.Length]);
        return chunk.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", _conn, _tx);
            closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
            closeCmd.ExecuteNonQuery();
            _tx.Commit();
            _conn.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", _conn, _tx);
            closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
            await closeCmd.ExecuteNonQueryAsync();
            await _tx.CommitAsync();
            await _conn.DisposeAsync();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}