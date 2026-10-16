using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>What an <c>.app</c> says about itself, read from its own manifest.</summary>
/// <param name="Id">The app id.</param>
/// <param name="Name">The app name.</param>
/// <param name="Publisher">The publisher.</param>
/// <param name="Version">The version, always four parts.</param>
/// <param name="HasSignature">
/// True when the file carries bytes after its package, which is where a signature is
/// written when an <c>.app</c> is signed. Only says a signature is there, not that it
/// verifies: that is Windows' job on the gateway's computer (see <see cref="AppFile"/>).
/// </param>
/// <param name="PackageId">
/// The package id from the NAVX header: new for every build, so two builds with the same
/// version differ by it. Business Central records it for a published app
/// (<c>PackageId</c>), which is how the gateway tells whether the version already
/// published is this very file. For a Ready2Run wrapper, the id of the app inside.
/// </param>
public sealed record AppFileInfo(Guid Id, string Name, string Publisher, Version Version, bool HasSignature, Guid PackageId);

/// <summary>A file that is not an <c>.app</c> the gateway can read, with a sentence saying why.</summary>
public sealed class AppFileException(string message) : Exception(message);

/// <summary>
/// Reads an <c>.app</c>'s identity from the file itself, so <c>publish-install</c> never
/// trusts what the command says about it (#1375; <c>.design/on-prem-gateway.md</c>,
/// "Each .app against the declared list").
///
/// <para><b>The layout.</b> An <c>.app</c> starts with a 40-byte header: the four bytes
/// <c>NAVX</c>, the header's length (32-bit, little-endian), its format version, the
/// package id (16 bytes), the package's length (64-bit) and <c>NAVX</c> again. The package,
/// a ZIP holding <c>NavxManifest.xml</c>, follows the header and is exactly that long. A
/// signed <c>.app</c> carries its signature after the package; an unsigned one ends
/// there. AL Workbench's own reader (<c>AppPackageReader</c>) reads the same header; the
/// gateway cannot reference the app, so this is the small part it needs.</para>
///
/// <para><b>What it refuses.</b> A file without the header, a package longer than the
/// file, an encrypted package (AppSource's <c>NAVX.NEA</c>: its manifest cannot be read,
/// so its publisher cannot be checked), a package without a manifest, and one with more
/// than one (names compared ignoring case). A Ready2Run
/// wrapper (one nested <c>.app</c> beside <c>readytorunappmanifest.json</c>) is read
/// through to the app inside it.</para>
/// </summary>
public static class AppFile
{
    /// <summary>The header every <c>.app</c> starts with.</summary>
    public const int HeaderLength = 40;

    /// <summary>The manifest is a few kilobytes; anything far larger is not one.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    /// <summary>A Ready2Run wrapper's nested <c>.app</c> is read into memory; at most this.</summary>
    public const long MaxNestedAppBytes = 256L * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "NAVX"u8;

    private static ReadOnlySpan<byte> Encrypted => ".NEA"u8;

