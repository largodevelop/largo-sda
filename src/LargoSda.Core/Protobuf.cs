using System.Text;

namespace LargoSda.Core;

/// <summary>Minimal protobuf writer — just enough for Steam WebAPI service requests.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public byte[] ToArray() => _stream.ToArray();

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        Varint((ulong)value.Length);
        _stream.Write(value);
        return this;
    }

    public ProtoWriter UInt64(int field, ulong value)
    {
        Tag(field, 0);
        Varint(value);
        return this;
    }

    /// <summary>int32/enum: negative values are sign-extended to 64 bits, as protobuf requires.</summary>
    public ProtoWriter Int32(int field, int value) => UInt64(field, unchecked((ulong)(long)value));

    public ProtoWriter Bool(int field, bool value) => UInt64(field, value ? 1UL : 0UL);

    public ProtoWriter Fixed64(int field, ulong value)
    {
        Tag(field, 1);
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        if (!BitConverter.IsLittleEndian) buffer.Reverse();
        _stream.Write(buffer);
        return this;
    }

    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }
}

/// <summary>Minimal protobuf reader: field number → raw values (ulong for numbers, byte[] for length-delimited).</summary>
internal sealed class ProtoMessage
{
    private readonly Dictionary<int, List<object>> _fields = [];

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var message = new ProtoMessage();
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            var field = (int)(key >> 3);
            object value;
            switch ((int)(key & 7))
            {
                case 0:
                    value = ReadVarint(data, ref pos);
                    break;
                case 1:
                    value = BitConverter.ToUInt64(data.Slice(pos, 8));
                    pos += 8;
                    break;
                case 2:
                    var length = (int)ReadVarint(data, ref pos);
                    value = data.Slice(pos, length).ToArray();
                    pos += length;
                    break;
                case 5:
                    value = (ulong)BitConverter.ToUInt32(data.Slice(pos, 4));
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type in field {field}.");
            }

            if (!message._fields.TryGetValue(field, out var list))
                message._fields[field] = list = [];
            list.Add(value);
        }
        return message;
    }

    public ulong? UInt64(int field) => _fields.TryGetValue(field, out var v) && v[0] is ulong n ? n : null;

    public byte[]? Bytes(int field) => _fields.TryGetValue(field, out var v) && v[0] is byte[] b ? b : null;

    public string? String(int field) => Bytes(field) is { } b ? Encoding.UTF8.GetString(b) : null;

    public IEnumerable<ProtoMessage> Messages(int field) =>
        _fields.TryGetValue(field, out var v) ? v.OfType<byte[]>().Select(b => Parse(b)) : [];

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new FormatException("Malformed varint.");
    }
}
