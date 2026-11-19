using Microsoft.JSInterop;

namespace Stampd.UI.Services;

/// <summary>
/// Hands bytes the server fetched (with the API's bearer token) to the browser as a file
/// download, via <c>wwwroot/js/file-download.js</c>.
/// </summary>
public sealed class FileDownloader : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public FileDownloader(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        _js = js;
    }

    public async Task SaveAsync(string fileName, string contentType, byte[] content, CancellationToken ct)
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ct, "/js/file-download.js").ConfigureAwait(false);
        using var stream = new MemoryStream(content, writable: false);
        using var streamRef = new DotNetStreamReference(stream);
        await _module.InvokeVoidAsync("saveFile", ct, fileName, contentType, streamRef).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone; the browser already dropped the module.
            }
        }
    }
}
