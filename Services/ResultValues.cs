using Oracle.ManagedDataAccess.Client;
using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Numerics;

namespace SqlMcp.Services;

/// <summary>
/// Turns database values into values that serialize to plain JSON. Providers return
/// types that don't: Oracle NUMBERs beyond decimal precision, NaN and infinite doubles,
/// Postgres inet, ranges and geometric types, SQL Server CLR types. One unreadable
/// value must not fail the whole query, so it is replaced by a note saying how to
/// select it instead.
/// </summary>
public static class ResultValues
{
    public static void Prepare(DbDataReader reader)
    {
        // 1/3 is a 40-digit NUMBER; without this, reading it throws InvalidCastException
        // rather than rounding to the 28 digits a decimal holds.
        if (reader is OracleDataReader oracle)
            oracle.SuppressGetDecimalInvalidCastException = true;
    }

    public static object? Read(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        try
        {
            return Normalize(reader.GetValue(ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadFallback(reader, ordinal, ex);
        }
    }

    private static object? ReadFallback(DbDataReader reader, int ordinal, Exception error)
    {
        try
        {
            if (reader is OracleDataReader oracle)
                return oracle.GetOracleValue(ordinal).ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the note.
        }

        string type;
        try
        {
            type = reader.GetDataTypeName(ordinal);
        }
        catch
        {
            type = "this";
        }
        var reason = error.Message.Split('\n')[0].Trim().TrimEnd('.');
        return $"(cannot read {type} value: {reason}. Convert it to a string in the query.)";
    }

    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or char or Guid => value,
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => value,
        double d => double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture),
        float f => float.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture),
        BigInteger b => b.ToString(CultureInfo.InvariantCulture),
        byte[] => value,
        BitArray bits => string.Concat(bits.Cast<bool>().Select(b => b ? '1' : '0')),
        IDictionary dict => dict.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture)!, k => Normalize(dict[k])),
        IEnumerable items => items.Cast<object?>().Select(Normalize).ToList(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
