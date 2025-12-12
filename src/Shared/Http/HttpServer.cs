namespace Shared.Http;

using Shared.Config;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

public abstract class HttpServer
{
    protected HttpRouter router;
    protected HttpListener server;

    public HttpServer()
    {
        router = new HttpRouter();
        Init(); // deja que las clases derivadas configuren rutas/middlewares

        string host = Configuration.Get<string>("HOST", "http://127.0.0.1");
        string port = Configuration.Get<string>("PORT", "5000");

        // Asegura formato correcto del prefijo y la barra final
        string authority = $"{host.TrimEnd('/')}" +
                           $":{port.TrimStart(':')}/";

        server = new HttpListener();
        server.Prefixes.Add(authority);
        Console.WriteLine("Server started at " + authority);
    }

    // Configura rutas y middlewares (lo implementa tu clase concreta)
    public abstract void Init();

    public async Task Start(CancellationToken cancellationToken = default)
    {
        server.Start();
        Console.WriteLine("Listening... (Ctrl+C para detener)");

        try
        {
            while (server.IsListening && !cancellationToken.IsCancellationRequested)
            {
                // GetContextAsync no acepta CancellationToken,
                // se interrumpe al llamar Stop/Close en Stop()
                var ctx = await server.GetContextAsync().ConfigureAwait(false);
                _ = router.HandleContextAsync(ctx); // fire-and-forget del manejo
            }
        }
        catch (HttpListenerException)
        {
            // se lanza al hacer Stop/Close mientras espera; lo ignoramos
        }
        catch (ObjectDisposedException)
        {
            // si ya está dispuesto al salir, lo ignoramos
        }
        finally
        {
            if (server.IsListening)
            {
                server.Stop();
            }
        }
    }

    public void Stop()
    {
        if (server != null)
        {
            try
            {
                if (server.IsListening) server.Stop();
                server.Close();
            }
            finally
            {
                Console.WriteLine("Server stopped.");
            }
        }
    }
}
