namespace Dataverse.Tests.Services;

using System.Text;
using Dataverse.Core.Io;
using NUnit.Framework;

/// <summary>
/// The rules behind "pass it inline or point at a file".
/// </summary>
/// <remarks>
/// The file path exists because copying a large payload inline corrupts it silently — see the remarks
/// on <see cref="PayloadSource"/>. These tests pin the parts that decide whether an agent gets a
/// usable error or a confusing one: which argument combinations are refused, and that a file is
/// returned byte-for-byte, BOM and all the characters that a copy would have flipped.
/// </remarks>
[TestFixture]
public sealed class PayloadSourceTests
{
    private string dir = null!;

    [SetUp]
    public void SetUp()
    {
        this.dir = Path.Combine(Path.GetTempPath(), "payloadsource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(this.dir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Test]
    public async Task Inline_IsReturnedAsGiven()
    {
        var result = await PayloadSource.ResolveAsync("{\"a\":1}", null, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Content, Is.EqualTo("{\"a\":1}"));
        });
    }

    [Test]
    public async Task File_IsReadVerbatim()
    {
        // The point of the parameter: content that no one had to retype.
        var content = "{\"img\":\"iVBORw0KGgoAAAANSUhEUgAA+/=abcXYZ019\"}";
        var path = this.WriteFile("definition.json", content);

        var result = await PayloadSource.ResolveAsync(null, path, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Content, Is.EqualTo(content));
        });
    }

    [Test]
    public async Task File_WithUtf8Bom_LosesTheBom()
    {
        // Left in place, the BOM makes the JSON parser fail at position 0 and the error reads as if
        // the file were malformed.
        var path = this.WriteFile("bom.json", "{\"a\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await PayloadSource.ResolveAsync(null, path, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Content, Is.EqualTo("{\"a\":1}"));
        });
    }

    [Test]
    public async Task File_KeepsNonAsciiIntact()
    {
        var content = "{\"reason\":\"Grüße aus München – geprüft, größtenteils fehlerfrei\"}";
        var path = this.WriteFile("umlauts.json", content);

        var result = await PayloadSource.ResolveAsync(null, path, "definitionJson", "definitionFile");

        Assert.That(result.Content, Is.EqualTo(content));
    }

    [Test]
    public async Task Neither_IsRefused_AndNamesBothArguments()
    {
        var result = await PayloadSource.ResolveAsync(null, null, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.Null);
            Assert.That(result.Error, Does.Contain("definitionJson").And.Contain("definitionFile"));
        });
    }

    [Test]
    public async Task Both_IsRefused_RatherThanPickingOne()
    {
        var path = this.WriteFile("definition.json", "{\"a\":1}");

        var result = await PayloadSource.ResolveAsync("{\"b\":2}", path, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.Null);
            Assert.That(result.Error, Does.Contain("not both"));
        });
    }

    [Test]
    public async Task WhitespaceInline_CountsAsAbsent()
    {
        var path = this.WriteFile("definition.json", "{\"a\":1}");

        var result = await PayloadSource.ResolveAsync("   ", path, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Content, Is.EqualTo("{\"a\":1}"));
        });
    }

    [Test]
    public async Task MissingFile_IsRefused_WithTheResolvedPath()
    {
        var path = Path.Combine(this.dir, "nope.json");

        var result = await PayloadSource.ResolveAsync(null, path, "definitionJson", "definitionFile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.Null);
            Assert.That(result.Error, Does.Contain("File not found").And.Contain(path));
        });
    }

    [Test]
    public async Task RelativePath_IsResolved_AndReportedInFull()
    {
        // A relative path resolves against the server's working directory, not the caller's — the
        // error has to show which path was actually tried.
        var result = await PayloadSource.ResolveAsync(null, "definitely-not-here.json", "xaml", "xamlFile");

        Assert.That(result.Error, Does.Contain(Path.GetFullPath("definitely-not-here.json")));
    }

    [Test]
    public async Task EmptyFile_IsRefused()
    {
        var path = this.WriteFile("empty.json", string.Empty);

        var result = await PayloadSource.ResolveAsync(null, path, "definitionJson", "definitionFile");

        Assert.That(result.Error, Does.Contain("empty"));
    }

    [Test]
    public async Task Write_CreatesTheDirectory_AndReturnsTheFullPath()
    {
        var path = Path.Combine(this.dir, "nested", "deeper", "backup.xaml");

        var written = await PayloadSource.WriteAsync(path, "<Activity />");

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(Path.GetFullPath(path)));
            Assert.That(File.ReadAllText(path), Is.EqualTo("<Activity />"));
        });
    }

    [Test]
    public async Task Write_ThenResolve_RoundTripsUnchanged()
    {
        // backupFile → xamlFile is the undo path; it has to survive the trip through disk.
        var xaml = "<Activity xmlns:x=\"…\">Ümlaut & <![CDATA[raw]]></Activity>";
        var path = Path.Combine(this.dir, "backup.xaml");

        await PayloadSource.WriteAsync(path, xaml);
        var result = await PayloadSource.ResolveAsync(null, path, "xaml", "xamlFile");

        Assert.That(result.Content, Is.EqualTo(xaml));
    }
}
