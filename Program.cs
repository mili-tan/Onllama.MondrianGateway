using System.ComponentModel;
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
using NRedisStack.RedisStackCommands;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using ProxyKit;
using StackExchange.Redis;

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

        public static string RedisDBStr = File.ReadAllText("redis.text").Trim();
        public static ConnectionMultiplexer RedisConnection;
        public static IDatabase RedisDatabase;

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

        public static Dictionary<HashSet<string>, List<Message>> HashsDictionary = new();

        public static FastCache<Guid, string> MsgSets = new FastCache<Guid, string>();

        static void Main(string[] args)
        {
            try
            {
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

                RedisConnection = ConnectionMultiplexer.Connect(RedisDBStr);
                RedisDatabase = RedisConnection.GetDatabase();

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

                            if (UseToken && !TokensList.Contains(reqToken))
                            {
                                context.Response.Headers.ContentType = "application/json";
                                context.Response.StatusCode = (int) HttpStatusCode.Forbidden;
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
                                var msgSetId = Ulid.NewUlid().ToGuid();
                                var hashesId = string.Empty;
                                var inputMsg = string.Empty;

                                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                                var jBody = JObject.Parse(body);
                                var isStream = jBody.ContainsKey("stream") && jBody["stream"]!.ToObject<bool>();
                                var sessionId = jBody.TryGetValue("session_id", out var sessionFromBody)
                                    ?
                                    sessionFromBody.ToString()
                                    :
                                    context.Request.Headers.TryGetValue("session_id", out var sessionFromHeader)
                                        ? sessionFromHeader.ToString()
                                        : null;

                                jBody["stream_options"] = new JObject() {["include_usage"] = true};

                                Console.WriteLine(jBody);

                                if (sessionId != null)
                                {
                                    RedisDatabase.JSON().Set("Session:" + sessionId + ":" + Ulid.NewUlid().ToGuid(),
                                        "$", body);
                                    try
                                    {
                                        jBody.Remove("session_id");
                                        context.Request.Headers.Remove("session_id");
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }
                                }

                                if (jBody.ContainsKey("messages"))
                                {
                                    var msgs = jBody["messages"]?.ToObject<List<Message>>();
                                    if (msgs != null && msgs.Any())
                                    {
                                        var lastMsg = msgs.Last();
                                        inputMsg = lastMsg.Role + ":" + lastMsg.Content;
                                        if (true)
                                        {
                                            var fnv = FNV1a.Create();
                                            var hashes = new HashSet<string>();
                                            foreach (var item in msgs)
                                            {
                                                hashes.Add(Convert
                                                    .ToBase64String(
                                                        fnv.ComputeHash(Encoding.UTF8.GetBytes(item.Content)))
                                                    .TrimEnd('='));
                                            }

                                            hashesId = string.Join(',', hashes.ToList());
                                            RedisDatabase.JSON().Set("MSG-HASH:" + hashesId, "$", body);

                                            if (MsgSets.Any(x => hashesId.StartsWith(x.Value)))
                                                msgSetId = MsgSets.FirstOrDefault(x => hashesId.StartsWith(x.Value)).Key;

                                            var setBody = jBody.DeepClone();
                                            setBody["Hash"] = hashesId;
                                            RedisDatabase.JSON().Set("MSG-SET:" + msgSetId, "$", setBody);
                                            MsgSets.AddOrUpdate(msgSetId, hashesId, TimeSpan.FromMinutes(15));

                                            //Console.WriteLine(string.Join(',',
                                            //    HashsDictionary.Keys.LastOrDefault(x =>
                                            //        x.Count <= hashs.Count && x.IsSubsetOf(hashs)) ?? ["NF"]));

                                            HashsDictionary.Add(hashes, msgs);
                                        }

                                        if (UseSystemPromptTrim)
                                        {
                                            msgs.RemoveAll(x => string.Equals(x.Role, ChatRole.System.ToString(),
                                                StringComparison.CurrentCultureIgnoreCase));
                                            jBody["messages"] = JArray.FromObject(msgs);
                                        }

                                        if (UseSystemPromptInject && !string.IsNullOrWhiteSpace(SystemPrompt))
                                        {
                                            msgs.Insert(0,
                                                new Message
                                                {
                                                    Role = ChatRole.System.ToString(), Content = SystemPrompt
                                                });
                                            jBody["messages"] = JArray.FromObject(msgs);
                                            Console.WriteLine(jBody.ToString());
                                        }

                                        if (UseRiskModel)
                                        {
                                            await foreach (var res in OllamaApi.ChatAsync(new ChatRequest()
                                                           {
                                                               Model = RiskModel,
                                                               Messages = msgs.Select(x =>
                                                                   new OllamaSharp.Models.Chat.Message(x.Role,
                                                                       x.Content)),
                                                               Stream = false
                                                           }))
                                            {
                                                var risks = res?.Message.Content;
                                                Console.WriteLine(risks);
                                                if (risks != null &&
                                                    RiskKeywordsList.Any(x => risks.ToUpper().Contains(x.ToUpper())))
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
                                                                {"type", "content_risks"}, {"risk_model", res?.Model},
                                                                {"risk_raw_msg", res?.Message.Content}
                                                            }
                                                        }
                                                    }.ToString());
                                                }
                                            }

                                        }
                                    }
                                    else
                                    {
                                        RedisDatabase.JSON().Set("MSG:" + Ulid.NewUlid().ToGuid(), "$", body);
                                    }
                                }

                                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jBody.ToString()));
                                context.Request.ContentLength = context.Request.Body.Length;

                                if (isStream)
                                {

                                    try
                                    {
                                        // 配置响应头
                                        context.Response.ContentType = "text/plain; charset=utf-8";
                                        context.Response.Headers.CacheControl = "no-cache";
                                        context.Response.Headers.Connection = "keep-alive";

                                        using var httpClient = new HttpClient();
                                        var apiUrl = TargetApiUrl + "/v1/chat/completions";
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

                                        var msgSet = new MsgSetEntity()
                                        {
                                            ReqTime = GetCurrentTimeStamp(), 
                                            Time = DateTime.Now,
                                            Hashes = hashesId,
                                            Input = inputMsg,
                                            Body = jBody.ToString(),
                                            Id = msgSetId.ToString(),
                                        };
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
                                                                msgSet.Output = deltaRole + ":" + deltas;
                                                                msgSet.PromptDuration = 0;
                                                                msgSet.EvalDuration = msgSet.EndTime - msgSet.StartTime;
                                                                msgSet.LoadDuration = msgSet.StartTime - msgSet.ReqTime;
                                                                msgSet.Time = DateTime.Now;
                                                                Console.WriteLine(JsonConvert.SerializeObject(msgSet));
                                                                return;
                                                            }
                                                            catch (Exception e)
                                                            {
                                                                Console.WriteLine(e);
                                                            }
                                                        }

                                                        try
                                                        {
                                                            var json = JObject.Parse(jsonData);
                                                            if (string.IsNullOrEmpty(deltas))
                                                                msgSet.StartTime = json["created"]!.ToObject<long>();

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
                                                                        msgSet.FinishReason =
                                                                            choice["finish_reason"]?.ToString();
                                                                        msgSet.EndTime =
                                                                            json["created"]!.ToObject<long>();
                                                                    }
                                                                }
                                                            }

                                                            if (json.TryGetValue("usage", out var usage))
                                                            {
                                                                msgSet.InputTokens = usage["prompt_tokens"]
                                                                    ?.ToObject<int>();
                                                                msgSet.OutputTokens = usage["completion_tokens"]
                                                                    ?.ToObject<int>();
                                                                msgSet.TotalTokens = usage["total_tokens"]
                                                                    ?.ToObject<int>();
                                                            }
                                                        }
                                                        catch (Exception e)
                                                        {
                                                            Console.WriteLine(e);
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

                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }

                                    await context.Response.CompleteAsync();
                                }
                                else
                                {
                                    var response = await context
                                        .ForwardTo(new Uri(TargetApiUrl + "/v1/chat/completions")).Send();
                                    response.Headers.Add("X-Forwarder-By", "MondrianGateway/0.1");
                                    await context.Response.WriteAsync(await response.Content.ReadAsStringAsync());

                                    var statusCode = Convert.ToInt32(response.StatusCode);
                                    if (UseTokenReplace && ReplaceTokenMode == "failback" && ReplaceTokensList.Any() &&
                                        statusCode is >= 400 and < 500)
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
                    if (context.Request.Method.ToUpper() == "POST")
                    {
                        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                        var jBody = JObject.Parse(body);
                        //RedisDatabase.JSON().Set(Ulid.NewUlid().ToGuid().ToString(), "$", body);

                        if (jBody.ContainsKey("model") && UseModelReplace &&
                            ModelReplaceDictionary.TryGetValue(jBody["model"]?.ToString()!, out var newModel))
                            jBody["model"] = newModel;

                        Console.WriteLine(jBody.ToString());

                    }

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

    public class MsgSetEntity
    {
        [DisplayName("对话 ID")] public string Id { get; set; }
        [DisplayName("对话 Hashes")] public string Hashes { get; set; }
        [DisplayName("输入内容")] public string Input { get; set; }
        [DisplayName("输出内容")] public string Output { get; set; }
        [DisplayName("请求内容")] public string Body { get; set; }
        [DisplayName("加载")] public long LoadDuration { get; set; }
        [DisplayName("输入")] public long PromptDuration { get; set; }
        [DisplayName("输出")] public long EvalDuration { get; set; }
        [DisplayName("输入 Token")] public int? InputTokens { get; set; }
        [DisplayName("输出 Token")] public int? OutputTokens { get; set; }
        [DisplayName("输出 Token")] public int? TotalTokens { get; set; }

        [DisplayName("创建时间")] public DateTime Time { get; set; }
        [DisplayName("请求时间")] public long ReqTime { get; set; }
        [DisplayName("开始时间")] public long StartTime { get; set; }
        [DisplayName("结束时间")] public long EndTime { get; set; }
        [DisplayName("结束")] public string? FinishReason { get; set; }
    }

}
