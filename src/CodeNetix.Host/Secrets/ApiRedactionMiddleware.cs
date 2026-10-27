using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Host.Secrets;

/// <summary>
/// Reescribe los cuerpos de respuesta de <c>/api</c> pasándolos por <see cref="ISecretRedactor"/>.
/// Los endpoints no redactan: este es el sumidero de la API.
/// </summary>
internal sealed class ApiRedactionMiddleware(RequestDelegate next, ISecretRedactor redactor)
{
    public const string ApiPrefix = "/api";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (buffer.Length == 0)
        {
            return;
        }

        var bytes = buffer.ToArray();
        if (IsText(context.Response.ContentType))
        {
            var text = Encoding.UTF8.GetString(bytes);
            var redacted = redactor.Redact(text);
            if (!string.Equals(text, redacted, StringComparison.Ordinal))
            {
                bytes = Encoding.UTF8.GetBytes(redacted);
            }
        }

        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted);
    }

    private static bool IsText(string? contentType)
    {
        // Sin Content-Type se trata como texto: es más seguro redactar de más.
        if (string.IsNullOrEmpty(contentType))
        {
            return true;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
    }
}
