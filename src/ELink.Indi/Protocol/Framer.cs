using System.Buffers.Text;
using System.Text;

namespace ELink.Indi.Protocol;

/// <summary>Splits an INDI byte stream into complete top-level XML elements as soon as their last byte
/// arrives (an XML reader would hold a message back until the next one starts). Only tag structure is
/// tracked: '&lt;' and '&gt;' never occur inside a UTF-8 multibyte sequence, attribute values may contain '&gt;'.
/// <para>BLOB contents never pile up as text: the base64 inside a <c>oneBLOB</c> is decoded straight into the
/// frame's bytes as it arrives, and the element is handed on empty with the decoded bytes beside it. A 24 MP
/// frame then costs its own size once, not its base64 text, a string of it and a parsed copy (about 14 times).</para></summary>
public sealed class IndiFramer
{
    private byte[] _buf = new byte[16 * 1024];
    private int _len;
    private int _scan;        // next byte to examine
    private int _start = -1;  // start of the current top-level element
    private int _depth;
    private readonly Queue<(int start, int end, List<byte[]>? blobs)> _ready = new();
    private List<byte[]>? _blobs;          // decoded BLOBs of the element being read, in document order
    private BlobDecoder? _blob;            // set while inside a oneBLOB's text

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_blob is not null)
            {
                int lt = data.IndexOf((byte)'<');
                _blob.Feed(lt < 0 ? data : data[..lt]);
                if (lt < 0) return;
                FinishBlob();
                data = data[lt..];
                continue;
            }
            if (_len + data.Length > _buf.Length)
            {
                int need = Math.Max(_buf.Length * 2, _len + data.Length);
                Array.Resize(ref _buf, need);
            }
            data.CopyTo(_buf.AsSpan(_len));
            _len += data.Length;
            Scan();
            return;
        }
    }

    /// <summary>The next complete element, without its BLOB contents.</summary>
    public bool TryTake(out string? xml) => TryTake(out xml, out _);

    /// <summary>The next complete element and the BLOBs decoded from it (null when it had none).</summary>
    public bool TryTake(out string? xml, out IReadOnlyList<byte[]>? blobs)
    {
        if (_ready.Count == 0)
        {
            xml = null; blobs = null;
            Compact();
            return false;
        }
        var (s, e, b) = _ready.Dequeue();
        xml = Encoding.UTF8.GetString(_buf, s, e - s);
        blobs = b;
        return true;
    }

    private void Compact()
    {
        // Drop everything before the first byte still needed.
        int keep = _start >= 0 ? _start : _scan;
        if (keep > 0)
        {
            Buffer.BlockCopy(_buf, keep, _buf, 0, _len - keep);
            _len -= keep;
            _scan -= keep;
            if (_start >= 0) _start -= keep;
        }
        // a burst of large text (a long property list) is not kept around afterwards
        if (_buf.Length > 1 << 20 && _len < 16 * 1024) Array.Resize(ref _buf, Math.Max(16 * 1024, _len));
    }

    private void FinishBlob()
    {
        (_blobs ??= new()).Add(_blob!.Finish());
        _blob = null;
    }

    private void Scan()
    {
        while (_scan < _len)
        {
            var span = _buf.AsSpan(0, _len);
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
                    if (_depth == 0) { _start = tagStart; _blobs = null; }
                    if (!selfClosing) _depth++;
                }
                if (_depth <= 0 && _start >= 0)
                {
                    _ready.Enqueue((_start, tagEnd, _blobs));
                    _start = -1;
                    _depth = 0;
                    _blobs = null;
                }
                else if (!closing && !selfClosing && _depth == 2 && IsTag(span, tagStart, "oneBLOB"))
                {
                    // the base64 text that follows goes to the decoder, not into the buffer
                    _blob = new BlobDecoder(SizeAttribute(span[tagStart..tagEnd]));
                    var tail = span[tagEnd.._len];
                    int end = tail.IndexOf((byte)'<');
                    _blob.Feed(end < 0 ? tail : tail[..end]);
                    int fed = end < 0 ? tail.Length : end;
                    Buffer.BlockCopy(_buf, tagEnd + fed, _buf, tagEnd, _len - tagEnd - fed);
                    _len -= fed;
                    _scan = tagEnd;
                    if (end < 0) return;               // the rest arrives later, straight into the decoder
                    FinishBlob();
                    continue;
                }
            }
            _scan = tagEnd;
        }
    }

    private static bool IsTag(ReadOnlySpan<byte> s, int lt, string name)
    {
        if (lt + 1 + name.Length >= s.Length) return false;
        for (int k = 0; k < name.Length; k++) if (s[lt + 1 + k] != (byte)name[k]) return false;
        byte after = s[lt + 1 + name.Length];
        return after is (byte)' ' or (byte)'>' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'/';
    }

    /// <summary>The size="..." of a oneBLOB tag (bytes once decoded, before any decompression); -1 when absent.</summary>
    private static long SizeAttribute(ReadOnlySpan<byte> tag)
    {
        ReadOnlySpan<byte> key = " size="u8;
        int k = tag.IndexOf(key);
        if (k < 0) { key = "\tsize="u8; k = tag.IndexOf(key); }
        if (k < 0 || k + key.Length >= tag.Length) return -1;
        var rest = tag[(k + key.Length)..];
        byte q = rest[0];
        if (q != (byte)'"' && q != (byte)'\'') return -1;
        rest = rest[1..];
        int close = rest.IndexOf(q);
        if (close < 0) return -1;
        return Utf8Parser.TryParse(rest[..close], out long v, out _) && v >= 0 ? v : -1;
    }

    /// <summary>Decodes base64 text as it streams in (whitespace and line breaks allowed anywhere) into one array.</summary>
    private sealed class BlobDecoder
    {
        private byte[] _out;
        private int _written;
        private readonly byte[] _clean = new byte[16 * 1024];
        private int _cleanLen;      // filtered text waiting to be decoded (fewer than 4 bytes are carried between feeds)
        private bool _bad;

        public BlobDecoder(long size) => _out = size > 0 && size < int.MaxValue ? GC.AllocateUninitializedArray<byte>((int)size) : new byte[64 * 1024];

        public void Feed(ReadOnlySpan<byte> text)
        {
            if (_bad) return;
            foreach (byte c in text)
            {
                if (c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') continue;
                _clean[_cleanLen++] = c;
                if (_cleanLen == _clean.Length) Decode(final: false);
            }
        }

        private void Decode(bool final)
        {
            if (_bad || _cleanLen == 0) return;
            int whole = final ? _cleanLen : _cleanLen / 4 * 4;
            if (whole == 0) return;
            int need = _written + whole / 4 * 3;
            if (need > _out.Length) Array.Resize(ref _out, Math.Max(need, _out.Length * 2));
            var status = Base64.DecodeFromUtf8(_clean.AsSpan(0, whole), _out.AsSpan(_written), out int used, out int wrote, isFinalBlock: final);
            _written += wrote;
            if (status is System.Buffers.OperationStatus.InvalidData || (final && status != System.Buffers.OperationStatus.Done)) { _bad = true; return; }
            Buffer.BlockCopy(_clean, used, _clean, 0, _cleanLen - used);
            _cleanLen -= used;
        }

        public byte[] Finish()
        {
            Decode(final: true);
            if (_bad) return Array.Empty<byte>();
            if (_written != _out.Length) Array.Resize(ref _out, _written);   // only when size= was absent or wrong
            return _out;
        }
    }
}
