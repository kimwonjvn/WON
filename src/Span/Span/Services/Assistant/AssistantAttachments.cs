using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Span.Services.Assistant;

public enum LlmAttachmentKind { Image, Text }

/// <summary>사용자가 대화에 직접 첨부한 이미지/텍스트 파일. 첨부한 내용은 선택한 서비스로 전송된다.</summary>
public sealed class LlmAttachment
{
    public LlmAttachmentKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>이미지일 때의 MIME 형식 (image/png 등).</summary>
    public string MediaType { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>텍스트 파일일 때의 내용.</summary>
    public string Text { get; init; } = string.Empty;

    public long SizeBytes => Kind == LlmAttachmentKind.Image ? Data.Length : Encoding.UTF8.GetByteCount(Text);

    /// <summary>텍스트 첨부를 모델에게 전달하는 형태.</summary>
    public string AsPromptText() =>
        $"<attached_file name=\"{Name.Replace('"', '\'')}\">\n{Text}\n</attached_file>";
}

public static class AssistantAttachmentLoader
{
    public const int MaxCount = 5;
    public const long MaxImageBytes = 5L * 1024 * 1024;
    public const long MaxTextBytes = 200L * 1024;
    public const long MaxTotalBytes = 8L * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp"
    };

    /// <summary>개인 키/인증서/비밀번호 저장소는 실수로라도 전송하지 않는다.</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".ppk", ".kdbx", ".keystore", ".jks", ".gpg", ".asc"
    };

    public static bool IsImageName(string name) => ImageTypes.ContainsKey(Path.GetExtension(name));

    public static bool TryLoad(string path, out LlmAttachment? attachment, out string error)
    {
        attachment = null; error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { error = "file not found"; return false; }
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path);
            if (BlockedExtensions.Contains(ext) || name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase))
            {
                error = "key / credential files cannot be attached";
                return false;
            }

            var length = new FileInfo(path).Length;
            if (ImageTypes.TryGetValue(ext, out var mediaType))
            {
                if (length > MaxImageBytes) { error = $"image is larger than {MaxImageBytes / 1024 / 1024} MB"; return false; }
                var bytes = File.ReadAllBytes(path);
                if (!LooksLikeImage(bytes, mediaType)) { error = "file content does not match its image type"; return false; }
                attachment = new LlmAttachment { Kind = LlmAttachmentKind.Image, Name = name, MediaType = mediaType, Data = bytes };
                return true;
            }

            if (length > MaxTextBytes) { error = $"text file is larger than {MaxTextBytes / 1024} KB"; return false; }
            var data = File.ReadAllBytes(path);
            if (Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0) { error = "binary files are not supported (images and text files only)"; return false; }
            string text;
            try { text = new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException) { error = "file is not UTF-8 text"; return false; }
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            attachment = new LlmAttachment { Kind = LlmAttachmentKind.Text, Name = name, Text = text };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>클립보드 등에서 얻은 PNG 바이트를 이미지 첨부로 만든다.</summary>
    public static LlmAttachment FromPngBytes(string name, byte[] png) =>
        new() { Kind = LlmAttachmentKind.Image, Name = name, MediaType = "image/png", Data = png };

    /// <summary>개수/총 용량 제한을 확인한다.</summary>
    public static bool CanAdd(IReadOnlyCollection<LlmAttachment> existing, LlmAttachment next, out string error)
    {
        error = string.Empty;
        if (existing.Count >= MaxCount) { error = $"at most {MaxCount} attachments per message"; return false; }
        if (existing.Sum(a => a.SizeBytes) + next.SizeBytes > MaxTotalBytes) { error = $"attachments exceed {MaxTotalBytes / 1024 / 1024} MB in total"; return false; }
        return true;
    }

    private static bool LooksLikeImage(byte[] b, string mediaType)
    {
        if (b.Length < 12) return false;
        return mediaType switch
        {
            "image/png" => b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
            "image/jpeg" => b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
            "image/gif" => b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8',
            "image/webp" => b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P',
            _ => false
        };
    }
}
