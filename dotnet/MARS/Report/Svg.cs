// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Minimal SVG construction for the QC report.

using System;
using System.Globalization;
using System.Text;

namespace MARS.Report;

/// <summary>
/// A very small SVG writer: enough for axes, rectangles, polylines and text, and nothing
/// more.
///
/// MARS draws its own charts rather than taking a plotting dependency. Every managed
/// charting library for .NET either wraps a native rasterizer or pulls in a large
/// dependency tree, and the port's stated goal is a binary with as little native code as
/// possible. SVG is text, so producing it costs nothing at runtime, it scales in an email
/// client, and it embeds directly in HTML with no base64 and no separate files.
/// </summary>
public sealed class Svg
{
    private readonly StringBuilder _body = new();

    public Svg(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    private static string F(double value) =>
        // Two decimals is finer than a pixel at these sizes, and keeps the markup small:
        // a density panel emits thousands of rectangles.
        value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    public Svg Rect(double x, double y, double width, double height, string fill, string? extra = null)
    {
        if (width <= 0 || height <= 0) return this;
        _body.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y))
             .Append("\" width=\"").Append(F(width)).Append("\" height=\"").Append(F(height))
             .Append("\" fill=\"").Append(fill).Append('"');
        if (extra is not null) _body.Append(' ').Append(extra);
        _body.Append("/>");
        return this;
    }

    public Svg Line(double x1, double y1, double x2, double y2, string stroke, double width = 1, string? dash = null)
    {
        _body.Append("<line x1=\"").Append(F(x1)).Append("\" y1=\"").Append(F(y1))
             .Append("\" x2=\"").Append(F(x2)).Append("\" y2=\"").Append(F(y2))
             .Append("\" stroke=\"").Append(stroke)
             .Append("\" stroke-width=\"").Append(F(width)).Append('"');
        if (dash is not null) _body.Append(" stroke-dasharray=\"").Append(dash).Append('"');
        _body.Append("/>");
        return this;
    }

    public Svg Polyline(ReadOnlySpan<(double X, double Y)> points, string stroke, double width = 1.5)
    {
        if (points.Length < 2) return this;
        _body.Append("<polyline fill=\"none\" stroke=\"").Append(stroke)
             .Append("\" stroke-width=\"").Append(F(width)).Append("\" points=\"");
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0) _body.Append(' ');
            _body.Append(F(points[i].X)).Append(',').Append(F(points[i].Y));
        }

        _body.Append("\"/>");
        return this;
    }

    public Svg Text(
        double x, double y, string content, string anchor = "start",
        double size = 11, string fill = "var(--fg)", bool bold = false, double rotate = 0)
    {
        _body.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(F(y))
             .Append("\" text-anchor=\"").Append(anchor)
             .Append("\" font-size=\"").Append(F(size))
             .Append("\" fill=\"").Append(fill).Append('"');
        if (bold) _body.Append(" font-weight=\"600\"");
        if (rotate != 0)
            _body.Append(" transform=\"rotate(").Append(F(rotate)).Append(' ').Append(F(x)).Append(' ').Append(F(y)).Append(")\"");
        _body.Append('>').Append(Escape(content)).Append("</text>");
        return this;
    }

    /// <summary>
    /// Places a raster image in the plot area. Used for the density layers, which are far
    /// smaller as a compressed image than as one rectangle per cell.
    /// </summary>
    public Svg Image(double x, double y, double width, double height, string dataUri)
    {
        _body.Append("<image x=\"").Append(F(x)).Append("\" y=\"").Append(F(y))
             .Append("\" width=\"").Append(F(width)).Append("\" height=\"").Append(F(height))
             .Append("\" preserveAspectRatio=\"none\" image-rendering=\"pixelated\" href=\"")
             .Append(dataUri).Append("\"/>");
        return this;
    }

    /// <summary>Serializes to an inline SVG element, sized to scale with its container.</summary>
    public override string ToString() =>
        $"<svg viewBox=\"0 0 {Width} {Height}\" width=\"100%\" height=\"auto\" " +
        "xmlns=\"http://www.w3.org/2000/svg\" font-family=\"system-ui, -apple-system, Segoe UI, sans-serif\">" +
        _body + "</svg>";
}
