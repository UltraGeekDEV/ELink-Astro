using System.Globalization;
using System.Text;

namespace ELink.Imaging;

/// <summary>Edits the primary header of a FITS file without touching the data: add or replace cards, keep the
/// 2880-byte block structure. Used to stamp frames with what the capture software knows (object, pointing, plan).</summary>
public static class FitsHeader
{
    public static string Card(string key, string value, string? comment = null)
    {
        string head = key.PadRight(8)[..8] + "= ";
        string body = value.PadLeft(20);
        string text = head + body + (comment is null ? "" : " / " + comment);
        return (text.Length > 80 ? text[..80] : text).PadRight(80);
    }

    public static string StringCard(string key, string value, string? comment = null)
    {
        string escaped = value.Replace("'", "''");
        if (escaped.Length > 68) escaped = escaped[..68];
        string quoted = "'" + escaped.PadRight(8) + "'";
        string text = key.PadRight(8)[..8] + "= " + quoted + (comment is null ? "" : " / " + comment);
        return (text.Length > 80 ? text[..80] : text).PadRight(80);
    }

    public static string NumberCard(string key, double value, string? comment = null) =>
        Card(key, value.ToString("0.#########", CultureInfo.InvariantCulture), comment);

    /// <summary>Returns a copy of the file where each (key, card-text) replaces the card of that key, or is added before END.</summary>
    public static byte[] Set(ReadOnlySpan<byte> fits, IReadOnlyDictionary<string, string> cards)
    {
        var header = NewHeader(fits, cards, out int headerEnd);
        var result = new byte[header.Length + (fits.Length - headerEnd)];
        header.CopyTo(result, 0);
        fits[headerEnd..].CopyTo(result.AsSpan(header.Length));
        return result;
    }

    /// <summary>Only the new header (padded to whole blocks): write it, then the original from <paramref name="dataStart"/> on,
    /// and a 48 MB frame is never copied.</summary>
    public static byte[] NewHeader(ReadOnlySpan<byte> fits, IReadOnlyDictionary<string, string> cards, out int dataStart)
    {
        int headerEnd = FindHeaderEnd(fits, out int endCardOffset);
        dataStart = headerEnd;
        var existing = new List<string>();
        for (int off = 0; off < endCardOffset; off += 80) existing.Add(Encoding.ASCII.GetString(fits.Slice(off, 80)));

        foreach (var (key, card) in cards)
        {
            string k = key.PadRight(8)[..8];
            int i = existing.FindIndex(c => c.StartsWith(k, StringComparison.Ordinal) && c.Length > 8 && c[8] == '=');
            if (i >= 0) existing[i] = card; else existing.Add(card);
        }
        existing.Add("END".PadRight(80));
        string header = string.Concat(existing);
        int padded = (header.Length + 2879) / 2880 * 2880;
        return Encoding.ASCII.GetBytes(header.PadRight(padded));
    }

    /// <returns>offset of the first data byte (a multiple of 2880); endCard = offset of the END card</returns>
    private static int FindHeaderEnd(ReadOnlySpan<byte> fits, out int endCard)
    {
        for (int block = 0; block + 2880 <= fits.Length; block += 2880)
            for (int c = 0; c < 36; c++)
            {
                var card = fits.Slice(block + c * 80, 80);
                if (card[0] == 'E' && card[1] == 'N' && card[2] == 'D' && (card[3] == ' ' || card[3] == 0))
                {
                    endCard = block + c * 80;
                    return block + 2880;
                }
            }
        throw new FormatException("FITS header is not terminated");
    }
}
