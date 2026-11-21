namespace UnityCSharpFastLSP.Syntax;

public sealed class LineMap
{
    private readonly int[] _lineStarts;

    public LineMap(string source)
    {
        var starts = new List<int> { 0 };

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
    }

    public int LineCount => _lineStarts.Length;

    public (int Line, int Character) GetPosition(int offset)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        int lo = 0;
        int hi = _lineStarts.Length - 1;
        int best = 0;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);

            if (_lineStarts[mid] <= offset)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (best, offset - _lineStarts[best]);
    }

    public int GetOffset(int line, int character)
    {
        if (line <= 0)
        {
            return 0;
        }

        if (line >= _lineStarts.Length)
        {
            return _lineStarts[^1];
        }

        int start = _lineStarts[line];
        int end = _lineStarts[line + 1];
        return Math.Min(start + Math.Max(0, character), end);
    }
}