    public static AppFileInfo Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream);
        }
        catch (IOException ex)
        {
            throw new AppFileException($"The file could not be read: {ex.Message}");
        }
    }

    /// <summary>Reads a seekable stream positioned anywhere; reads from its start.</summary>
    public static AppFileInfo Read(Stream stream) => Read(stream, nested: false);

    private static AppFileInfo Read(Stream stream, bool nested)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength + Encrypted.Length];
        stream.Position = 0;
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < HeaderLength || !header.AsSpan(0, 4).SequenceEqual(Magic) || !header.AsSpan(36, 4).SequenceEqual(Magic))
        {
            throw new AppFileException("It is not a Business Central .app file: it does not start with the NAVX header.");
        }
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var contentLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(28, 8));
        var packageId = new Guid(header.AsSpan(12, 16));
        // Compared as "longer than what is left", never as a sum: a length near
        // long.MaxValue would overflow headerLength + contentLength into a negative
        // number that passes.
        if (headerLength < HeaderLength || headerLength > 4096 || contentLength <= 0 || contentLength > stream.Length - headerLength)
        {
            throw new AppFileException("It is not a Business Central .app file the gateway can read: its header does not match its length. The file may be damaged.");
        }
        if (read >= header.Length && headerLength == HeaderLength && header.AsSpan(HeaderLength, Encrypted.Length).SequenceEqual(Encrypted))
        {
            throw new AppFileException("It is an encrypted .app (as AppSource apps are), so the gateway cannot read who published it.");
        }
        var hasSignature = stream.Length > headerLength + contentLength;

        AppFileInfo? info;
        try
        {
            using var package = new WindowStream(stream, headerLength, contentLength);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            info = ReadManifest(archive, nested);
        }
        catch (InvalidDataException ex)
        {
            throw new AppFileException($"Its package could not be opened: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw new AppFileException("It is not a Business Central .app file the gateway can read: its header does not match its length. The file may be damaged.");
        }
        // A Ready2Run wrapper's info comes back from the app inside, with that app's package id.
        return info with { HasSignature = hasSignature, PackageId = info.PackageId == Guid.Empty ? packageId : info.PackageId };
    }

    private static AppFileInfo ReadManifest(ZipArchive archive, bool nested)
    {
        var manifest = Entry(archive, "NavxManifest.xml");
        if (manifest is null)
        {
            if (!nested && Entry(archive, "readytorunappmanifest.json") is not null)
            {
                var inner = archive.Entries.Where(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)).ToList();
                if (inner.Count != 1 || inner[0].Length > MaxNestedAppBytes)
                {
                    throw new AppFileException("It is a Ready2Run package without exactly one app inside it.");
                }
                using var buffer = new MemoryStream();
                using (var entry = inner[0].Open()) entry.CopyTo(buffer);
                return Read(buffer, nested: true);
            }
            throw new AppFileException("Its package has no NavxManifest.xml, so the gateway cannot tell which app it is.");
        }
        if (manifest.Length > MaxManifestBytes)
        {
            throw new AppFileException("Its NavxManifest.xml is far larger than a manifest is.");
        }

        XDocument document;
        try
        {
            using var content = manifest.Open();
            using var reader = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxManifestBytes,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new AppFileException($"Its NavxManifest.xml could not be read: {ex.Message}");
        }
        var ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var app = document.Root?.Element(ns + "App")
                  ?? throw new AppFileException("Its NavxManifest.xml has no App element.");
        string Attribute(string name) => app.Attribute(name)?.Value is { Length: > 0 } value
            ? value
            : throw new AppFileException($"Its NavxManifest.xml does not say the app's {name}.");

        if (!Guid.TryParse(Attribute("Id"), out var id)) throw new AppFileException("Its NavxManifest.xml has an Id that is not a GUID.");
        if (!AppVersions.TryParse(Attribute("Version"), out var version)) throw new AppFileException("Its NavxManifest.xml has a Version that is not a version number.");
        return new AppFileInfo(id, Attribute("Name"), Attribute("Publisher"), version, HasSignature: false, PackageId: Guid.Empty);
    }

    /// <summary>
    /// ZIP entry names have drifted in case across versions (NavxManifest, navxmanifest).
    /// A package with two entries that match is refused: which one Business Central reads is
    /// not something to guess, and a second manifest naming an allowed publisher would
    /// otherwise get an app past the publisher list.
    /// </summary>
    private static ZipArchiveEntry? Entry(ZipArchive archive, string name)
    {
        var matches = archive.Entries.Where(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (matches.Count > 1)
        {
            throw new AppFileException($"Its package has more than one {name}, so the gateway cannot tell which app it is.");
        }
        return matches.FirstOrDefault();
    }

    /// <summary>A read-only, seekable view of part of another stream, so the ZIP reader sees only the package.</summary>
    private sealed class WindowStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public WindowStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = _length - _position;
            if (left <= 0) return 0;
            _inner.Position = _start + _position;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, left));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>App versions, compared as Business Central does: four parts, a missing part is 0.</summary>
public static class AppVersions
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(text?.Trim(), out var parsed)) return false;
        version = Normalise(parsed);
        return true;
    }

    public static Version Normalise(Version version) => new(
        version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
