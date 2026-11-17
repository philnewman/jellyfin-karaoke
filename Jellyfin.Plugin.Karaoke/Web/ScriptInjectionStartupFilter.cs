using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Karaoke.Web;

/// <summary>
/// Puts <see cref="ScriptInjectionMiddleware"/> at the front of the request pipeline.
/// </summary>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds a script tag for the karaoke client script to the web client's index.html
/// as it is served. Nothing on disk is modified.
/// </summary>
public class ScriptInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var webIndex = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        var tail = webIndex < 0 ? null : path.Substring(webIndex);

        var isIndex = HttpMethods.IsGet(context.Request.Method)
            && tail is not null
            && (tail.Equals("/web/", StringComparison.OrdinalIgnoreCase)
                || tail.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase));

        if (!isIndex || Plugin.Instance?.Configuration.InjectClientScript != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for an uncompressed, unconditional copy so the HTML can be edited.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        var contentType = context.Response.ContentType ?? string.Empty;

        if (context.Response.StatusCode == StatusCodes.Status200OK
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var html = Encoding.UTF8.GetString(bytes);
                var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (headEnd >= 0 && !html.Contains("Karaoke/ClientScript", StringComparison.Ordinal))
                {
                    var prefix = path.Substring(0, webIndex);
                    var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
                    var tag = "<script defer src=\"" + prefix + "/Karaoke/ClientScript?v=" + version + "\"></script>";
                    html = html.Insert(headEnd, tag);
                    bytes = Encoding.UTF8.GetBytes(html);
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Karaoke: could not add the client script to index.html");
            }
        }

        if (bytes.Length > 0)
        {
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
    }
}
