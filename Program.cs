using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jitbit.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using ProxyKit;

namespace Onllama.MondrianGateway
{
    internal class Program
    {
        public static List<string> NonChatApiPathList =
            ["/api/generate", "/api/chat", "/api/tags", "/api/embed", "/api/show", "/api/ps", "/api/embeddings"];

        public static string TargetApiUrl = "http://127.0.0.1:11434";
        public static string ActionApiUrl = "http://127.0.0.1:11434";

        public static bool UseToken = false;
        public static bool UseTokenReplace = false;
        public static bool UseModelReplace = false;
        public static bool UseRiskModel = false;
        public static bool UseSystemPromptTrim = false;
        public static bool UseSystemPromptInject = false;

        //public static string RedisDBStr = File.ReadAllText("redis.text").Trim();
        //public static ConnectionMultiplexer RedisConnection;
        //public static IDatabase RedisDatabase;

        public static string ReplaceTokenMode = "failback";
        public static List<string> ReplaceTokensList = ["sk-test"];

        public static string RiskModel = "shieldgemma:2b";
        public static string RiskModelPrompt = string.Empty;
        public static string SystemPrompt = "你是Bob，你是一个友好的助手。";

        public static Dictionary<string, string> ModelReplaceDictionary = new()
        {
            {"gpt-3.5-turbo", "qwen2.5:7b"}
        };

        public static List<string> TokensList = ["sk-test-token"];
        public static List<string> RiskKeywordsList = ["YES", "UNSAFE"];

        public static OllamaApiClient OllamaApi = new OllamaApiClient(new Uri(ActionApiUrl));

        //public static Dictionary<HashSet<string>, List<Message>> HashsDictionary = new();
        public static FastCache<Guid, string> MsgThreads = new FastCache<Guid, string>();

        public static MsgContext MyMsgContext = new MsgContext();

        static void Main(string[] args)
        {
            try
            {
                MyMsgContext.Database.EnsureCreated();

                var configurationRoot = new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();

                if (configurationRoot.GetSection("MondrianGateway").Exists())
                {
                    var gateConfig = configurationRoot.GetSection("MondrianGateway");

                    if (gateConfig.GetSection("Tokens").Exists())
                    {
                        TokensList = gateConfig.GetSection("Tokens").Get<List<string>>() ?? [];
                        if (TokensList.Any()) UseToken = true;
                    }

                    if (gateConfig.GetSection("ReplaceTokens").Exists())
                    {
                        ReplaceTokensList = gateConfig.GetSection("ReplaceTokens").Get<List<string>>() ?? [];
                        if (ReplaceTokensList.Any()) UseTokenReplace = true;
                    }

                    if (gateConfig.GetSection("ModelReplace").Exists())
                    {
                        ModelReplaceDictionary =
                            gateConfig.GetSection("ModelReplace").Get<Dictionary<string, string>>() ??
                            new Dictionary<string, string>();
                        if (ModelReplaceDictionary.Any()) UseModelReplace = true;
                    }

                    if (gateConfig.GetSection("SystemPrompt").Exists())
                    {
                        SystemPrompt = gateConfig.GetValue<string>("SystemPrompt") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(SystemPrompt))
                        {
                            UseSystemPromptInject = true;
                            UseSystemPromptTrim = true;
                        }
                    }

                    if (gateConfig.GetSection("Risks").Exists())
                    {
                        var riskConfig = gateConfig.GetSection("Risks");
                        if (riskConfig.GetSection("RiskModel").Exists())
                            RiskModel = riskConfig.GetValue<string>("RiskModel") ?? string.Empty;
                        if (riskConfig.GetSection("RiskModelPrompt").Exists())
                            RiskModelPrompt = riskConfig.GetValue<string>("RiskModelPrompt") ?? string.Empty;
                        if (riskConfig.GetSection("RiskKeywords").Exists())
                            RiskKeywordsList = riskConfig.GetSection("RiskKeywords").Get<List<string>>() ?? [];
                        if (!string.IsNullOrWhiteSpace(RiskModel) && RiskKeywordsList.Any()) UseRiskModel = true;
                    }

                    if (gateConfig.GetSection("UseToken").Exists())
                        UseToken = gateConfig.GetValue<bool>("UseToken");
                    if (gateConfig.GetSection("UseModelReplace").Exists())
                        UseModelReplace = gateConfig.GetValue<bool>("UseModelReplace");
                    if (gateConfig.GetSection("UseTokenReplace").Exists())
                        UseTokenReplace = gateConfig.GetValue<bool>("UseTokenReplace");
                    if (gateConfig.GetSection("UseRiskModel").Exists())
                        UseRiskModel = gateConfig.GetValue<bool>("UseRiskModel");
                    if (gateConfig.GetSection("UseSystemPromptTrim").Exists())
                        UseSystemPromptTrim = gateConfig.GetValue<bool>("UseSystemPromptTrim");
                    if (gateConfig.GetSection("UseSystemPromptInject").Exists())
                        UseSystemPromptInject = gateConfig.GetValue<bool>("UseSystemPromptInject");

                    if (gateConfig.GetSection("ReplaceTokenMode").Exists())
                        ReplaceTokenMode = gateConfig.GetValue<string>("ReplaceTokenMode") ?? ReplaceTokenMode;
                    if (gateConfig.GetSection("TargetApiUrl").Exists())
                        TargetApiUrl = gateConfig.GetValue<string>("TargetApiUrl") ?? TargetApiUrl;
                    if (gateConfig.GetSection("ActionApiUrl").Exists())
                    {
                        ActionApiUrl = gateConfig.GetValue<string>("ActionApiUrl") ?? TargetApiUrl;
                        OllamaApi = new OllamaApiClient(new Uri(ActionApiUrl));
                    }
                }

                //RedisConnection = ConnectionMultiplexer.Connect(RedisDBStr);
                //RedisDatabase = RedisConnection.GetDatabase();

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
                            context.Connection.RemoteIpAddress = RealIP.Get(context);
                            await next(context);
                        });

