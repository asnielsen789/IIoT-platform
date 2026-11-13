using System.Globalization;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>A table's order: which column it is sorted by, and in which direction.</summary>
/// <param name="Key">The column, as the query string names it: <c>?sort=measured</c>.</param>
/// <param name="Descending">Whether the largest, newest or worst value comes first.</param>
public readonly record struct TableSort(string Key, bool Descending)
{
    /// <summary>The query string's word for the direction: <c>?dir=desc</c>.</summary>
    public string Direction => Descending ? "desc" : "asc";
}

/// <summary>What a sortable table's header needs: the order each of its links leads to.</summary>
public interface ITableSorts
{
    /// <summary>
    /// The order a column's header links to: the other direction when the table is already sorted
    /// by that column, otherwise the column's own first direction.
    /// </summary>
    TableSort Next(TableSort current, string key);
}

/// <summary>
/// The columns a table can be sorted by, and how each one orders the rows (#87).
/// </summary>
/// <remarks>
/// <para>
/// The dashboard has no script, so a sort is part of the address: a header is a link to the same
/// page in another order, and the page's own refresh, which reloads the address, keeps it. The
/// query string is untrusted, and nothing in it is used as given: a key that names no column means
/// the default order, and a direction other than <c>asc</c> or <c>desc</c> the column's own.
/// </para>
/// <para>
/// A row without a value for the column, such as a device that reported no firmware, comes last in
/// either direction: it is not the smallest or the largest, only unknown. Rows with equal values
/// keep the table's own order, so the order is the same on every refresh.
/// </para>
/// </remarks>
/// <typeparam name="T">The row.</typeparam>
internal sealed class TableSorts<T> : ITableSorts
{
    private readonly IReadOnlyList<SortColumn<T>> _columns;
    private readonly Comparison<T> _tieBreak;

    /// <param name="tieBreak">The table's own order, for rows the column cannot tell apart.</param>
    /// <param name="columns">The columns; the first is the default order.</param>
    public TableSorts(Comparison<T> tieBreak, params SortColumn<T>[] columns)
    {
        _tieBreak = tieBreak;
        _columns = columns;
    }

    /// <summary>The order without a <c>sort</c> in the query string: the first column, in its first direction.</summary>
    public TableSort Default => new(_columns[0].Key, _columns[0].DescendingFirst);

    /// <summary>The order the query string asks for, or the default when it names no column.</summary>
    /// <param name="key">The <c>sort</c> parameter. Untrusted.</param>
    /// <param name="direction">The <c>dir</c> parameter. Untrusted.</param>
    public TableSort Parse(string? key, string? direction)
    {
        if (Find(key) is not { } column)
        {
            return Default;
        }

        return new TableSort(column.Key, direction switch
        {
            "asc" => false,
            "desc" => true,
            _ => column.DescendingFirst
        });
    }

    public TableSort Next(TableSort current, string key)
    {
        var column = Find(key) ?? throw new ArgumentException($"The table has no column '{key}'.", nameof(key));
        return current.Key == column.Key ? current with { Descending = !current.Descending } : new TableSort(column.Key, column.DescendingFirst);
    }

    /// <summary><paramref name="rows"/> in the order <paramref name="sort"/> names.</summary>
    public IReadOnlyList<T> Order(IEnumerable<T> rows, TableSort sort)
    {
        var column = Find(sort.Key) ?? _columns[0];

        return rows.Order(Comparer<T>.Create((a, b) =>
        {
            var byValue = (column.Value(a), column.Value(b)) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                ({ } x, { } y) => sort.Descending ? y.CompareTo(x) : x.CompareTo(y)
            };

            return byValue != 0 ? byValue : _tieBreak(a, b);
        })).ToList();
    }

    private SortColumn<T>? Find(string? key) => _columns.FirstOrDefault(column => string.Equals(column.Key, key, StringComparison.Ordinal));
}

/// <summary>One column a table can be sorted by.</summary>
/// <param name="Key">Its name in the query string.</param>
/// <param name="DescendingFirst">
/// The direction its header first sorts in: newest, most or worst first where that is what a reader
/// looks for, A to Z otherwise.
/// </param>
/// <param name="Value">
/// The row's value for the column, or <c>null</c> when it has none. Every row's value is of the
/// same type, which compares with itself.
/// </param>
internal sealed record SortColumn<T>(string Key, bool DescendingFirst, Func<T, IComparable?> Value);

/// <summary>
/// Text that sorts as people expect: character by character, except that runs of digits compare as
/// numbers, so firmware <c>2.10.0</c> comes after <c>2.9.1</c>, and <c>tank-10</c> after
/// <c>tank-9</c>. Letters compare by their code, as the IDs and versions are ASCII.
/// </summary>
internal readonly record struct SortText(string Value) : IComparable
{
    /// <summary>The text, or <c>null</c> for a value that is missing or empty.</summary>
    public static SortText? Of(string? value) => string.IsNullOrEmpty(value) ? null : new SortText(value);

    public int CompareTo(object? obj) => obj is SortText other ? Compare(Value, other.Value) : 1;

    public static int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var start = i;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                var otherStart = j;
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                // Compared as digit strings without their leading zeros, so a number of any length fits.
                var number = a.AsSpan(start, i - start).TrimStart('0');
                var otherNumber = b.AsSpan(otherStart, j - otherStart).TrimStart('0');
                var byNumber = number.Length != otherNumber.Length
                    ? number.Length.CompareTo(otherNumber.Length)
                    : number.SequenceCompareTo(otherNumber);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            var byCharacter = a[i].CompareTo(b[j]);
            if (byCharacter != 0)
            {
                return byCharacter;
            }

            i++;
            j++;
        }

        var byRest = (a.Length - i).CompareTo(b.Length - j);

        // Only texts that differ in their leading zeros get this far equal: "007" and "7".
        return byRest != 0 ? byRest : string.CompareOrdinal(a, b);
    }
}

/// <summary>The sortable tables: the overview's devices and a device's message history.</summary>
internal static class DashboardSorts
{
    /// <summary>The overview's columns. By default A to Z by device ID.</summary>
    public static readonly TableSorts<DeviceSummary> Devices = new(
        (a, b) => string.CompareOrdinal(a.DeviceId, b.DeviceId),
        new("device", false, device => SortText.Of(device.DeviceId)),
        new("type", false, device => SortText.Of(device.Type.Label)),

        // Devices of different kinds measure different things, so their values are compared within
        // the kind of their first series: every level before every energy reading, lowest first.
        new("latest", false, device => device.Series.FirstOrDefault() is { Reading.Number: { } number } first
            ? (DashboardKinds.Order(first.Key.Kind), number)
            : null),
        new("quality", true, device => BatchView.Rank(device.WorstLatestQuality)),
        new("measured", true, device => device.Latest?.MeasuredAt),
        new("last-message", true, device => device.LastArrived.Sequence),
        new("messages", true, device => device.MessageCount),
        new("firmware", false, device => SortText.Of(device.LastArrived.Firmware)));

    /// <summary>
    /// A device's history. By default newest first by when the platform received each message, and
    /// messages received at the same moment by when the mock stored them.
    /// </summary>
    public static readonly TableSorts<BatchView> History = new(
        (a, b) => b.Sequence.CompareTo(a.Sequence),
        new("received", true, batch => Instant(batch.ReceivedAt)),
        new("message", false, batch => SortText.Of(batch.MessageId)),
        new("readings", true, batch => batch.Readings.Count),
        new("quality", true, batch => BatchView.Rank(batch.WorstQuality)),
        new("firmware", false, batch => SortText.Of(batch.Firmware)));

    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : null;
}
