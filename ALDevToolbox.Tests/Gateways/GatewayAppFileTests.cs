using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ALWorkbench.Gateway.Core.PublishInstall;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Builds <c>.app</c> files the way the AL compiler lays them out: the 40-byte NAVX
/// header, then a ZIP holding <c>NavxManifest.xml</c>, then, for a signed one, the
/// signature. The signature bytes are a stand-in: only Windows can check a real one.
/// </summary>
internal static class AppFiles
{
    public static readonly Guid CoffeeId = Guid.Parse("5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e");

    public static byte[] Build(Guid id, string name, string publisher, string version, bool signed = false, string? manifest = null)
    {
        var package = Zip(("NavxManifest.xml", manifest ?? Manifest(id, name, publisher, version)), ("SymbolReference.json", "{}"));
        return Wrap(package, signed);
    }

    public static string Manifest(Guid id, string name, string publisher, string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
          <App Id="{id}" Name="{name}" Publisher="{publisher}" Brief="" Description="" Version="{version}" CompatibilityId="0.0.0.0" Platform="25.0.0.0" Application="25.0.0.0" Runtime="14.0" Target="OnPrem" ShowMyCode="False" />
          <IdRanges><IdRange MinObjectId="50100" MaxObjectId="50149" /></IdRanges>
          <Dependencies />
        </Package>
        """;

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                using var stream = zip.CreateEntry(entryName).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }

    public static byte[] ZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                using var stream = zip.CreateEntry(entryName).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    public static byte[] Wrap(byte[] package, bool signed = false)
    {
        var header = new byte[AppFile.HeaderLength];
        "NAVX"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), AppFile.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 2);
        Guid.NewGuid().TryWriteBytes(header.AsSpan(12, 16));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28), package.Length);
        "NAVX"u8.CopyTo(header.AsSpan(36));
        var signature = signed ? Encoding.ASCII.GetBytes("stand-in for a PKCS #7 signature block") : [];
        return [.. header, .. package, .. signature];
    }
}

/// <summary>
/// <see cref="AppFile"/>: the gateway reading an <c>.app</c>'s identity from the file
/// itself, so <c>publish-install</c> never trusts what the command says about it (#1375).
/// </summary>
public sealed class GatewayAppFileTests
{
    private static AppFileInfo Read(byte[] bytes) => AppFile.Read(new MemoryStream(bytes));

    [Fact]
    public void Reads_the_id_name_publisher_and_version_from_the_manifest()
    {
        var info = Read(AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0"));

        info.Should().Be(new AppFileInfo(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", new Version(1, 4, 0, 0), HasSignature: false));
    }

    [Fact]
    public void A_short_version_reads_as_four_parts()
    {
        Read(AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4")).Version.Should().Be(new Version(1, 4, 0, 0));
    }

    [Fact]
    public void Bytes_after_the_package_are_read_as_a_signature()
    {
        Read(AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0", signed: true)).HasSignature.Should().BeTrue();
    }

    [Fact]
    public void A_file_without_the_navx_header_is_refused()
    {
        var act = () => Read(AppFiles.Zip(("NavxManifest.xml", "<Package />")));

        act.Should().Throw<AppFileException>().WithMessage("*does not start with the NAVX header*");
    }

    [Fact]
    public void A_header_that_claims_more_than_the_file_holds_is_refused()
    {
        var bytes = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0");
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), bytes.Length);

        var act = () => Read(bytes);

        act.Should().Throw<AppFileException>().WithMessage("*header does not match its length*");
    }

    [Fact]
    public void An_encrypted_package_is_refused_because_its_publisher_cannot_be_read()
    {
        var act = () => Read(AppFiles.Wrap([.. ".NEA"u8.ToArray(), .. new byte[64]]));

        act.Should().Throw<AppFileException>().WithMessage("*encrypted*");
    }

    [Fact]
    public void A_package_without_a_manifest_is_refused()
    {
        var act = () => Read(AppFiles.Wrap(AppFiles.Zip(("SymbolReference.json", "{}"))));

        act.Should().Throw<AppFileException>().WithMessage("*no NavxManifest.xml*");
    }

    [Fact]
    public void A_manifest_with_a_document_type_is_refused()
    {
        var hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE Package [ <!ENTITY big "x"> ]>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest"><App Id="5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e" Name="&big;" Publisher="CRONUS Partner" Version="1.0.0.0" /></Package>
            """;
        var act = () => Read(AppFiles.Build(AppFiles.CoffeeId, "x", "x", "1.0.0.0", manifest: hostile));

        act.Should().Throw<AppFileException>().WithMessage("*could not be read*");
    }

    [Fact]
    public void A_ready2run_wrapper_is_read_through_to_the_app_inside()
    {
        var inner = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0");
        var wrapper = AppFiles.Wrap(AppFiles.ZipBytes(
            ("readytorunappmanifest.json", "{}"u8.ToArray()),
            ("CRONUS Partner_CRONUS Coffee_1.4.0.0.app", inner)));

        Read(wrapper).Name.Should().Be("CRONUS Coffee");
    }
}
