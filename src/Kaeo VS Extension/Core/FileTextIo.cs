using System.IO;
using System.Text;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Reads/writes text files while preserving the original encoding and BOM (UTF-8 with/without
/// BOM, UTF-16 LE/BE). Files without a recognized BOM are treated as UTF-8 without BOM.
/// </summary>
internal static class FileTextIo
{
    public static (string Text, Encoding Encoding) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);

        Encoding encoding;
        int preamble;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { encoding = new UTF8Encoding(true); preamble = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { encoding = new UnicodeEncoding(false, true); preamble = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { encoding = new UnicodeEncoding(true, true); preamble = 2; }
        else
        { encoding = new UTF8Encoding(false); preamble = 0; }

        return (encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
    }

    public static void Write(string path, string text, Encoding encoding)
    {
        var body = encoding.GetBytes(text);
        var bom = encoding.GetPreamble();
        byte[] output;
        if (bom.Length > 0)
        {
            output = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, output, 0, bom.Length);
            Buffer.BlockCopy(body, 0, output, bom.Length, body.Length);
        }
        else
        {
            output = body;
        }
        File.WriteAllBytes(path, output);
    }
}