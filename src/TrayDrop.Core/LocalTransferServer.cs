using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TrayDrop.Core;

public sealed record ServerOptions(string DataDirectory, int Port = 45823, IPAddress? BindAddress = null)
{
    public string ComputerName { get; init; } = Environment.MachineName;
    public string? ReceiveDirectory { get; init; }
}

public sealed class LocalTransferServer : IAsyncDisposable
{
    private sealed record PairRequest(string? Code, string? Name);
    private sealed record RenameRequest(string? Name);
    private readonly WebApplication _app;
    private readonly TransferState _state;
    private readonly ServerOptions _options;
    private readonly ILogger _logger;

    public LocalTransferServer(
        TransferState state, ServerOptions options, ILoggerProvider? loggerProvider = null,
        Func<IReadOnlyList<LanAddress>>? networkProvider = null)
    {
        _state = state;
        _options = options;
        var getNetworks = networkProvider ?? NetworkDiscovery.GetAddresses;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LocalTransferServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
            builder.Logging.AddProvider(loggerProvider);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Listen(options.BindAddress ?? IPAddress.Any, options.Port);
            kestrel.Limits.MaxRequestBodySize = null;
        });
        builder.Services.Configure<RouteHandlerOptions>(settings => settings.ThrowOnBadRequest = true);
        builder.Services.AddProblemDetails();
        builder.Services.AddRateLimiter(settings =>
        {
            settings.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            settings.OnRejected = async (context, cancellationToken) =>
                await Results.Problem(
                    title: "Too many connection attempts", detail: "Wait one minute before connecting again.",
                    statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext).ConfigureAwait(false);
        });
        _app = builder.Build();
        _logger = _app.Logger;
        Uploads = new UploadService(state, options.DataDirectory,
            _app.Services.GetRequiredService<ILogger<UploadService>>(), options.ReceiveDirectory);

        _app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var badRequest = error as BadHttpRequestException;
            if (badRequest is not null)
                _logger.LogWarning(error, "Rejected an invalid API request.");
            else
                _logger.LogError(error, "Unexpected local transfer server error.");
            await Results.Problem(
                title: badRequest is null ? "Transfer server error" : "Invalid request",
                detail: badRequest?.Message ?? "Check TrayDrop's log on your PC, then try again.",
                statusCode: badRequest?.StatusCode ?? StatusCodes.Status500InternalServerError)
                .ExecuteAsync(context).ConfigureAwait(false);
        }));
        _app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            var networks = getNetworks();
            if (!NetworkDiscovery.IsAllowedClient(context.Connection.RemoteIpAddress, networks) ||
                !NetworkDiscovery.IsAllowedHost(context.Request.Host.Host, networks))
            {
                _logger.LogWarning("Rejected a request outside the local network or with an invalid Host header.");
                await Problem(403, "Local network only", "Use the local Wi-Fi link shown in TrayDrop.")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
                context.Request.Headers.TryGetValue("Origin", out var origin) &&
                (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) ||
                 !uri.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase) ||
                 !uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Rejected an API request from a different browser origin.");
                await Problem(403, "Invalid origin", "Send files from TrayDrop's own phone page.")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.Path != "/api/uploads" &&
                context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = 64 * 1024;
            await next(context).ConfigureAwait(false);
        });
        _app.UseRateLimiter();
        var html = ReadPhoneHtml();
        _app.MapGet("/", () => Results.Text(html, "text/html", Encoding.UTF8));
        _app.MapPost("/api/pair", (PairRequest request) =>
        {
            try
            {
                return Results.Ok(state.Pair(request.Code, request.Name));
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogWarning("Rejected a pairing attempt with an invalid code.");
                return Problem(401, "Connection code expired or invalid", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return Problem(400, "Invalid device name", exception.Message);
            }
        }).RequireRateLimiting("pairing");
        _app.MapGet("/api/state", (HttpContext context) =>
        {
            var device = Authenticate(context);
            if (device is null)
                return Unauthorized();
            try
            {
                return Results.Ok(state.GetPhoneState(device.Id, options.ComputerName));
            }
            catch (KeyNotFoundException)
            {
                return Unauthorized();
            }
        });
        _app.MapPatch("/api/device", (HttpContext context, RenameRequest request) =>
        {
            var device = Authenticate(context);
            if (device is null)
                return Unauthorized();
            try
            {
                return Results.Ok(state.RenameDevice(device.Id, request.Name));
            }
            catch (ArgumentException exception)
            {
                return Problem(400, "Invalid device name", exception.Message);
            }
            catch (KeyNotFoundException)
            {
                return Unauthorized();
            }
        });
        _app.MapDelete("/api/device", (HttpContext context) =>
        {
            var device = Authenticate(context);
            if (device is null)
                return Unauthorized();
            state.RemoveDevice(device.Id);
            return Results.NoContent();
        });
        _app.MapMethods("/api/downloads/{id:guid}", ["GET", "HEAD"], Download);
        _app.MapPost("/api/uploads", (Func<HttpContext, Task<IResult>>)UploadAsync);
    }

    public UploadService Uploads { get; }
    public int Port { get; private set; }
    public Uri LoopbackUrl => new($"http://127.0.0.1:{Port}/");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var boundAddress = addresses?.FirstOrDefault() ??
                           throw new InvalidOperationException("The local transfer server did not report a listening address.");
        Port = new Uri(boundAddress).Port;
        _logger.LogInformation("TrayDrop's local server is listening on port {Port}.", Port);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning("Shutdown timed out. Active transfers will be cancelled.");
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DeviceInfo? Authenticate(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        return _state.TryAuthenticate(token, out var device) ? device : null;
    }

    private IResult Download(Guid id, HttpContext context)
    {
        try
        {
            var download = _state.OpenDownload(id, context.Request.Query["key"].ToString());
            if (HttpMethods.IsGet(context.Request.Method))
                _state.RecordDownloadRequest(id);
            return Results.File(download.Stream, "application/octet-stream", download.Name, enableRangeProcessing: true);
        }
        catch (InvalidDownloadException exception)
        {
            return Problem(404, "Download unavailable", exception.Message);
        }
        catch (SharedFileChangedException exception)
        {
            return Problem(409, "File changed", exception.Message);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning(exception, "An original shared file is no longer available.");
            return Problem(410, "File moved or deleted", "Drop the file in TrayDrop again from its current location.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not open a shared file for download.");
            return Problem(503, "Cannot read this file", "Close any app locking the file, check its permissions, and try again.");
        }
    }

    private async Task<IResult> UploadAsync(HttpContext context)
    {
        var device = Authenticate(context);
        if (device is null)
            return Unauthorized();
        try
        {
            if (context.Request.Query["name"].Count != 1)
                return Problem(400, "Missing filename", "Select a file to upload.");
            var received = await Uploads.SaveAsync(
                device, context.Request.Query["name"].ToString(), context.Request.Body,
                context.Request.ContentLength, context.RequestAborted).ConfigureAwait(false);
            _logger.LogInformation("Received upload {Id} from device {DeviceId}.", received.Id, device.Id);
            return Results.Json(new { received.Id, received.Name, received.Size, received.ReceivedAt }, statusCode: 201);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            _logger.LogWarning("Rejected an invalid or incomplete upload: {Reason}", exception.Message);
            return Problem(400, "Upload rejected", exception.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("An upload was cancelled.");
            return Problem(499, "Upload cancelled", "The file was not saved. Please send it again.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Could not save an incoming upload.");
            return Problem(507, "Could not save the upload",
                "Check free disk space and folder permissions on your PC, then try again.");
        }
    }

    private static IResult Unauthorized() =>
        Problem(401, "Connect this device first", "Scan the current QR code in TrayDrop to connect again.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static string ReadPhoneHtml()
    {
        using var stream = typeof(LocalTransferServer).Assembly.GetManifestResourceStream("TrayDrop.Core.Web.phone.html")
                           ?? throw new InvalidOperationException("The embedded phone page is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
