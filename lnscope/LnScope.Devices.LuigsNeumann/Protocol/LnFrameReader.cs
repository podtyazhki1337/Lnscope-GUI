using System.Text;

namespace LnScope.Devices.LuigsNeumann.Protocol;

/// <summary>Accumulates arbitrary serial byte chunks and reconstructs frames using STX/EOT markers.</summary>
public sealed class LnFrameReader
{
    private readonly StringBuilder _buffer = new();
    private bool _insideFrame;

    public IReadOnlyList<LnFrame> Feed(byte[] data, int count)
    {
        var frames = new List<LnFrame>();

        for (var i = 0; i < count; i++)
        {
            var b = data[i];

            if (b == LnFrame.Stx)
            {
                // Any STX starts a new sequence, allowing recovery after bus noise.
                _insideFrame = true;
                _buffer.Clear();
                continue;
            }

            if (b == LnFrame.Eot)
            {
                if (_insideFrame)
                {
                    TryParse(_buffer.ToString(), frames);
                    _insideFrame = false;
                    _buffer.Clear();
                }

                continue;
            }

            if (_insideFrame)
            {
                _buffer.Append((char)b);
            }
        }

        return frames;
    }

    private static void TryParse(string body, List<LnFrame> frames)
    {
        try
        {
            frames.Add(LnFrame.Parse(body));
        }
        catch (FormatException)
        {
            // Ignore non-protocol data, such as a switching sequence or noise.
        }
    }
}
