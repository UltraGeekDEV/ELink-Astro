using System.Text;

namespace ELink.Indi.Protocol;

/// <summary>Splits an INDI byte stream into complete top-level XML elements as soon as their last byte
/// arrives (an XML reader would hold a message back until the next one starts). Only tag structure is
/// tracked: '&lt;' and '&gt;' never occur inside a UTF-8 multibyte sequence, attribute values may contain '&gt;'.</summary>
public sealed class IndiFramer
{
    private byte[] _buf = new byte[16 * 1024];
    private int _len;
    private int _scan;        // next byte to examine
    private int _start = -1;  // start of the current top-level element
    private int _depth;
    private readonly Queue<(int start, int end)> _ready = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length)
        {
            int need = Math.Max(_buf.Length * 2, _len + data.Length);
            Array.Resize(ref _buf, need);
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Scan();
    }

    public bool TryTake(out string? xml)
    {
        if (_ready.Count == 0)
        {
            xml = null;
            Compact();
            return false;
        }
        var (s, e) = _ready.Dequeue();
        xml = Encoding.UTF8.GetString(_buf, s, e - s);
        return true;
    }

    private void Compact()
    {
        // Drop everything before the first byte still needed.
        int keep = _start >= 0 ? _start : _scan;
        if (keep == 0) return;
        Buffer.BlockCopy(_buf, keep, _buf, 0, _len - keep);
        _len -= keep;
        _scan -= keep;
        if (_start >= 0) _start -= keep;
    }

    private void Scan()
    {
        var span = _buf.AsSpan(0, _len);
        while (_scan < _len)
        {
            int lt = span[_scan..].IndexOf((byte)'<');
            if (lt < 0) { _scan = _len; return; }
            int tagStart = _scan + lt;
            if (tagStart + 1 >= _len) { _scan = tagStart; return; }

            // find the end of this tag, honouring quoted attribute values
            int i = tagStart + 1;
            byte quote = 0;
            for (; i < _len; i++)
            {
                byte c = span[i];
                if (quote != 0) { if (c == quote) quote = 0; }
                else if (c == (byte)'"' || c == (byte)'\'') quote = c;
                else if (c == (byte)'>') break;
            }
            if (i >= _len) { _scan = tagStart; return; } // tag incomplete: wait for more bytes

            int tagEnd = i + 1; // exclusive
            byte first = span[tagStart + 1];
            bool special = first == (byte)'?' || first == (byte)'!';
            bool closing = first == (byte)'/';
            bool selfClosing = !special && !closing && span[i - 1] == (byte)'/';

            if (!special)
            {
                if (closing) _depth--;
                else
                {
                    if (_depth == 0) _start = tagStart;
                    if (!selfClosing) _depth++;
                }
                if (_depth <= 0 && _start >= 0)
                {
                    _ready.Enqueue((_start, tagEnd));
                    _start = -1;
                    _depth = 0;
                }
            }
            _scan = tagEnd;
        }
    }
}
