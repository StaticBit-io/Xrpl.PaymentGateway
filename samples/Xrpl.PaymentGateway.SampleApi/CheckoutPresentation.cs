using QRCoder;
using Xrpl.AddressCodec;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Turns payment instructions into the two things a payer can act on: one string that carries both the
/// address and the tag, and a picture of it.
/// </summary>
public sealed class CheckoutPresentation
{
    private readonly bool _isTestNetwork;

    /// <param name="isTestNetwork">
    /// Encoded into the X-address itself. A wallet on mainnet refuses an X-address flagged for test and
    /// the other way round, so getting this wrong makes the code unusable — loudly, which is the good
    /// kind of wrong.
    /// </param>
    public CheckoutPresentation(bool isTestNetwork) => _isTestNetwork = isTestNetwork;

    /// <summary>
    /// The address and tag as a single value. This is what a QR code should carry: a scanned classic
    /// address loses the tag, and a payment without the tag reaches the account attached to nobody.
    /// </summary>
    public string ToXAddress(PaymentInstructions instructions) =>
        XrplAddressCodec.ClassicAddressToXAddress(instructions.Address, instructions.DestinationTag, _isTestNetwork);

    /// <summary>
    /// An SVG QR code, drawn dark-on-white whatever the page's theme is. Inverting it for dark mode looks
    /// tidier and scans worse, and the point of the picture is that it scans.
    /// </summary>
    public string ToQrSvg(string payload)
    {
        using QRCodeGenerator generator = new QRCodeGenerator();

        // Medium correction: an X-address is short, and the extra redundancy costs nothing at this size
        // while surviving a fingerprint on a phone screen.
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        SvgQRCode code = new SvgQRCode(data);
        return code.GetGraphic(pixelsPerModule: 8, darkColorHex: "#000000", lightColorHex: "#ffffff", drawQuietZones: true);
    }
}
