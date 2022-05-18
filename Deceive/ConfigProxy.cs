using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbedIO;
using EmbedIO.Actions;

namespace Deceive
{
    public class ConfigProxy
    {
        private readonly HttpClient _client = new HttpClient();

        /**
         * Starts a new client configuration proxy at a random port. The proxy will modify any responses
         * to point the chat servers to our local setup. This function returns the random port that the HTTP
         * server is listening on.
         */
        public ConfigProxy(string configUrl, int chatPort)
        {
            // Find a free port.
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            // Start a web server that sends everything to ProxyAndRewriteResponse
            var server = new WebServer(o => o
                    .WithUrlPrefix("http://127.0.0.1:" + port)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get,
                    ctx => ProxyAndRewriteResponse(configUrl, chatPort, ctx)));

            // Run this on a new thread, just for the sake of it.
            // It seemed to be buggy if run on the same thread.
            var thread = new Thread(() => { server.RunAsync().Wait(); }) {IsBackground = true};
            thread.Start();

            ConfigPort = port;
        }

        public int ConfigPort { get; }

        public event EventHandler<ChatServerEventArgs> PatchedChatServer;

        /**
         * Proxies any request made to this web server to the clientconfig service. Rewrites the response
         * to have any chat servers point to localhost at the specified port.
         */
        private async Task ProxyAndRewriteResponse(string configUrl, int chatPort, IHttpContext ctx)
        {
            string url = configUrl + ctx.Request.RawUrl;

            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            // Cloudflare bitches at us without a user agent.
            message.Headers.TryAddWithoutValidation("User-Agent", ctx.Request.Headers["user-agent"]);

            // Add authorization headers for player config.
            if (ctx.Request.Headers["x-riot-entitlements-jwt"] != null)
                message.Headers.TryAddWithoutValidation("X-Riot-Entitlements-JWT",
                    ctx.Request.Headers["x-riot-entitlements-jwt"]);

            if (ctx.Request.Headers["authorization"] != null)
                message.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers["authorization"]);

            var result = await _client.SendAsync(message);
            string content = await result.Content.ReadAsStringAsync();
            string modifiedContent = content;
            Trace.WriteLine("ORIGINAL CLIENTCONFIG: " + content);

            if (!result.IsSuccessStatusCode)
                goto RESPOND;

            try
            {
                var configObject = JsonSerializer.Deserialize<JsonNode>(content);

                string riotChatHost = null;
                int riotChatPort = 0;

                // Set fallback host to localhost.
                if (configObject?["chat.host"] != null)
                {
                    // Save fallback host
                    riotChatHost = configObject["chat.host"].GetValue<string>();
                    configObject["chat.host"] = "127.0.0.1";
                }

                // Set chat port.
                if (configObject?["chat.port"] != null)
                {
                    riotChatPort = configObject["chat.port"].GetValue<int>();
                    configObject["chat.port"] = chatPort;
                }

                // Set chat.affinities (a dictionary) to all localhost.
                if (configObject?["chat.affinities"] != null)
                {
                    var affinities = configObject["chat.affinities"];
                    if (configObject["chat.affinity.enabled"]?.GetValue<bool>() ?? false)
                    {
                        var pasRequest = new HttpRequestMessage(HttpMethod.Get,
                            "https://riot-geo.pas.si.riotgames.com/pas/v1/service/chat");
                        pasRequest.Headers.TryAddWithoutValidation("Authorization",
                            ctx.Request.Headers["authorization"]);
                        string pasJwt = await (await _client.SendAsync(pasRequest)).Content.ReadAsStringAsync();
                        Trace.WriteLine("PAS JWT:" + pasJwt);
                        string pasJwtContent = pasJwt.Split('.')[1];
                        string validBase64 =
                            pasJwtContent.PadRight(
                                pasJwtContent.Length / 4 * 4 + (pasJwtContent.Length % 4 == 0 ? 0 : 4), '=');
                        string pasJwtString = Encoding.UTF8.GetString(Convert.FromBase64String(validBase64));
                        var pasJwtJson = JsonSerializer.Deserialize<JsonNode>(pasJwtString);
                        string affinity = pasJwtJson?["affinity"]?.GetValue<string>();
                        if (affinity != null)
                        {
                            riotChatHost = affinities?[affinity]?.GetValue<string>();
                            Trace.WriteLine($"AFFINITY: {affinity} -> {riotChatHost}");
                        }
                    }

                    affinities?.AsObject().Select(pair => pair.Key).ToList().ForEach(s => affinities[s] = "127.0.0.1");
                }

                // Allow an invalid cert.
                if (configObject?["chat.allow_bad_cert.enabled"] != null)
                    configObject["chat.allow_bad_cert.enabled"] = true;

                modifiedContent = JsonSerializer.Serialize(configObject);
                Trace.WriteLine("MODIFIED CLIENTCONFIG: " + modifiedContent);

                if (riotChatHost != null && riotChatPort != 0)
                    PatchedChatServer?.Invoke(this,
                        new ChatServerEventArgs {ChatHost = riotChatHost, ChatPort = riotChatPort});
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);

                // Show a message instead of failing silently.
                MessageBox.Show(
                    "Deceive was unable to rewrite a League of Legends configuration file. This normally happens because Riot changed something on their end. " +
                    "Please check if there's a new version of Deceive available, or contact the creator through GitHub (https://github.com/molenzwiebel/deceive) or Discord if there's not.\n\n" +
                    ex,
                    StartupHandler.DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );

                Application.Exit();
            }

            // Using the builtin EmbedIO methods for sending the response adds some garbage in the front of it.
            // This seems to do the trick.
            RESPOND:
            byte[] responseBytes = Encoding.UTF8.GetBytes(modifiedContent);

            ctx.Response.StatusCode = (int)result.StatusCode;
            ctx.Response.SendChunked = false;
            ctx.Response.ContentLength64 = responseBytes.Length;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(responseBytes);
            ctx.Response.OutputStream.Close();
        }

        public class ChatServerEventArgs : EventArgs
        {
            public string ChatHost { get; set; }
            public int ChatPort { get; set; }
        }
    }
}
