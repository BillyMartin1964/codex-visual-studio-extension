using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Guarantees that image inputs sent to the app-server carry the raw pixel data as an inline
/// base64 data URL instead of a file path/name that the sandbox may not be able to resolve.
/// </summary>
internal static class CodexImageInputInliner
{
	public static string GetImageMimeType(string path)
	{
		return Path.GetExtension(path).ToLowerInvariant() switch
		{
			".jpg" or ".jpeg" => "image/jpeg",
			".gif" => "image/gif",
			".webp" => "image/webp",
			".bmp" => "image/bmp",
			_ => "image/png"
		};
	}

	public static JObject CreateInlineImage(string path)
	{
		var fullPath = Path.GetFullPath(path);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException("The attached image is no longer available: " + fullPath, fullPath);
		}

		var imageBytes = File.ReadAllBytes(fullPath);
		if (imageBytes.Length == 0)
		{
			throw new InvalidDataException("The attached image is empty.");
		}

		return new JObject
		{
			["type"] = "image",
			["url"] = "data:" + GetImageMimeType(fullPath) + ";base64," + Convert.ToBase64String(imageBytes)
		};
	}

	/// <summary>
	/// Rewrites every localImage item, and every image item whose url is not already a data/http URL,
	/// into { "type": "image", "url": "data:...;base64,..." }.
	/// </summary>
	public static JToken? InlineImages(string method, JToken? parameters, string workingDirectory)
	{
		if (!string.Equals(method, "turn/start", StringComparison.Ordinal)
			&& !string.Equals(method, "turn/steer", StringComparison.Ordinal))
		{
			return parameters;
		}

		if (parameters is not JObject source || source["input"] is not JArray)
		{
			return parameters;
		}

		var result = (JObject)source.DeepClone();
		var input = (JArray)result["input"]!;
		AppendMentionedImageFiles(input, workingDirectory);
		for (var index = 0; index < input.Count; index++)
		{
			if (input[index] is not JObject item)
			{
				continue;
			}

			var type = item["type"]?.Value<string>();
			string? reference;
			if (string.Equals(type, "localImage", StringComparison.OrdinalIgnoreCase))
			{
				reference = FirstString(item, "path", "url", "uri", "name");
			}
			else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
			{
				reference = FirstString(item, "url", "image_url", "path", "uri", "name");
				if (reference is null || IsRemoteOrInline(reference))
				{
					continue;
				}
			}
			else
			{
				continue;
			}

			if (string.IsNullOrWhiteSpace(reference))
			{
				throw new InvalidDataException("The attached image has no readable location.");
			}

			input[index] = CreateInlineImage(ResolvePath(reference!, workingDirectory));
		}

		return result;
	}

	private static readonly Regex MentionedFileLine = new(
		@"^##\s+.+?:\s+(?<path>.+?)\s*(\((lines\s+\d+-\d+|line\s+\d+)\))?\s*$",
		RegexOptions.Compiled);

	private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

	/// <summary>
	/// Files picked with the + button reach the host only as text lines under
	/// "# Files mentioned by the user:" ("## label: path"). Image files among them are appended
	/// as inline images so the model gets the pixels instead of a path it cannot open.
	/// </summary>
	private static void AppendMentionedImageFiles(JArray input, string workingDirectory)
	{
		const string header = "# Files mentioned by the user:";
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var additions = new List<JObject>();
		foreach (var item in input)
		{
			if (!string.Equals(item?["type"]?.Value<string>(), "text", StringComparison.Ordinal))
			{
				continue;
			}

			var text = item!["text"]?.Value<string>();
			var start = text?.IndexOf(header, StringComparison.Ordinal) ?? -1;
			if (text is null || start < 0)
			{
				continue;
			}

			var end = text.IndexOf("## My request for Codex:", start, StringComparison.Ordinal);
			var section = end < 0 ? text.Substring(start + header.Length) : text.Substring(start + header.Length, end - start - header.Length);
			foreach (var rawLine in section.Split('\n'))
			{
				var line = rawLine.Trim();
				if (line.Length == 0)
				{
					continue;
				}

				var match = MentionedFileLine.Match(line);
				if (!match.Success)
				{
					break;
				}

				var path = ResolvePath(match.Groups["path"].Value, workingDirectory);
				if (Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) < 0
					|| !File.Exists(path)
					|| !seen.Add(path))
				{
					continue;
				}

				additions.Add(CreateInlineImage(path));
			}
		}

		foreach (var addition in additions)
		{
			input.Add(addition);
		}
	}

	private static string? FirstString(JObject item, params string[] names)
	{
		foreach (var name in names)
		{
			var value = item[name]?.Type == JTokenType.String ? item[name]!.Value<string>() : null;
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
		}

		return null;
	}

	private static bool IsRemoteOrInline(string reference)
	{
		return reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
			|| reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
	}

	private static string ResolvePath(string reference, string workingDirectory)
	{
		var path = reference.Trim();
		if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
		{
			path = uri.LocalPath;
		}

		// The renderer normalizes paths to POSIX style, e.g. "/C:/Users/me/image.png".
		if (path.Length >= 3 && (path[0] == '/' || path[0] == '\\') && char.IsLetter(path[1]) && path[2] == ':')
		{
			path = path.Substring(1);
		}

		path = path.Replace('/', Path.DirectorySeparatorChar);
		if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(workingDirectory))
		{
			path = Path.Combine(workingDirectory, path);
		}

		return path;
	}
}
