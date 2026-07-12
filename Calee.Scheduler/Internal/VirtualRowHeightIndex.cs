#nullable enable

namespace Calee.Scheduler.Internal;

/// <summary>
/// Prefix-sum index for variable-height virtual rows. The public Timeline surface
/// deliberately keeps its height estimate and overscan internal.
/// </summary>
internal sealed class VirtualRowHeightIndex
{
    private readonly double _estimate;
    private double[] _heights = Array.Empty<double>();
    private double[] _tree = Array.Empty<double>();

    internal VirtualRowHeightIndex(double estimate)
    {
        _estimate = estimate > 0 ? estimate : 1;
    }

    internal int Count => _heights.Length;

    internal double TotalHeight => PrefixSum(Count);

    internal void Reset(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == Count) return;

        _heights = new double[count];
        _tree = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            _heights[i] = _estimate;
            Add(i, _estimate);
        }
    }

    internal bool Update(int index, double height)
    {
        if (index < 0 || index >= Count || height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
        {
            return false;
        }

        if (Math.Abs(_heights[index] - height) < 0.5) return false;

        var delta = height - _heights[index];
        _heights[index] = height;
        Add(index, delta);
        return true;
    }

    internal double PrefixSum(int exclusiveIndex)
    {
        var index = Math.Clamp(exclusiveIndex, 0, Count);
        var sum = 0d;
        while (index > 0)
        {
            sum += _tree[index];
            index -= index & -index;
        }
        return sum;
    }

    /// <summary>Returns the row containing the supplied content-space offset.</summary>
    internal int FindRow(double offset)
    {
        if (Count == 0) return 0;
        if (offset <= 0) return 0;
        if (offset >= TotalHeight) return Count - 1;

        var index = 0;
        var bit = HighestPowerOfTwoAtMost(Count);
        var sum = 0d;
        while (bit != 0)
        {
            var next = index + bit;
            if (next <= Count && sum + _tree[next] <= offset)
            {
                index = next;
                sum += _tree[next];
            }
            bit >>= 1;
        }
        return Math.Min(index, Count - 1);
    }

    internal (int First, int LastExclusive) GetRange(double scrollTop, double viewportHeight, int overscan)
    {
        if (Count == 0) return (0, 0);
        var firstVisible = FindRow(Math.Max(0, scrollTop));
        var lastVisible = FindRow(Math.Max(0, scrollTop) + Math.Max(0, viewportHeight));
        return (
            Math.Max(0, firstVisible - Math.Max(0, overscan)),
            Math.Min(Count, lastVisible + 1 + Math.Max(0, overscan)));
    }

    private void Add(int zeroBasedIndex, double delta)
    {
        for (var index = zeroBasedIndex + 1; index < _tree.Length; index += index & -index)
        {
            _tree[index] += delta;
        }
    }

    private static int HighestPowerOfTwoAtMost(int value)
    {
        var bit = 1;
        while ((bit << 1) <= value) bit <<= 1;
        return bit;
    }
}
