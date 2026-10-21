using System.Buffers;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// A growable character buffer backed by <see cref="ArrayPool{T}"/> that avoids
/// Large Object Heap fragmentation when accumulating large streaming responses.
/// Unlike <see cref="System.Text.StringBuilder"/>, intermediate arrays are returned
/// to the pool on growth rather than being abandoned on the LOH.
/// </summary>
internal sealed class PooledCharBuffer : IDisposable
{
    private char[] _buffer;
    private int _length;
    private bool _disposed;

    private const int InitialCapacity = 4096;

    public PooledCharBuffer(int initialCapacity = InitialCapacity)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(initialCapacity, 256));
    }

    /// <summary>Number of characters currently stored.</summary>
    public int Length => _length;

    /// <summary>Appends a string to the buffer.</summary>
    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        ObjectDisposedException.ThrowIf(_disposed, this);

        long required = (long)_length + value.Length;
        if (required > _buffer.Length)
            Grow(required);

        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Appends a single character to the buffer.</summary>
    public void Append(char value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_length >= _buffer.Length)
            Grow((long)_length + 1);

        _buffer[_length++] = value;
    }

    /// <summary>Returns the accumulated content as a string.</summary>
    public override string ToString()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _length == 0 ? string.Empty : new string(_buffer, 0, _length);
    }

    private void Grow(long requiredCapacity)
    {
        if (requiredCapacity > Array.MaxLength)
            throw new InvalidOperationException(
                $"Accumulated content would exceed the maximum buffer size of {Array.MaxLength} characters.");

        // Both operands in 64-bit so neither the requirement nor the doubling can overflow;
        // the doubling is capped so Rent() stays within Array.MaxLength.
        long doubled = (long)_buffer.Length * 2;
        int newCapacity = (int)Math.Max(requiredCapacity, Math.Min(doubled, Array.MaxLength));
        char[] newBuffer = ArrayPool<char>.Shared.Rent(newCapacity);

        _buffer.AsSpan(0, _length).CopyTo(newBuffer);

        Array.Clear(_buffer, 0, _length);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Array.Clear(_buffer, 0, _length);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = [];
    }
}
