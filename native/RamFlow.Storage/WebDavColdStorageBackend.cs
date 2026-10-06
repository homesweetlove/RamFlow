using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace RamFlow.Storage;

/// <summary>
/// 이미 존재하는 HTTPS WebDAV 컬렉션만 지원합니다. 자격 증명은 호출자가 제공하며 저장하지 않습니다.
/// PUT 성공 응답 후 GET으로 전체 해시를 검증합니다. 서버 자체의 영구 보존/백업은 보장하지 않습니다.
/// </summary>
public sealed class WebDavColdStorageBackend : IColdStorageBackend, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri collection;
    private readonly TimeSpan transferTimeout;
    private readonly long maximumObjectBytes;
    public string BackendId { get; }
    public string? LocalRoot => null;
    public bool HasRemoteServerAcknowledgement => true;

    public WebDavColdStorageBackend(Uri collectionUri, string backendId, NetworkCredential credentials, TimeSpan? timeout = null, long maximumObjectBytes = 1024L * 1024 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(collectionUri);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        if (string.IsNullOrWhiteSpace(credentials.UserName) || credentials.UserName.Contains(':')) throw new ArgumentException("Basic 인증 사용자 이름이 비어 있거나 콜론을 포함합니다.", nameof(credentials));
        transferTimeout = timeout ?? TimeSpan.FromMinutes(30);
        if (transferTimeout <= TimeSpan.Zero || transferTimeout.TotalMilliseconds > uint.MaxValue - 1 || maximumObjectBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(timeout), "전송 제한 시간/최대 파일 크기가 잘못되었습니다.");
        this.maximumObjectBytes = maximumObjectBytes;
        if (!collectionUri.IsAbsoluteUri || collectionUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(collectionUri.UserInfo) || !string.IsNullOrEmpty(collectionUri.Query) || !string.IsNullOrEmpty(collectionUri.Fragment))
            throw new StorageException(StorageError.InvalidPath, "인증 정보, 쿼리 및 프래그먼트가 없는 HTTPS WebDAV 컬렉션 URL이 필요합니다.");
        collection = new Uri(collectionUri.AbsoluteUri.TrimEnd('/') + "/");
        BackendId = backendId;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None };
        // ResponseHeadersRead 이후 본문까지 동일한 제한 시간을 적용합니다.
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // 시스템 로그인/OAuth 또는 환경의 자격 증명을 조회하지 않습니다.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
    }

    private Uri ObjectUri(string key) => new(collection, Safety.Key(key));

    public async Task UploadAsync(string key, Stream source, TransferDigest expected, CancellationToken cancellationToken = default)
    {
        if (expected.Size < 0 || expected.Size > maximumObjectBytes) throw new StorageException(StorageError.TransferFailed, "WebDAV 전송 크기가 허용 범위를 초과했습니다.");
        using var timeout = TransferCancellation(cancellationToken);
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(key));
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Content = new StreamContent(new NonOwningStream(source), 128 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = expected.Size;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new StorageException(StorageError.Collision, "WebDAV 대상이 존재하거나 컬렉션을 사용할 수 없습니다.");
        // 202는 아직 수신 완료가 아니므로 제거 전제 조건으로 인정하지 않습니다.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent))
            throw new StorageException(StorageError.TransferFailed, $"WebDAV 전송이 확인되지 않았습니다. HTTP {(int)response.StatusCode}");
    }

    public async Task<TransferDigest> VerifyAsync(string key, CancellationToken cancellationToken = default)
    {
        using var timeout = TransferCancellation(cancellationToken);
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(key));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        CheckDownload(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var actual = await Safety.DigestAsync(stream, ct: cancellationToken, maximumBytes: maximumObjectBytes).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long size && actual.Size != size)
            throw new StorageException(StorageError.HashMismatch, "WebDAV 응답 길이가 실제 읽은 길이와 다릅니다.");
        return actual;
    }

    public async Task DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default)
    {
        using var timeout = TransferCancellation(cancellationToken);
        cancellationToken = timeout.Token;
        using var response = await client.GetAsync(ObjectUri(key), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        CheckDownload(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await Safety.DigestAsync(stream, destination, cancellationToken, maximumObjectBytes).ConfigureAwait(false);
    }

    private void CheckDownload(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0)
            throw new StorageException(StorageError.TransferFailed, $"WebDAV 전체 파일 읽기가 실패했습니다. HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength is long size && size > maximumObjectBytes)
            throw new StorageException(StorageError.HashMismatch, "WebDAV 응답이 최대 파일 크기를 초과했습니다.");
    }

    private CancellationTokenSource TransferCancellation(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(transferTimeout);
        return timeout;
    }

    public void Dispose() => client.Dispose();

    private sealed class NonOwningStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
