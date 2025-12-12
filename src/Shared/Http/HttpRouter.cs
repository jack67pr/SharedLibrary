namespace Shared.Http;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Threading.Tasks;
using System.Web;

public class HttpRouter
{
    public const int RESPONSE_NOT_SENT = 777;
    private static ulong requestId = 0;
    private string basePath;
    private List<HttpMiddleware> middlewares;
    private List<(string method, string path, HttpMiddleware[] mw)> routes;

    public HttpRouter()
    {
        basePath = string.Empty;
        middlewares = new List<HttpMiddleware>();
        routes = new List<(string, string, HttpMiddleware[])>();
    }

    // --- NUEVO: normalizador de comillas/rutas ---
    private static string NormalizeRoutePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "/";

        // Reemplaza comillas "inteligentes" por ASCII
        var s = raw
            .Replace('“', '"').Replace('”', '"')
            .Replace('‘', '\'').Replace('’', '\'');

        s = s.Trim();

        // Si viene rodeada por comillas simples, dobles o backticks, quítalas
        if (s.Length >= 2)
        {
            char a = s[0], b = s[^1];
            if ((a == '"' && b == '"') || (a == '\'' && b == '\'') || (a == '`' && b == '`'))
                s = s.Substring(1, s.Length - 2).Trim();
        }

        // Normaliza slashes (líder) y quita dobles
        s = "/" + s.Trim().TrimStart('/');

        return s;
    }

    // Middleware global
    public HttpRouter Use(params HttpMiddleware[] middlewares)
    {
        this.middlewares.AddRange(middlewares);
        return this;
    }

    public HttpRouter Map(string method, string path, params HttpMiddleware[] middlewares)
    {
        // 👈 Normaliza aquí
        var clean = NormalizeRoutePath(path);
        routes.Add((method.ToUpperInvariant(), clean, middlewares));
        return this;
    }

    public HttpRouter MapGet(string path, params HttpMiddleware[] middlewares) => Map("GET", path, middlewares);
    public HttpRouter MapPost(string path, params HttpMiddleware[] middlewares) => Map("POST", path, middlewares);
    public HttpRouter MapPut(string path, params HttpMiddleware[] middlewares) => Map("PUT", path, middlewares);
    public HttpRouter MapDelete(string path, params HttpMiddleware[] middlewares) => Map("DELETE", path, middlewares);

    public async Task HandleContextAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var props = new Hashtable();
        res.StatusCode = RESPONSE_NOT_SENT;
        props["req.id"] = ++requestId;
        try
        {
            await HandleAsync(req, res, props, () => Task.CompletedTask);
        }
        finally
        {
            if (res.StatusCode == RESPONSE_NOT_SENT)
                res.StatusCode = (int)HttpStatusCode.NotImplemented;

            res.Close();
        }
    }

    private async Task HandleAsync(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
    {
        Func<Task> globalMiddlewarePipeline = GenerateMiddlewarePipeline(req, res, props, middlewares);
        await globalMiddlewarePipeline();
        await next();
    }

    public HttpRouter UseRouter(string path, HttpRouter router)
    {
        // 👈 Normaliza el path base del subrouter
        router.basePath = NormalizeRoutePath(this.basePath) + NormalizeRoutePath(path);
        return Use(router.HandleAsync);
    }

    private Func<Task> GenerateMiddlewarePipeline(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, List<HttpMiddleware> middlewares)
    {
        int index = -1;
        Func<Task> next = () => Task.CompletedTask;

        next = async () =>
        {
            index++;
            if (index < middlewares.Count && res.StatusCode == RESPONSE_NOT_SENT)
                await middlewares[index](req, res, props, next);
        };
        return next;
    }

    public HttpRouter UseSimpleRouteMatching() => Use(SimpleRouteMatching);
    public HttpRouter UseParametrizedRouteMatching() => Use(ParametrizedRouteMatching);

    private async Task SimpleRouteMatching(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
    {
        var abs = req.Url!.AbsolutePath;
        foreach (var (method, path, mw) in routes)
        {
            if (req.HttpMethod == method &&
                string.Equals(abs, NormalizeRoutePath(basePath) + path, StringComparison.Ordinal))
            {
                var pipeline = GenerateMiddlewarePipeline(req, res, props, mw.ToList());
                await pipeline();
                break; // o return;
            }
        }
        await next();
    }

    private async Task ParametrizedRouteMatching(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
    {
        var abs = req.Url!.AbsolutePath;
        foreach (var (method, path, mw) in routes)
        {
            NameValueCollection? parameters;
            var routeFull = NormalizeRoutePath(basePath) + path;
            if (req.HttpMethod == method &&
                (parameters = ParseUrlParams(abs, routeFull)) != null)
            {
                props["req.params"] = parameters;
                var pipeline = GenerateMiddlewarePipeline(req, res, props, mw.ToList());
                await pipeline();
                break; // o return;
            }
        }
        await next();
    }

    public static NameValueCollection? ParseUrlParams(string uPath, string rPath)
    {
        string[] uParts = uPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] rParts = rPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uParts.Length != rParts.Length) return null;

        var parameters = new NameValueCollection();
        for (int i = 0; i < rParts.Length; i++)
        {
            string uPart = uParts[i];
            string rPart = rParts[i];
            if (rPart.StartsWith(":", StringComparison.Ordinal))
            {
                string paramName = rPart.Substring(1);
                parameters[paramName] = HttpUtility.UrlDecode(uPart);
            }
            else if (!string.Equals(uPart, rPart, StringComparison.Ordinal))
            {
                return null;
            }
        }
        return parameters;
    }
}
