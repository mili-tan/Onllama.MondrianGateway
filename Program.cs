using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using ProxyKit;


namespace Onllama.MondrianGateway
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var ollama = new OllamaApiClient(new Uri("http://localhost:11434"));
                var paths = new List<string>
                {
                    "/api/generate", "/api/chat", "/api/tags", "/api/embed", "/api/show", "/api/ps", "/api/embeddings"
                };

                var host = new WebHostBuilder()
                    .UseKestrel()
                    .UseContentRoot(AppDomain.CurrentDomain.SetupInformation.ApplicationBase)
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddProxy(httpClientBuilder =>
                            httpClientBuilder.ConfigureHttpClient(client =>
                                client.Timeout = TimeSpan.FromMinutes(5)));
                    })
                    .ConfigureKestrel(options =>
                    {
                        options.ListenAnyIP(18080,
                            listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            var token = context.Request.Headers.ContainsKey("Authorization")
                                ? context.Request.Headers.Authorization.ToString().Split(' ').Last().ToString()
                                : string.Empty;
                            //if (token != "sk-")
                            if (false)
                            {
                                context.Response.Headers.ContentType = "application/json";
                                context.Response.StatusCode = (int) HttpStatusCode.Forbidden;
                                await context.Response.WriteAsync(new JObject()
                                {
                                    {
                                        "error", new JObject()
                                        {
                                            {
                                                "message",
                                                "Authentication Fails, You need to provide a valid API key in the Authorization header using Bearer authentication (i.e. Authorization: Bearer YOUR_KEY)."
                                            },
                                            {"type", "invalid_request_error"}
                                        }
                                    }
                                }.ToString());
                            }
                            else
                            {
                                context.Items["Token"] = token;
                                await next.Invoke();
                            }
                        });

                        foreach (var path in paths)
                            app.Map(path, svr =>
                            {
                                svr.RunProxy(async context =>
                                {
                                    var response = new HttpResponseMessage();
                                    try
                                    {
                                        response = await context.ForwardTo(new Uri("http://127.0.0.1:11434" + path))
                                            .Send();
                                        response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");
                                        return response;
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                        return response;
                                    }
                                });
                            });

                        app.Map("/v1", svr =>
                        {
                            svr.RunProxy(async context =>
                            {
                                HttpResponseMessage response;
                                try
                                {
                                    if (context.Request.Method.ToUpper() == "POST")
                                    {
                                        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                                        var jBody = JObject.Parse(body);

                                        if (jBody["model"]?.ToString() == "gpt-3.5-turbo") jBody["model"] = "qwen2.5:7b";
                                        Console.WriteLine(jBody.ToString());

                                        var msgs = jBody["messages"]?.ToObject<List<Message>>();
                                        await foreach (var res in ollama.ChatAsync(new ChatRequest()
                                                           {Model = "shieldgemma:2b", Messages = msgs, Stream = false}))
                                        {
                                            Console.WriteLine(res?.Message.Content);
                                            var risks = res?.Message.Content?.ToLower();
                                            if (risks != null && (risks.Contains("yes") || risks.Contains("unsafe")))
                                            {
                                                return new HttpResponseMessage(HttpStatusCode.UnavailableForLegalReasons)
                                                {
                                                    Content = new StringContent(new JObject()
                                                    {
                                                        {
                                                            "error", new JObject()
                                                            {
                                                                {"message", "Messages with content security risks. Unable to continue."},
                                                                {"type", "content_risks"},
                                                                {"risk_model", res?.Model},
                                                                {"risk_raw_msg", res?.Message.Content}
                                                            }
                                                        }
                                                    }.ToString())
                                                };
                                            }
                                        }

                                        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jBody.ToString()));
                                        context.Request.ContentLength = context.Request.Body.Length;
                                    }

                                    response = await context.ForwardTo(new Uri("http://127.0.0.1:11434/v1")).Send();
                                    response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");
                                    return response;
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e);
                                    response = await context.ForwardTo(new Uri("http://127.0.0.1:11434/v1")).Send();
                                    return response;
                                }
                            });
                        });
                    }).Build();

                host.Run();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
