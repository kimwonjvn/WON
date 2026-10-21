using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Span.Models;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Span.Services
{
    /// <summary>
    /// 미리보기 서비스 구현. 파일 확장자에 따라 미리보기 유형(Image/Text/PDF/Media/Hex/Font)을 결정하고,
    /// 각 유형별 비동기 로더(썸네일, 텍스트 읽기, PDF 렌더링, MediaSource, Hex 덤프, 폰트 파싱)를 제공.
    /// 클라우드 전용 파일은 다운로드 트리거를 방지한다.
    /// </summary>
    public class PreviewService : IPreviewService
    {
        // Issue #56: .jfif는 JPEG 별칭(WIC 네이티브)이라 미리보기까지 완전 동작.
        // .clip은 자체 추출기(ClipThumbnailExtractor)로 내장 미리보기(CanvasPreview)를 뽑으므로
        // 셸 코덱 호출 없이 안전 → 미리보기도 지원(LoadImagePreviewAsync의 .clip 분기).
        // .psd만 제외 — 미리보기는 인프로세스 셸 코덱 호출이라 크래시 리스크가 있어
        // 격리 워커가 처리하는 썸네일 게이트(FileViewModel)에서만 연다.
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".ico",
            ".jfif", ".clip"
        };

        private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown"
        };

        private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".tsv"
        };

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".cs", ".json", ".xml", ".log", ".ini", ".cfg", ".yaml", ".yml",
            ".toml", ".html", ".htm", ".css", ".js", ".ts", ".py", ".java", ".cpp", ".c",
            ".h", ".go", ".rs", ".sh", ".bat", ".ps1", ".sql", ".gitignore",
            ".editorconfig", ".env", ".dockerfile", ".xaml", ".csproj", ".sln",

            // Issue #69: 아래 9개는 _extToLanguage(PreviewPanelView)에 구문 강조가 이미
            // 매핑돼 있었는데 이 목록에 없어 미리보기가 열리지 않았다 — 강조 코드가
            // 도달 불가 상태였다. 신고자가 걸린 .php가 정확히 이 경우다.
            ".php", ".jsx", ".tsx", ".hpp", ".psm1", ".vb", ".fs", ".fsx", ".svg",

            // Issue #69: 흔한 코드/설정 확장자. ColorCode에 강조기가 없어 평문으로 뜨지만
            // 아무것도 안 뜨는 것보다 낫다. 여기 없는 것은 아래 내용 판별이 받아낸다.
            //
            // 이진 포맷과 이름이 겹치는 확장자는 이 목록에 넣지 않는다. 여기 있으면 내용
            // 판별 없이 Text로 확정돼 이진 파일이 깨진 텍스트로 뜬다. 판별에 맡기면 텍스트
            // 쪽은 그대로 보이고, 이진 쪽은 커밋 전처럼 메타데이터만 뜬다. 단 판별은 로컬
            // 파일에서만 한다 — 네트워크·광학·클라우드 전용 파일과 심볼릭 링크는 텍스트 쪽도
            // 메타데이터만 뜬다(LooksLikeText 참고). 뺀 것들:
            //   .mts  TypeScript 모듈  <->  AVCHD 캠코더 영상(00001.MTS)
            //   .mod  Go 모듈          <->  트래커 음악, JVC 캠코더 영상
            //   .pot  gettext 템플릿   <->  PowerPoint 97-2003 서식(OLE)
            //   .plist XML plist       <->  bplist00 이진 plist
            //   .lock 패키지 잠금       <->  앱별 이진 잠금 파일
            ".cc", ".cxx", ".hh", ".hxx", ".m", ".mm", ".ino", ".asm", ".pas", ".d",
            ".rb", ".lua", ".pl", ".pm", ".r", ".kt", ".kts", ".swift", ".dart",
            ".scala", ".groovy", ".ex", ".exs", ".erl", ".clj", ".hs", ".ml", ".nim",
            ".zig", ".jl", ".vbs", ".zsh", ".bash", ".fish", ".cmd",
            ".vue", ".svelte", ".scss", ".sass", ".less", ".styl", ".astro",
            ".mjs", ".cjs", ".cts",
            ".conf", ".properties", ".gradle", ".tf", ".tfvars", ".hcl", ".proto",
            ".graphql", ".gql", ".prisma", ".cmake", ".mk", ".nix", ".bzl", ".rc",
            ".sum", ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc",
            ".babelrc", ".gitattributes", ".gitmodules",
            ".rst", ".adoc", ".tex", ".bib", ".srt", ".vtt", ".po",
            ".diff", ".patch", ".reg", ".inf"
        };

        /// <summary>
        /// Issue #69: 확장자만으로는 텍스트인지 이진인지 단정할 수 없는 것들.
        /// 내용을 들여다본 뒤 텍스트가 아니면 기존대로 헥스 뷰어로 보낸다.
        /// </summary>
        private static readonly HashSet<string> AmbiguousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dat", ".data", ".out", ".sav", ".bak", ".tmp", ".temp", ".cache", ".db"
        };

        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf"
        };

        private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mp3", ".wav", ".wma", ".avi", ".mkv", ".flac", ".ogg", ".aac",
            ".m4a", ".m4v", ".mov", ".wmv", ".webm"
        };

        private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ttf", ".otf", ".woff", ".woff2", ".ttc"
        };

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz",
            ".tgz", ".tbz2", ".txz", ".cab"
        };

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // .dat은 AmbiguousExtensions로 옮겼다 (Issue #69) — 텍스트인 .dat이 흔하다.
            ".dll", ".exe", ".sys", ".bin", ".so", ".dylib", ".o", ".obj",
            ".class", ".pyc", ".pdb", ".lib", ".a", ".wasm"
        };

        private const long MaxPreviewFileSize = 100 * 1024 * 1024; // 100MB
        private const int MaxTextChars = 30000;
        // Issue #69: 텍스트 인코딩 판별에 읽을 바이트. MaxTextChars를 CP949(2바이트/자)로 채우는
        // 크기 이상이라 화면에 뜰 부분을 대부분 덮는다.
        private const int EncodingProbeBytes = 64 * 1024;
        private const int HexPreviewBytes = 512; // Hex viewer: first 512 bytes

        // Issue #69: 내용 판별에 읽을 바이트 수. 텍스트/이진 구분에는 이 정도면 충분하고,
        // 한 번에 읽히는 크기라 디스크 왕복이 1회다.
        private const int SniffBytes = 4096;
        // 제어문자가 이 비율(%)을 넘으면 이진으로 본다.
        private const int SniffControlCharPercent = 5;

        public PreviewType GetPreviewType(string? filePath, bool isFolder)
        {
            if (isFolder) return PreviewType.Folder;
            if (string.IsNullOrEmpty(filePath)) return PreviewType.None;

            var ext = Path.GetExtension(filePath);

            // 확장자 없는 파일(Makefile, LICENSE, README 등)도 내용으로 판별한다.
            if (string.IsNullOrEmpty(ext))
                return LooksLikeText(filePath) ? PreviewType.Text : PreviewType.Generic;

            if (ImageExtensions.Contains(ext)) return PreviewType.Image;
            if (MarkdownExtensions.Contains(ext)) return PreviewType.Markdown;
            if (CsvExtensions.Contains(ext)) return PreviewType.Csv;
            if (TextExtensions.Contains(ext)) return PreviewType.Text;
            if (PdfExtensions.Contains(ext)) return PreviewType.Pdf;
            if (MediaExtensions.Contains(ext)) return PreviewType.Media;
            if (FontExtensions.Contains(ext)) return PreviewType.Font;
            if (ArchiveExtensions.Contains(ext)) return PreviewType.Archive;

            // Issue #69: 모호한 확장자는 내용을 보고 정한다. 아니면 기존대로 헥스.
            if (AmbiguousExtensions.Contains(ext))
                return LooksLikeText(filePath) ? PreviewType.Text : PreviewType.HexBinary;

            if (BinaryExtensions.Contains(ext)) return PreviewType.HexBinary;

            // Issue #69: 목록에 없는 확장자 — 텍스트면 보여준다. 목록을 무한히 늘리는 대신
            // 내용으로 받아낸다. 아니면 기존대로 메타데이터만.
            return LooksLikeText(filePath) ? PreviewType.Text : PreviewType.Generic;
        }

        /// <summary>
        /// Issue #69: 파일 앞부분을 읽어 텍스트인지 판정한다. 확장자로 단정할 수 없는
        /// 파일(.dat, 확장자 없음, 목록에 없는 코드 파일)에만 쓴다.
        ///
        /// 판정: NUL 바이트가 하나라도 있으면 이진. BOM이 있으면 즉시 텍스트.
        /// 그 외에는 제어문자 비율로 가른다. UTF-8 멀티바이트(0x80~)는 세지 않으므로
        /// 한국어/중국어/일본어 텍스트도 통과한다.
        ///
        /// 읽지 않는 경우 — 모두 false를 돌려 기존 동작(Generic/HexBinary)을 유지한다:
        ///   압축 내부·FTP/SFTP 경로 — 실제 로컬 파일이 아니라 열어 봐야 예외만 난다.
        ///   네트워크 경로(UNC·매핑 드라이브)와 광학 드라이브 — 잠든 서버에서 FileStream
        ///     열기가 42초 블록되는 것을 실측했다. 알려진 텍스트 확장자는 어차피 미리보기
        ///     로더가 읽지만, 이진일 수도 있는 파일까지 투기적으로 읽어 UI를 세우지는 않는다.
        ///     (Issue #67의 UI 스레드 블로킹이 해소되면 이 가드는 걷어낼 수 있다.)
        ///   클라우드 전용 파일 — 여기서 열면 하이드레이션(다운로드)이 걸린다. 호출자의
        ///     클라우드 가드는 GetPreviewType "다음"에 있어서 이 안에서 막아야 한다.
        ///   파일 심볼릭 링크 — 링크 자체는 로컬이라 위 가드를 통과하지만, 열면 대상으로
        ///     따라간다. 대상이 네트워크(직접이든, 대상 경로 중간의 폴더 링크를 거치든)거나
        ///     클라우드 전용이면 같은 블록·하이드레이션이 난다. 대상 경로의 구성요소를 하나씩
        ///     풀지 않고는 안전을 가릴 수 없어서, 링크는 판별하지 않는다(2.0.6과 같은 동작).
        /// </summary>
        private static bool LooksLikeText(string filePath)
        {
            try
            {
                if (Helpers.ArchivePathHelper.IsArchivePath(filePath) || FileSystemRouter.IsRemotePath(filePath)) return false;
                if (IsNetworkOrOpticalPath(filePath)) return false;
                if (CloudSyncService.IsCloudOnlyFile(filePath)) return false;
                if (IsSymbolicLink(filePath)) return false;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length == 0) return true;   // 빈 파일은 헥스 뷰어보다 빈 텍스트가 낫다

                Span<byte> buf = stackalloc byte[SniffBytes];
                int read = fs.Read(buf);
                if (read <= 0) return true;
                buf = buf[..read];

                // BOM이면 확정
                if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return true;
                if (read >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;

                int control = 0;
                foreach (byte b in buf)
                {
                    if (b == 0) return false;                             // NUL 하나면 이진 확정
                    if (b < 0x09 || (b > 0x0D && b < 0x20) || b == 0x7F)  // 탭/개행 제외한 제어문자
                        control++;
                }
                return control * 100 / read <= SniffControlCharPercent;
            }
            catch
            {
                return false;   // 잠김/권한 없음 등 — 기존 동작 유지
            }
        }

        /// <summary>
        /// 열기가 수십 초 블록될 수 있는 경로인지. UNC(\\server\share, \\?\ 포함), 매핑된
        /// 네트워크 드라이브(Z:\ -> \\server\share), 광학 드라이브. 이동식(USB)은 로컬이라 뺀다.
        ///
        /// GetDriveType은 네트워크를 타지 않는다 — 실측 1000회 평균 2~17μs, 서버가 꺼진
        /// 매핑 드라이브에서도 17μs(열기는 42초 블록되는 바로 그 드라이브). 그래서 캐시하지
        /// 않는다. 드라이브 문자별로 캐시하면 세션 중 매핑이 바뀔 때(USB였던 Z:가 네트워크
        /// 드라이브로) 틀린 값이 남는다.
        /// </summary>
        private static bool IsNetworkOrOpticalPath(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            {
                var type = new DriveInfo(path[0].ToString()).DriveType;
                return type is DriveType.Network or DriveType.CDRom;
            }
            return false;
        }

        /// <summary>
        /// 파일 심볼릭 링크인지. 링크 자체의 속성과 재분석 데이터만 읽으므로 대상에 접근하지
        /// 않는다(실측: 없는 서버를 가리키는 링크에서 1ms 미만, 가드 없이 열면 약 20초 블록).
        /// OneDrive 자리표시자·앱 실행 별칭 같은 다른 재분석 지점은 LinkTarget이 null이라
        /// 해당하지 않는다(읽어도 하이드레이션 없음).
        /// 폴더 링크 아래의 파일(C:\mnt\a.dat, mnt → \\server\share)은 잡지 못한다 — 그 폴더는
        /// 목록을 읽을 때 이미 네트워크를 탄다.
        /// </summary>
        private static bool IsSymbolicLink(string path)
        {
            var info = new FileInfo(path);
            return (info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }

        public FilePreviewMetadata GetBasicMetadata(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                return new FilePreviewMetadata
                {
                    FileName = fi.Name,
                    Size = fi.Length,
                    Created = fi.CreationTime,
                    Modified = fi.LastWriteTime,
                    Extension = fi.Extension,
                    IsReadOnly = fi.IsReadOnly
                };
            }
            catch
            {
                return new FilePreviewMetadata { FileName = Path.GetFileName(filePath) };
            }
        }

        public int GetFolderItemCount(string folderPath)
        {
            try
            {
                int count = 0;
                var di = new DirectoryInfo(folderPath);
                foreach (var entry in di.EnumerateFileSystemInfos())
                {
                    // 이 경로는 ShowHiddenFiles 설정을 반영하지 않는다 — 본 목록과 불일치(별건).
                    if (Helpers.FileVisibility.IsHidden(entry.Attributes)) continue;
                    count++;
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        public async Task<BitmapImage?> LoadImagePreviewAsync(string filePath, uint maxSize, CancellationToken ct)
        {
            try
            {
                // Issue #56: .clip은 셸/WIC로 못 여니 격리 워커가 내장 SQLite 미리보기를 추출·리사이즈.
                if (string.Equals(Path.GetExtension(filePath), ".clip", StringComparison.OrdinalIgnoreCase))
                    return await LoadClipPreviewAsync(filePath, maxSize, ct);

                var fi = new FileInfo(filePath);
                if (fi.Length > MaxPreviewFileSize) return null;

                bool isCloudOnly = CloudSyncService.IsCloudOnlyFile(filePath);

                var file = await StorageFile.GetFileFromPathAsync(filePath);
                ct.ThrowIfCancellationRequested();

                // Cloud-only: use cached thumbnail only to avoid triggering download
                var thumbOptions = isCloudOnly
                    ? ThumbnailOptions.ReturnOnlyIfCached
                    : ThumbnailOptions.UseCurrentScale;

                using var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem, maxSize, thumbOptions);

                ct.ThrowIfCancellationRequested();

                if (thumbnail != null && thumbnail.Type == ThumbnailType.Image)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    ct.ThrowIfCancellationRequested(); // SetSourceAsync 후 취소 여부 재확인
                    return bitmap;
                }

                // Fallback: load full image (skip for cloud-only files to prevent download)
                if (isCloudOnly) return null;

                using var stream = await file.OpenReadAsync();
                ct.ThrowIfCancellationRequested();

                var fullBitmap = new BitmapImage();
                await fullBitmap.SetSourceAsync(stream);
                ct.ThrowIfCancellationRequested(); // SetSourceAsync 후 취소 여부 재확인
                return fullBitmap;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] Image load error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Issue #56: .clip 미리보기 — 격리 워커(Span.Thumbs)가 내장 SQLite 미리보기(CanvasPreview)를
        /// 추출·리사이즈해 PNG 캐시로 반환하고, 메인은 그 file:// URI를 BitmapImage로 로드한다.
        /// SQLite 네이티브를 메인 프로세스에 두지 않기 위해 썸네일과 동일한 워커 경로를 재사용한다
        /// (SQLitePCLRaw 네이티브가 WinUI 3 MSIX 레이아웃에 포함되지 않는 문제 회피 + 크래시 격리).
        /// 워커 비활성/실패 시 null(미리보기 없음).
        /// </summary>
        private static async Task<BitmapImage?> LoadClipPreviewAsync(string filePath, uint maxSize, CancellationToken ct)
        {
            var client = App.Current.Services.GetService(typeof(Services.Thumbnails.ThumbnailClientService))
                as Services.Thumbnails.ThumbnailClientService;
            if (client == null) return null;

            var uri = await client.GetThumbnailUriAsync(
                filePath,
                (int)maxSize,
                mode: "SingleItem",
                isCloudOnly: false,
                applyExif: false,
                theme: "Default",
                dpi: 96,
                ct: ct);
            if (uri == null) return null;
            ct.ThrowIfCancellationRequested();

            return new BitmapImage { UriSource = uri };
        }

        public async Task<string?> LoadTextPreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                // Issue #69: 다른 프로세스가 쓰는 중인 파일(활성 로그, nohup.out 등)도 읽는다.
                // StreamReader(path)는 FileShare.Read로 열어 쓰는 중인 파일에서 공유 위반이 났고,
                // 판별기(LooksLikeText)는 ReadWrite로 열어 Text로 판정한 뒤 빈 화면이 됐다.
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);

                // Issue #69: BOM 없는 레거시 인코딩(CP949·GBK·Shift-JIS)을 UTF-8로 읽으면 글자가
                // 깨진다. 앞부분으로 인코딩을 정하고 처음으로 되감는다. BOM이 있으면 StreamReader가
                // 그쪽을 따른다(detectEncodingFromByteOrderMarks).
                var head = new byte[(int)Math.Min(fs.Length, EncodingProbeBytes)];
                int headRead = await fs.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                fs.Position = 0;
                var encoding = Helpers.TextEncodingDetector.DetectNoBom(
                    head.AsSpan(0, headRead), isWholeFile: headRead >= fs.Length);

                using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaxTextChars];
                int charsRead = await reader.ReadAsync(buffer, 0, MaxTextChars);

                ct.ThrowIfCancellationRequested();

                var text = new string(buffer, 0, charsRead);
                if (charsRead == MaxTextChars && !reader.EndOfStream)
                {
                    text += "\n\n" + LocalizationService.L("Preview_Truncated");
                }

                return text;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] Text load error: {ex.Message}");
                return null;
            }
        }

        public async Task<BitmapImage?> LoadPdfPreviewAsync(string filePath, CancellationToken ct)
        {
            InMemoryRandomAccessStream? stream = null;
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length > MaxPreviewFileSize) return null;

                // Issue #60: PdfDocument.LoadFromFileAsync(StorageFile)는 파일 핸들을 문서 수명
                // 동안 유지하는데 PdfDocument에는 Close API가 없어 GC 시점까지(비결정적) 락이
                // 지속됨 → 미리보기 중인 PDF를 삭제/이동할 수 없었다. 파일을 읽고 즉시 닫은 뒤
                // 메모리 스트림으로 로드하여 파일 핸들을 결정적으로 해제한다.
                // (FileShare.Delete 포함 — 읽는 도중의 삭제도 허용)
                byte[] pdfBytes;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    pdfBytes = new byte[fs.Length];
                    await fs.ReadExactlyAsync(pdfBytes, ct);
                }
                ct.ThrowIfCancellationRequested();

                var pdfDoc = await PdfDocument.LoadFromStreamAsync(
                    new MemoryStream(pdfBytes).AsRandomAccessStream());
                if (pdfDoc.PageCount == 0) return null;

                using var page = pdfDoc.GetPage(0);
                stream = new InMemoryRandomAccessStream();

                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = 1024,
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };

                await page.RenderToStreamAsync(stream, options);
                ct.ThrowIfCancellationRequested();

                var bitmap = new BitmapImage();
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);

                // SetSourceAsync 완료 후 취소 여부 재확인 (H-5)
                ct.ThrowIfCancellationRequested();

                stream = null; // bitmap이 소유 — dispose 하지 않음
                return bitmap;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] PDF load error: {ex.Message}");
                return null;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        public async Task<MediaSource?> LoadMediaSourceAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                ct.ThrowIfCancellationRequested();
                return MediaSource.CreateFromStorageFile(file);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] Media load error: {ex.Message}");
                return null;
            }
        }

        public async Task<ImageMetadata?> GetImageMetadataAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                ct.ThrowIfCancellationRequested();

                var props = await file.Properties.GetImagePropertiesAsync();
                ct.ThrowIfCancellationRequested();

                return new ImageMetadata(props.Width, props.Height, props.DateTaken,
                    props.CameraManufacturer, props.CameraModel);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read the first N bytes of a binary file and format as hex dump.
        /// Format: offset  hex bytes (16 per row)  ASCII representation
        /// </summary>
        public async Task<string?> LoadHexPreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length == 0) return LocalizationService.L("Preview_EmptyFile");

                int bytesToRead = (int)Math.Min(fi.Length, HexPreviewBytes);
                var buffer = new byte[bytesToRead];

                // Issue #69: 쓰는 중인 파일도 연다 — 텍스트 로더·판별기와 같은 공유 모드.
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                int read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);

                ct.ThrowIfCancellationRequested();

                var sb = new System.Text.StringBuilder((read / 16 + 1) * 80);
                for (int i = 0; i < read; i += 16)
                {
                    // Offset
                    sb.Append(i.ToString("X8"));
                    sb.Append("  ");

                    // Hex bytes
                    int lineLen = Math.Min(16, read - i);
                    for (int j = 0; j < 16; j++)
                    {
                        if (j < lineLen)
                        {
                            sb.Append(buffer[i + j].ToString("X2"));
                            sb.Append(' ');
                        }
                        else
                        {
                            sb.Append("   ");
                        }
                        if (j == 7) sb.Append(' '); // mid-separator
                    }

                    sb.Append(' ');

                    // ASCII
                    for (int j = 0; j < lineLen; j++)
                    {
                        byte b = buffer[i + j];
                        sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
                    }

                    sb.AppendLine();
                }

                if (fi.Length > HexPreviewBytes)
                    sb.AppendLine($"\n{string.Format(LocalizationService.L("Preview_BytesShowing"), HexPreviewBytes, fi.Length)}");

                return sb.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] Hex load error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get font metadata by parsing the TrueType/OpenType 'name' table
        /// to extract the font family name for FontFamily binding.
        /// </summary>
        public FontPreviewData? GetFontPreviewData(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length > MaxPreviewFileSize) return null;

                var familyName = ExtractFontFamilyName(filePath);

                return new FontPreviewData
                {
                    FilePath = filePath,
                    FamilyName = familyName ?? fi.Name,
                    FileName = fi.Name,
                    FileSize = fi.Length,
                    Extension = fi.Extension.ToUpperInvariant().TrimStart('.')
                };
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PreviewService] Font preview error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parse TrueType/OpenType 'name' table to extract font family name (nameID=1).
        /// Prefers Windows platform (UTF-16 BE), falls back to Mac (ASCII).
        /// </summary>
        private static string? ExtractFontFamilyName(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var reader = new BinaryReader(stream);

                if (stream.Length < 12) return null;

                // Offset table header
                reader.ReadBytes(4); // sfVersion
                ushort numTables = ReadUInt16BE(reader);
                reader.ReadBytes(6); // searchRange, entrySelector, rangeShift

                // Find 'name' table
                uint nameTableOffset = 0;
                for (int i = 0; i < numTables; i++)
                {
                    if (stream.Position + 16 > stream.Length) return null;
                    byte[] tagBytes = reader.ReadBytes(4);
                    string tag = System.Text.Encoding.ASCII.GetString(tagBytes);
                    reader.ReadBytes(4); // checksum
                    uint offset = ReadUInt32BE(reader);
                    reader.ReadBytes(4); // length

                    if (tag == "name")
                    {
                        nameTableOffset = offset;
                        break;
                    }
                }

                if (nameTableOffset == 0) return null;

                // Read name table header
                stream.Seek(nameTableOffset, SeekOrigin.Begin);
                if (stream.Position + 6 > stream.Length) return null;
                reader.ReadBytes(2); // format
                ushort nameCount = ReadUInt16BE(reader);
                ushort stringOffset = ReadUInt16BE(reader);
                long stringsBase = nameTableOffset + stringOffset;

                // Scan name records for nameID=1 (Font Family)
                string? windowsName = null;
                string? macName = null;

                for (int i = 0; i < nameCount; i++)
                {
                    if (stream.Position + 12 > stream.Length) break;
                    ushort platformID = ReadUInt16BE(reader);
                    reader.ReadBytes(2); // encodingID
                    reader.ReadBytes(2); // languageID
                    ushort nameID = ReadUInt16BE(reader);
                    ushort length = ReadUInt16BE(reader);
                    ushort strOff = ReadUInt16BE(reader);

                    if (nameID != 1) continue;

                    long savedPos = stream.Position;
                    long targetPos = stringsBase + strOff;
                    if (targetPos + length > stream.Length) continue;

                    stream.Seek(targetPos, SeekOrigin.Begin);
                    byte[] nameBytes = reader.ReadBytes(length);
                    stream.Seek(savedPos, SeekOrigin.Begin);

                    if (platformID == 3 && windowsName == null)
                        windowsName = System.Text.Encoding.BigEndianUnicode.GetString(nameBytes);
                    else if (platformID == 1 && macName == null)
                        macName = System.Text.Encoding.ASCII.GetString(nameBytes);

                    if (windowsName != null) break; // prefer Windows name
                }

                return windowsName ?? macName;
            }
            catch
            {
                return null;
            }
        }

        private static ushort ReadUInt16BE(BinaryReader r)
        {
            byte[] b = r.ReadBytes(2);
            return (ushort)((b[0] << 8) | b[1]);
        }

        private static uint ReadUInt32BE(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        public async Task<MediaMetadata?> GetMediaMetadataAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                ct.ThrowIfCancellationRequested();

                var contentType = file.ContentType;

                if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    ct.ThrowIfCancellationRequested();
                    return new MediaMetadata(props.Duration, props.Bitrate, props.Width, props.Height, null, null);
                }

                if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    var props = await file.Properties.GetMusicPropertiesAsync();
                    ct.ThrowIfCancellationRequested();
                    return new MediaMetadata(props.Duration, props.Bitrate, null, null, props.Artist, props.Album);
                }

                // Fallback: try by extension
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".m4v" or ".webm")
                {
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    ct.ThrowIfCancellationRequested();
                    return new MediaMetadata(props.Duration, props.Bitrate, props.Width, props.Height, null, null);
                }
                else
                {
                    var props = await file.Properties.GetMusicPropertiesAsync();
                    ct.ThrowIfCancellationRequested();
                    return new MediaMetadata(props.Duration, props.Bitrate, null, null, props.Artist, props.Album);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }
    }

    public record FilePreviewMetadata
    {
        public string FileName { get; init; } = "";
        public long Size { get; init; }
        public DateTime Created { get; init; }
        public DateTime Modified { get; init; }
        public string Extension { get; init; } = "";
        public bool IsReadOnly { get; init; }

        public string SizeFormatted => FormatBytes(Size);

        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < SizeUnits.Length - 1) { order++; size /= 1024; }
            return $"{size:0.##} {SizeUnits[order]}";
        }
    }

    public record ImageMetadata(uint Width, uint Height, DateTimeOffset? DateTaken,
                                 string? CameraManufacturer, string? CameraModel);

    public record MediaMetadata(TimeSpan Duration, uint Bitrate,
                                 uint? Width, uint? Height,
                                 string? Artist, string? Album);

    public record FontPreviewData
    {
        public string FilePath { get; init; } = "";
        public string FamilyName { get; init; } = "";
        public string FileName { get; init; } = "";
        public long FileSize { get; init; }
        public string Extension { get; init; } = "";
    }
}