                        app.Use(async (context, next) =>
                        {
                            var reqToken = context.Request.Headers.ContainsKey("Authorization")
                                ? context.Request.Headers.Authorization.ToString().Split(' ').Last().ToString()
                                : string.Empty;

                            if (UseToken && !TokensList.Contains(reqToken))
                            {
                                context.Response.Headers.ContentType = "application/json";
                                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                                await context.Response.WriteAsync(new JObject()
                                {
                                    {
                                        "error", new JObject
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

                        app.Use(async (context, next) =>
                        {
                            try
                            {
                                var hashesId = string.Empty;
                                var msgThreadId = Ulid.NewUlid().ToGuid();
                                var roundId = context.Request.Headers.TryGetValue("round_id", out var roundFromHeader)
                                    ? roundFromHeader.ToString()
                                    : string.Empty;

                                var strBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                                var jBody = new JObject();

                                if (context.Request.Method.ToUpper() == "POST")
                                    try
                                    {
                                        jBody = JObject.Parse(strBody);

                                        if (jBody.ContainsKey("model") && UseModelReplace &&
                                            ModelReplaceDictionary.TryGetValue(jBody["model"]?.ToString()!,
                                                out var newModel))
                                            jBody["model"] = newModel;

                                        if (jBody.ContainsKey("stream") && jBody["stream"]!.ToObject<bool>()) 
                                            jBody["stream_options"] = new JObject() {["include_usage"] = true};

                                        roundId = jBody.TryGetValue("round_id", out var roundFromBody)
                                            ? roundFromBody.ToString()
                                            : roundId;
                                        if (!string.IsNullOrEmpty(roundId)) jBody.Remove("round_id");

                                        if (jBody.ContainsKey("messages"))
                                        {
                                            var msgs = jBody["messages"]?.ToObject<List<Message>>();
                                            if (msgs != null && msgs.Any())
                                            {
                                                var lastMsg = msgs.Last();
                                                var inputMsg = lastMsg.Role + ":" + lastMsg.Content;

                                                if (true)
                                                {
                                                    var fnv = FNV1a.Create();
                                                    var hashes = new HashSet<string>();
                                                    foreach (var item in msgs.Where(item =>
                                                                 item.Role?.ToUpper() != "SYSTEM"))
                                                    {
                                                        hashes.Add(Convert
                                                            .ToBase64String(
                                                                fnv.ComputeHash(Encoding.UTF8.GetBytes(item.Content)))
                                                            .TrimEnd('='));
                                                    }

                                                    hashesId = string.Join(',', hashes.ToList());

                                                    if (!MyMsgContext.RequestHashesObjs.Any(x => x.Hashes == hashesId))
                                                    {
                                                        MyMsgContext.RequestHashesObjs.Add(new RequestHashesObj()
                                                        {
                                                            Hashes = hashesId,
                                                            RoundId = roundId,
                                                            Input = jBody.ToString(),
                                                        });
                                                    }

                                                    if (MsgThreads.Any(x => hashesId.StartsWith(x.Value)))
                                                        msgThreadId = MsgThreads
                                                            .FirstOrDefault(x => hashesId.StartsWith(x.Value)).Key;

                                                    MsgThreads.AddOrUpdate(msgThreadId, hashesId, TimeSpan.FromMinutes(15));

                                                    var msgThreadEntity = new MsgThreadEntity()
                                                    {
                                                        ReqTime = GetCurrentTimeStamp(),
                                                        Hashes = hashesId,
                                                        Input = inputMsg,
                                                        Body = jBody.ToString(),
                                                        Id = msgThreadId.ToString(),
                                                    };
                                                    if (!MyMsgContext.MsgThreadEntities.Any(x => x.Id == msgThreadId.ToString()))
                                                    {
                                                        Console.BackgroundColor = ConsoleColor.Blue;
                                                        Console.WriteLine("Add");
                                                        Console.ResetColor();
                                                        MyMsgContext.MsgThreadEntities.Add(msgThreadEntity);
                                                    }
                                                    else
                                                    {
                                                        Console.BackgroundColor = ConsoleColor.Blue;
                                                        Console.WriteLine("Update");
                                                        Console.ResetColor();

                                                        msgThreadEntity = MyMsgContext.MsgThreadEntities.FirstOrDefault(x =>
                                                            x.Id == msgThreadId.ToString());
                                                        msgThreadEntity.ReqTime = GetCurrentTimeStamp();
                                                        msgThreadEntity.Input = inputMsg;
                                                        msgThreadEntity.Output = string.Empty;
                                                        msgThreadEntity.Body = jBody.ToString();
                                                        msgThreadEntity.Hashes = hashesId;
                                                        MyMsgContext.MsgThreadEntities.Update(msgThreadEntity);
                                                    }

                                                    context.Items["MsgThreadEntity"] = msgThreadEntity;
                                                }

                                                if (UseSystemPromptTrim)
                                                {
                                                    msgs.RemoveAll(x => string.Equals(x.Role,
                                                        ChatRole.System.ToString(),
                                                        StringComparison.CurrentCultureIgnoreCase));
                                                    jBody["messages"] = JArray.FromObject(msgs);
                                                }

                                                if (UseSystemPromptInject && !string.IsNullOrWhiteSpace(SystemPrompt))
                                                {
                                                    msgs.Insert(0,
                                                        new Message
                                                        {
                                                            Role = ChatRole.System.ToString(),
                                                            Content = SystemPrompt
                                                        });
                                                    jBody["messages"] = JArray.FromObject(msgs);
                                                    Console.WriteLine(jBody.ToString());
                                                }
                                            }
                                        }

                                        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jBody.ToString()));
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }
                                else
                                {
                                    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(strBody));
                                }

                                var requestObj = new MsgRequestIdObj()
                                {
                                    Id = Ulid.NewUlid().ToGuid().ToString(),
                                    RoundId = roundId,
                                    Body = strBody,
                                    Path = context.Request.PathBase.ToString() + context.Request.Path.ToString(),
                                    Method = context.Request.Method,
                                    Header = JsonConvert.SerializeObject(context.Request.Headers),
                                    UserAgent = context.Request.Headers["User-Agent"].ToString(),
                                    IP = context.Connection.RemoteIpAddress?.ToString(),
                                    Hashes = hashesId,
                                };
                                MyMsgContext.MsgRequestIdObjs.Add(requestObj);

                                context.Items["RequestObj"] = requestObj;
                                context.Items["JBody"] = jBody;
                                await MyMsgContext.SaveChangesAsync();

                                context.Request.ContentLength = context.Request.Body.Length;
                                context.Request.Body.Position = 0;
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                            }

                            await next(context);
                        });

                        app.Use(async (context, next) =>
                        {
                            if (UseRiskModel && context.Request.Method == "POST" &&
                                context.Items.TryGetValue("JBody", out var jBodyObj) &&
                                jBodyObj is JObject jBody)
                            {
                                var msgs = jBody["messages"]?.ToObject<List<Message>>();

                                await foreach (var res in OllamaApi.ChatAsync(new ChatRequest()
                                               {
                                                   Model = RiskModel,
                                                   Messages = msgs.Select(x =>
                                                       new OllamaSharp.Models.Chat.Message(x.Role, x.Content)),
                                                   Stream = false
                                               }))
                                {
                                    var risks = res?.Message.Content;
                                    Console.WriteLine(risks);
                                    if (risks != null &&
                                        RiskKeywordsList.Any(x =>
                                            risks.ToUpper().Contains(x.ToUpper())))
                                    {
                                        context.Response.WriteAsync(new JObject
                                        {
                                            {
                                                "error",
                                                new JObject
                                                {
                                                    {
                                                        "message",
                                                        "Messages with content security risks. Unable to continue."
                                                    },
                                                    {"type", "content_risks"},
                                                    {"risk_model", res?.Model},
                                                    {"risk_raw_msg", res?.Message.Content}
                                                }
                                            }
                                        }.ToString());
                                    }
                                }
                            }

                            await next(context);
                        });

                        app.Use(async (context, next) =>
                        {
                            if (UseTokenReplace)
                            {
                                context.Request.Headers.Remove("Authorization");
                                if (ReplaceTokenMode is "first" or "failback")
                                {
                                    context.Request.Headers.Authorization =
                                        "Bearer " + ReplaceTokensList.FirstOrDefault();
                                }
                                else if (ReplaceTokenMode == "random")
                                {
                                    context.Request.Headers.Authorization =
                                        "Bearer " + ReplaceTokensList.ToArray()[
                                            new Random().Next(ReplaceTokensList.Count - 1)];
                                }
                            }

                            await next.Invoke();
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

                        app.UseRouting().UseEndpoints(endpoint =>
                        {
                            endpoint.Map("/v1/chat/completions", async context =>
                            {
                                var jBody = (JObject) context.Items["JBody"]!;
                                var isStream = jBody.ContainsKey("stream") && jBody["stream"]!.ToObject<bool>();

                                Console.WriteLine(TargetApiUrl + context.Request.PathBase + context.Request.Path);
                                Console.WriteLine("CHAT:" + jBody);

                                //try
                                //{
                                //    if (context.Items.TryGetValue("RequestObj", out var rObj) &&
                                //        rObj is MsgRequestIdObj requestObj)
                                //    {
                                //        requestObj.Hashes = hashesId;
                                //        MyMsgContext.MsgRequestIdObjs.Update(requestObj);
                                //        await MyMsgContext.SaveChangesAsync();
                                //    }
                                //}
                                //catch (Exception e)
                                //{
                                //    Console.WriteLine(e);
                                //}

                                var msgThreadEntity = (MsgThreadEntity)context.Items["MsgThreadEntity"];

                                if (isStream)
                                {
                                    try
                                    {
                                        // 配置响应头
                                        context.Response.ContentType = "text/plain; charset=utf-8";
                                        context.Response.Headers.CacheControl = "no-cache";
                                        context.Response.Headers.Connection = "keep-alive";

                                        using var httpClient = new HttpClient();
                                        var apiUrl = TargetApiUrl + context.Request.PathBase + context.Request.Path;


                                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);

                                        request.Headers.Clear();
                                        foreach (var header in context.Request.Headers)
                                            if (!header.Key.Equals("Content-Length",
                                                    StringComparison.OrdinalIgnoreCase))
                                                request.Headers.TryAddWithoutValidation(header.Key,
                                                    header.Value.ToArray());

                                        request.Content =
                                            new StringContent(jBody.ToString(),
                                                Encoding.UTF8, "application/json");

                                        // 关键：使用 ResponseHeadersRead 模式立即获取流
                                        var response = await httpClient.SendAsync(request,
                                            HttpCompletionOption.ResponseHeadersRead);

                                        if (!response.IsSuccessStatusCode)
                                        {
                                            context.Response.StatusCode = (int) response.StatusCode;
                                            var msg = await response.Content.ReadAsStringAsync();
                                            await context.Response.WriteAsync(msg);
                                            msgThreadEntity.Output = msg;
                                            msgThreadEntity.FinishReason = "error:" + response.StatusCode;

                                            MyMsgContext.MsgThreadEntities.Update(msgThreadEntity);
                                            await MyMsgContext.SaveChangesAsync();

                                            Console.WriteLine(msg);
                                            return;
                                        }

                                        // 获取响应流
                                        using var stream = await response.Content.ReadAsStreamAsync();
                                        using var reader = new StreamReader(stream);

                                        // 流式读取
                                        var buffer = new char[1024];
                                        var sb = new StringBuilder();
                                        var lines = new List<string>();

                                        var deltas = "";
                                        var deltaRole = "";

                                        while (true)
                                        {
                                            var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                                            if (bytesRead == 0) break;

                                            sb.Append(buffer, 0, bytesRead);

                                            // 处理完整行
                                            while (sb.Length > 0)
                                            {
                                                var newLineIndex = sb.ToString().IndexOf('\n');
                                                if (newLineIndex < 0) break;

                                                var line = sb.ToString(0, newLineIndex).Trim();
                                                sb.Remove(0, newLineIndex + 1);

                                                lines.Add(line);
                                                Console.WriteLine(line);

                                                if (!string.IsNullOrEmpty(line))
                                                {
                                                    if (line.StartsWith("data: "))
                                                    {
                                                        var jsonData = line.Substring(6);
                                                        if (jsonData == "[DONE]")
                                                        {
                                                            try
                                                            {
                                                                Console.WriteLine("___________");
                                                                msgThreadEntity.Output = deltaRole + ":" + deltas;
                                                                msgThreadEntity.PromptDuration = 0;
                                                                msgThreadEntity.EvalDuration = msgThreadEntity.EndTime - msgThreadEntity.StartTime;
                                                                msgThreadEntity.LoadDuration = msgThreadEntity.StartTime - msgThreadEntity.ReqTime;
                                                                msgThreadEntity.Time = DateTime.Now;
                                                                Console.WriteLine(JsonConvert.SerializeObject(msgThreadEntity));
                                                            }
                                                            catch (Exception e)
                                                            {
                                                                Console.WriteLine(e);
                                                            }
                                                        }
                                                        else
                                                        {
                                                            try
                                                            {
                                                                var json = JObject.Parse(jsonData);
                                                                if (string.IsNullOrEmpty(deltas)) msgThreadEntity.StartTime = GetCurrentTimeStamp();

                                                                if (json.ContainsKey("tool_calls") && json["tool_calls"]!.Any())
                                                                {
                                                                    deltas = json["tool_calls"]?.ToString();
                                                                    deltaRole = "tool";
                                                                }
                                                                if (json.ContainsKey("function_call") && json["function_call"]!.Any())
                                                                {
                                                                    deltas = json["function_call"]?.ToString();
                                                                    deltaRole = "tool";
                                                                }

                                                                if (json.ContainsKey("choices") && json["choices"]!.Any())
                                                                {
                                                                    foreach (var choice in json["choices"]!)
                                                                    {
                                                                        var delta = choice?["delta"];
                                                                        if (delta?["role"] != null)
                                                                            deltaRole = delta["role"]?.ToString();
                                                                        if (delta?["content"] != null)
                                                                            deltas += delta["content"]?.ToString();

                                                                        if (choice?["finish_reason"] != null)
                                                                        {
                                                                            msgThreadEntity.FinishReason = choice["finish_reason"]?.ToString();
                                                                            msgThreadEntity.EndTime = GetCurrentTimeStamp();
                                                                        }
                                                                    }
                                                                }

                                                                if (json.TryGetValue("usage", out var usage))
                                                                {
                                                                    msgThreadEntity.InputTokens = usage["prompt_tokens"]
                                                                        ?.ToObject<int>();
                                                                    msgThreadEntity.OutputTokens = usage["completion_tokens"]
                                                                        ?.ToObject<int>();
                                                                    msgThreadEntity.TotalTokens = usage["total_tokens"]
                                                                        ?.ToObject<int>();
                                                                }
                                                            }
                                                            catch (Exception e)
                                                            {
                                                                Console.WriteLine(e);
                                                            }
                                                        }
                                                    }
                                                }

                                                await context.Response.WriteAsync(line + Environment.NewLine);
                                                await context.Response.Body.FlushAsync();
                                            }

                                            var statusCode = Convert.ToInt32(response.StatusCode);
                                            if (UseTokenReplace && ReplaceTokenMode == "failback" &&
                                                ReplaceTokensList.Any() &&
                                                statusCode is >= 400 and < 500)
                                            {
                                                ReplaceTokensList.Add(ReplaceTokensList.First());
                                                ReplaceTokensList.RemoveAt(0);
                                            }
                                        }

                                        MyMsgContext.MsgThreadEntities.Update(msgThreadEntity);
                                        await MyMsgContext.SaveChangesAsync();
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }

                                    await context.Response.CompleteAsync();
                                }
                                else
                                {
                                    var response = await context.ForwardTo(new Uri(TargetApiUrl)).Send();
                                    response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");
                                    context.Response.StatusCode = (int)response.StatusCode;

                                    var resBody = await response.Content.ReadAsStringAsync();
                                    await context.Response.WriteAsync(resBody);

                                    try
                                    {
                                        var json = JObject.Parse(resBody);
                                        if (json.TryGetValue("usage", out var usage))
                                        {
                                            msgThreadEntity.InputTokens = usage["prompt_tokens"]
                                                ?.ToObject<int>();
                                            msgThreadEntity.OutputTokens = usage["completion_tokens"]
                                                ?.ToObject<int>();
                                            msgThreadEntity.TotalTokens = usage["total_tokens"]
                                                ?.ToObject<int>();
                                        }

                                        if (json.TryGetValue("choices", out var choices))
                                        {
                                            var choice = choices.FirstOrDefault();
                                            msgThreadEntity.FinishReason = choice["finish_reason"]?.ToString();
                                            msgThreadEntity.Output = choice["message"]?["role"] + ":" +
                                                                     choice["message"]?["content"];
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }

                                    msgThreadEntity.StartTime = msgThreadEntity.ReqTime;
                                    msgThreadEntity.EndTime = GetCurrentTimeStamp();
                                    msgThreadEntity.LoadDuration = 0;
                                    msgThreadEntity.PromptDuration = 0;
                                    msgThreadEntity.EvalDuration = msgThreadEntity.EndTime - msgThreadEntity.ReqTime;

                                    MyMsgContext.MsgThreadEntities.Update(msgThreadEntity);
                                    await MyMsgContext.SaveChangesAsync();

                                    if (UseTokenReplace && ReplaceTokenMode == "failback" && ReplaceTokensList.Any() &&
                                        context.Response.StatusCode is >= 400 and < 500)
                                    {
                                        ReplaceTokensList.Add(ReplaceTokensList.First());
                                        ReplaceTokensList.RemoveAt(0);
                                    }
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
                    response = await context.ForwardTo(new Uri(TargetApiUrl + "/v1")).Send();
                    response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");

                    Console.WriteLine(await response.Content.ReadAsStringAsync());

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

        public static long GetCurrentTimeStamp(bool isMinseconds = false)
        {
            var ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(isMinseconds ? ts.TotalMilliseconds : ts.TotalSeconds);
        }
    }

    public class Message
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }

        [JsonPropertyName("images")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Images { get; set; }

        [JsonPropertyName("image_url")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ImageUrl { get; set; }

        [JsonPropertyName("tool_calls")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ToolCalls { get; set; }
    }
}
