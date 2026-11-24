using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using SmartstoreSattleme.Net.Codecrete.QrCodeGenerator;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// A payment URI as a QR code: an inline SVG image made on the server (no script, no request), by
/// the embedded QR code generator (vendor/QrCodeGenerator).
/// </summary>
public static class QrSvg
{
    /// <summary>Modules of white around the code, as the QR code standard asks for.</summary>
    public const int QuietZone = 4;

    /// <summary>
    /// <paramref name="text"/> as a QR code, labelled <paramref name="label"/> for screen readers;
    /// null when it is too long for one (the page shows it as text then). Dark modules are one path
    /// of horizontal runs, on a white background of its own, so that it scans whatever the page's
    /// colors. It holds nothing but numbers and the label, HTML-encoded.
    /// </summary>
    public static string? Of(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(text);
        QrCode code;
        try
        {
            code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        }
        catch (DataTooLongException)
        {
            return null;
        }

        var size = code.Size + (2 * QuietZone);
        var path = new StringBuilder();
        for (var y = 0; y < code.Size; y++)
        {
            var x = 0;
            while (x < code.Size)
            {
                if (!code.GetModule(x, y))
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < code.Size && code.GetModule(x, y))
                {
                    x++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{start + QuietZone} {y + QuietZone}h{x - start}v1h-{x - start}z");
            }
        }
        var sides = size.ToString(CultureInfo.InvariantCulture);
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 " + sides + " " + sides + "\" role=\"img\" aria-label=\""
            + HtmlEncoder.Default.Encode(label ?? "") + "\" shape-rendering=\"crispEdges\">"
            + "<rect width=\"" + sides + "\" height=\"" + sides + "\" fill=\"#fff\"/>"
            + "<path fill=\"#000\" d=\"" + path + "\"/></svg>";
    }
}
