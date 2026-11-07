using System.Text;

namespace CarriageTracker;

// A chat message goes to players as FixedString512Bytes: 509 bytes of UTF-8 at most (2 bytes of length and a
// terminating zero take the rest). A Russian letter is 2 bytes. A longer text (an admin's own welcome line, say)
// is cut here, on a whole character, and not inside a rich text tag such as <color=#FFC83C>: a tag cut in two
// would show up in the chat as text.
public static class ChatText
{
    public const int MaxBytes = 509;

    public static string Fit(string text, out bool cut, int maxBytes = MaxBytes)
    {
        cut = false;
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var utf8 = Encoding.UTF8;
        if (utf8.GetByteCount(text) <= maxBytes) return text;
        cut = true;
        int bytes = 0, i = 0;
        while (i < text.Length)
        {
            int n = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            int b = utf8.GetByteCount(text.ToCharArray(i, n));
            if (bytes + b > maxBytes) break;
            bytes += b;
            i += n;
        }
        string result = text.Substring(0, i);
        int open = result.LastIndexOf('<');
        if (open >= 0 && result.IndexOf('>', open) < 0) result = result.Substring(0, open);
        return result;
    }
}
