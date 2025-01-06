using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        public static List<string> NonChatApiPathList = new List<string>
        {
            "/api/generate", "/api/chat", "/api/tags", "/api/embed", "/api/show", "/api/ps", "/api/embeddings"
        };

        public static string TargetApiUrl = "http://127.0.0.1:11434";
        public static string ActionApiUrl = "http://127.0.0.1:11434";

        public static bool UseToken = false;
        public static bool UseModelReplace = true;
        public static bool UseRiskModel = true;

        public static string RiskModel = "shieldgemma:2b";
        public static string RiskModelPrompt = string.Empty;

        public static Dictionary<string, string> ModelReplaceDictionary = new()
        {
            {"gpt-3.5-turbo", "qwen2.5:7b"}
        };

        public static List<string> TokenList = ["sk-test-token"];

        public static List<string> RiskKeywordsList = ["YES", "UNSAFE"];


        public static OllamaApiClient OllamaApi = new OllamaApiClient(new Uri(ActionApiUrl));


        static void Main(string[] args)
        {
            try
            {
                //var configurationRoot = new ConfigurationBuilder()
                //    .AddEnvironmentVariables()
                //    .Build();
                //if (!configurationRoot.GetSection("MondrianGateway").Exists()) return;
                //var config = configurationRoot.GetSection("MondrianGateway");

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
                            var reqToken = context.Request.Headers.ContainsKey("Authorization")
                                ? context.Request.Headers.Authorization.ToString().Split(' ').Last().ToString()
                                : string.Empty;

                            if (UseToken && !TokenList.Contains(reqToken))
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
                                context.Items["Token"] = reqToken;
                                await next.Invoke();
                            }
                        });

                        foreach (var path in NonChatApiPathList)
                            app.Map(path, svr =>
                            {
                                svr.RunProxy(async context =>
                                {
                                    var response = new HttpResponseMessage();
                                    try
                                    {
                                        response = await context.ForwardTo(new Uri(TargetApiUrl + path)).Send();
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

                        app.Map("/v1", HandleOpenaiStyleChat);

                    }).Build();

                host.Run();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void HandleOpenaiStyleChat(IApplicationBuilder svr)
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

                        if (jBody.ContainsKey("model") && UseModelReplace &&
                            ModelReplaceDictionary.TryGetValue(jBody["model"]?.ToString()!, out var newModel))
                            jBody["model"] = newModel;

                        Console.WriteLine(jBody.ToString());

                        if (jBody.ContainsKey("messages"))
                        {
                            var msgs = jBody["messages"]?.ToObject<List<Message>>();
                            if (msgs != null && msgs.Any())
                            {
                                if (UseRiskModel)
                                {
                                    if (!string.IsNullOrWhiteSpace(RiskModelPrompt))
                                    {
                                        msgs.RemoveAll(x => x.Role == ChatRole.System);
                                        msgs.Add(new Message { Role = ChatRole.System, Content = RiskModelPrompt });
                                    }

                                    await foreach (var res in OllamaApi.ChatAsync(new ChatRequest()
                                                       { Model = RiskModel, Messages = msgs, Stream = false }))
                                    {
                                        var risks = res?.Message.Content?.ToUpper();
                                        Console.WriteLine(risks);
                                        if (risks != null && RiskKeywordsList.Any(x => risks.Contains(x)))
                                        {
                                            return new HttpResponseMessage(HttpStatusCode.UnavailableForLegalReasons)
                                            {
                                                Content = new StringContent(new JObject
                                                {
                                                    {
                                                        "error",
                                                        new JObject
                                                        {
                                                            {
                                                                "message",
                                                                "Messages with content security risks. Unable to continue."
                                                            },
                                                            {"type", "content_risks"}, {"risk_model", res?.Model},
                                                            {"risk_raw_msg", res?.Message.Content}
                                                        }
                                                    }
                                                }.ToString())
                                            };
                                        }
                                    }
                                }
                            }
                        }

                        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jBody.ToString()));
                        context.Request.ContentLength = context.Request.Body.Length;
                    }

                    response = await context.ForwardTo(new Uri(TargetApiUrl + "/v1")).Send();
                    response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");
                    return response;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    response = await context.ForwardTo(new Uri(TargetApiUrl + "/v1")).Send();
                    return response;
                }
            });
        }
    }
}
