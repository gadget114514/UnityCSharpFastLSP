using System.Buffers;
using System.Text;

namespace UnityCSharpFastLSP.Protocol;

public sealed class LspMessageReader
{
    private static readonly byte[] ContentLengthPrefix = "Content-Length: "u8.ToArray();
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    public static bool TryReadMessage(Stream stream, out byte[]? message)
    {
        message = null;
        int contentLength = -1;
        var headerBytes = new List<byte>(128);

        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1)
            {
                return false;
            }

            headerBytes.Add((byte)b);
            int count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' &&
                headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' &&
                headerBytes[count - 1] == '\n')
            {
                string headerStr = Encoding.ASCII.GetString(headerBytes.ToArray());
                string[] lines = headerStr.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        string lenStr = line.Substring("Content-Length:".Length).Trim();
                        if (int.TryParse(lenStr, out int parsed))
                        {
                            contentLength = parsed;
                        }
                    }
                }
                break;
            }
        }

        if (contentLength <= 0)
        {
            return false;
        }

        byte[] buffer = new byte[contentLength];
        int readTotal = 0;
        while (readTotal < contentLength)
        {
            int read = stream.Read(buffer, readTotal, contentLength - readTotal);
            if (read <= 0)
            {
                return false;
            }
            readTotal += read;
        }

        message = buffer;
        return true;
    }
}
