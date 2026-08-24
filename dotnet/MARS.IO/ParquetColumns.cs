// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace MARS.IO;

/// <summary>
/// Reading columns out of a parquet row group, tolerantly.
/// </summary>
/// <remarks>
/// Shared by the DIA-NN and PRISM readers. Both face the same problem: the writer chose the
/// physical type, and it is not the same choice everywhere. DIA-NN's column types drift between
/// versions, and Skyline writes int32 where a converted CSV would give a string. Binding to one
/// type would break on an upgrade or on a file that took a different route to the same schema,
/// so a numeric column is read as whatever it is and converted.
/// </remarks>
internal static class ParquetColumns
{
    /// <summary>The field with this exact name, or null.</summary>
    public static DataField? Find(DataField[] fields, string name) =>
        fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The field with this name, ignoring spaces and case.
    /// </summary>
    /// <remarks>
    /// Skyline names the same column "Product Mz" in a CSV header and "ProductMz" in parquet.
    /// A report that has been through a conversion tool can arrive with either, and the
    /// distinction carries no meaning, so it is not worth failing over.
    /// </remarks>
    public static DataField? FindLoose(DataField[] fields, string name)
    {
        string wanted = Squash(name);
        return fields.FirstOrDefault(f => string.Equals(Squash(f.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasLoose(DataField[] fields, string name) => FindLoose(fields, name) is not null;

    public static DataField Require(DataField[] fields, string name) =>
        Find(fields, name) ?? throw new InvalidDataException($"Column '{name}' is missing.");

    public static string[] ReadStrings(ParquetRowGroupReader reader, DataField field)
    {
        DataColumn column = reader.ReadColumnAsync(field).GetAwaiter().GetResult();
        Array data = column.Data;
        var result = new string[data.Length];
        for (var i = 0; i < data.Length; i++) result[i] = data.GetValue(i)?.ToString() ?? string.Empty;
        return result;
    }

    /// <summary>Reads a numeric column whatever physical type the writer chose for it.</summary>
    public static double[] ReadDoubles(ParquetRowGroupReader reader, DataField field)
    {
        DataColumn column = reader.ReadColumnAsync(field).GetAwaiter().GetResult();
        Array data = column.Data;
        var result = new double[data.Length];

        for (var i = 0; i < data.Length; i++)
        {
            result[i] = ToDouble(data.GetValue(i));
        }

        return result;
    }

    /// <summary>
    /// Reads a column of counts. Non-integral and missing values become
    /// <paramref name="fallback"/>, since a charge of NaN is not a number a caller can use.
    /// </summary>
    public static int[] ReadInts(ParquetRowGroupReader reader, DataField field, int fallback)
    {
        DataColumn column = reader.ReadColumnAsync(field).GetAwaiter().GetResult();
        Array data = column.Data;
        var result = new int[data.Length];

        for (var i = 0; i < data.Length; i++)
        {
            double value = ToDouble(data.GetValue(i));
            result[i] = double.IsNaN(value) || double.IsInfinity(value) ? fallback : (int)Math.Round(value);
        }

        return result;
    }

    private static double ToDouble(object? value) => value switch
    {
        null => double.NaN,
        double d => d,
        float f => f,
        int n => n,
        long l => l,
        short s => s,
        byte b => b,
        decimal m => (double)m,
        bool flag => flag ? 1 : 0,
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.NaN,
        _ => double.NaN,
    };

    private static string Squash(string name) => name.Replace(" ", string.Empty).Replace("_", string.Empty);
}
