using System;
using System.IO;
using System.Linq;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexImagePickerFlowTests : IDisposable
{
	private static readonly byte[] PngBytes = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexImagePickerFlow_" + Guid.NewGuid().ToString("N"));

	public CodexImagePickerFlowTests()
	{
		Directory.CreateDirectory(_directory);
	}

	public void Dispose()
	{
		Directory.Delete(_directory, recursive: true);
	}

	private string CreateImage(string name = "photo.png")
	{
		var path = Path.Combine(_directory, name);
		File.WriteAllBytes(path, PngBytes);
		return path;
	}

	[Fact]
	public void PickedImageIsReadBackWithTheFieldTheWebviewExpects()
	{
		var path = CreateImage();

		// What the picker returns to the webview, then what the webview asks for next.
		var picked = CodexOfficialWebViewBridge.ToPickedFile(path);
		var reply = CodexOfficialWebViewBridge.ReadBinaryFile(new JObject
		{
			["path"] = picked["fsPath"]
		});

		Assert.Equal(Convert.ToBase64String(PngBytes), reply["contentsBase64"]?.Value<string>());
	}

	[Fact]
	public void PickedImageReachesTurnStartAsInlineDataUrl()
	{
		var path = CreateImage();
		var request = new JObject
		{
			["threadId"] = "t1",
			["input"] = new JArray(
				new JObject { ["type"] = "text", ["text"] = "what is this?", ["text_elements"] = new JArray() },
				new JObject { ["type"] = "localImage", ["path"] = path })
		};

		var result = (JObject)CodexImageInputInliner.InlineImages("turn/start", request, _directory)!;

		var image = Assert.Single(((JArray)result["input"]!).OfType<JObject>(), i => i["type"]?.Value<string>() == "image");
		Assert.Equal("data:image/png;base64," + Convert.ToBase64String(PngBytes), image["url"]?.Value<string>());
		Assert.DoesNotContain(((JArray)result["input"]!).OfType<JObject>(), i => i["type"]?.Value<string>() == "localImage");
	}

	[Fact]
	public void ImageListedAsAMentionedFileIsAddedInline()
	{
		var path = CreateImage();
		var text = "# Files mentioned by the user:\n\n## photo.png: " + path + "\n\n## My request for Codex:\nlook";
		var request = new JObject
		{
			["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = text })
		};

		var result = (JObject)CodexImageInputInliner.InlineImages("turn/start", request, _directory)!;

		var image = Assert.Single(((JArray)result["input"]!).OfType<JObject>(), i => i["type"]?.Value<string>() == "image");
		Assert.StartsWith("data:image/png;base64,", image["url"]?.Value<string>());
	}

	[Fact]
	public void PosixStyleWindowsPathIsResolved()
	{
		var path = CreateImage();
		var posix = "/" + path.Replace('\\', '/');
		var request = new JObject
		{
			["input"] = new JArray(new JObject { ["type"] = "localImage", ["path"] = posix })
		};

		var result = (JObject)CodexImageInputInliner.InlineImages("turn/start", request, _directory)!;

		Assert.Equal("image", ((JArray)result["input"]!)[0]!["type"]?.Value<string>());
	}
}
