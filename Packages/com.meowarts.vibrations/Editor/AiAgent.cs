using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Vibrations
{
    public enum AiProvider { Anthropic, OpenAI }

    // Runs a prompt as a tool-use loop against Anthropic (Messages API) or OpenAI (Chat Completions).
    // Continuations come back on Unity's main thread, so tools can touch the scene directly.
    public static class AiAgent
    {
        public const int MaxSteps = 16;
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

        public readonly struct ToolCall
        {
            public readonly string id, name;
            public readonly JObject args;
            public ToolCall(string id, string name, JObject args) => (this.id, this.name, this.args) = (id, name, args);
        }

        // Per-machine settings: the key never goes into the project.
        public static AiProvider Provider
        {
            get => (AiProvider)EditorPrefs.GetInt("Vibrations.AI.Provider", 0);
            set => EditorPrefs.SetInt("Vibrations.AI.Provider", (int)value);
        }

        public static string GetKey(AiProvider p) => EditorPrefs.GetString($"Vibrations.AI.Key.{p}", "");
        public static void SetKey(AiProvider p, string key) => EditorPrefs.SetString($"Vibrations.AI.Key.{p}", key.Trim());
        public static string GetModel(AiProvider p) => EditorPrefs.GetString($"Vibrations.AI.Model.{p}", DefaultModel(p));
        public static void SetModel(AiProvider p, string model) => EditorPrefs.SetString($"Vibrations.AI.Model.{p}", model.Trim());
        public static string DefaultModel(AiProvider p) => p == AiProvider.Anthropic ? "claude-sonnet-5" : "gpt-5";

        public static async Task RunAsync(AiProvider provider, string key, string model, string prompt, AiTools tools,
            Action<string, string> log, Action<byte[]> showImage, CancellationToken cancel)
        {
            var messages = new JArray();
            if (provider == AiProvider.OpenAI) messages.Add(new JObject { ["role"] = "system", ["content"] = AiTools.SystemPrompt });
            messages.Add(new JObject { ["role"] = "user", ["content"] = prompt });

            for (int step = 0; step < MaxSteps; step++)
            {
                var response = await Post(provider, key, BuildRequest(provider, model, messages), cancel);
                var (text, calls, assistant) = ParseResponse(provider, response);
                messages.Add(assistant);
                if (!string.IsNullOrWhiteSpace(text)) log("assistant", text.Trim());
                if (calls.Count == 0) return;

                var results = new List<AiTools.ToolResult>();
                foreach (var call in calls)
                {
                    cancel.ThrowIfCancellationRequested();
                    log("tool", Describe(call));
                    var result = tools.Execute(call.name, call.args);
                    if (result.IsError) log("error", result.text);
                    if (result.image != null) showImage(result.image);
                    results.Add(result);
                }
                AppendToolResults(provider, messages, calls, results);
            }
            log("error", $"Stopped after {MaxSteps} steps.");
        }

        public static JObject BuildRequest(AiProvider provider, string model, JArray messages)
        {
            var tools = new JArray();
            foreach (var (name, description, schema) in AiTools.Definitions)
                tools.Add(provider == AiProvider.Anthropic
                    ? new JObject { ["name"] = name, ["description"] = description, ["input_schema"] = JObject.Parse(schema) }
                    : new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject { ["name"] = name, ["description"] = description, ["parameters"] = JObject.Parse(schema) },
                    });

            var body = new JObject { ["model"] = model, ["messages"] = messages, ["tools"] = tools };
            if (provider == AiProvider.Anthropic)
            {
                body["system"] = AiTools.SystemPrompt;
                body["max_tokens"] = 4096;
            }
            return body;
        }

        public static (string text, List<ToolCall> calls, JToken assistantMessage) ParseResponse(AiProvider provider, JObject response)
        {
            var calls = new List<ToolCall>();
            var text = new StringBuilder();
            if (provider == AiProvider.Anthropic)
            {
                var content = (JArray)response["content"];
                foreach (var block in content)
                {
                    var type = block.Value<string>("type");
                    if (type == "text") text.Append(block.Value<string>("text"));
                    else if (type == "tool_use") calls.Add(new ToolCall(block.Value<string>("id"), block.Value<string>("name"), block["input"] as JObject ?? new JObject()));
                }
                return (text.ToString(), calls, new JObject { ["role"] = "assistant", ["content"] = content });
            }

            var message = (JObject)response["choices"][0]["message"];
            text.Append(message.Value<string>("content"));
            if (message["tool_calls"] is JArray toolCalls)
                foreach (var call in toolCalls)
                {
                    var arguments = call["function"].Value<string>("arguments");
                    calls.Add(new ToolCall(call.Value<string>("id"), call["function"].Value<string>("name"),
                        string.IsNullOrEmpty(arguments) ? new JObject() : JObject.Parse(arguments)));
                }
            var clean = new JObject { ["role"] = "assistant", ["content"] = message["content"] };
            if (message["tool_calls"] != null) clean["tool_calls"] = message["tool_calls"];
            return (text.ToString(), calls, clean);
        }

        public static void AppendToolResults(AiProvider provider, JArray messages, List<ToolCall> calls, List<AiTools.ToolResult> results)
        {
            if (provider == AiProvider.Anthropic)
            {
                var content = new JArray();
                for (int i = 0; i < calls.Count; i++)
                {
                    var r = results[i];
                    JToken body = r.text;
                    if (r.image != null) // tool results can carry images directly
                        body = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = r.text },
                            new JObject
                            {
                                ["type"] = "image",
                                ["source"] = new JObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(r.image) },
                            },
                        };
                    content.Add(new JObject { ["type"] = "tool_result", ["tool_use_id"] = calls[i].id, ["content"] = body, ["is_error"] = r.IsError });
                }
                messages.Add(new JObject { ["role"] = "user", ["content"] = content });
                return;
            }

            // OpenAI tool messages are text only: images follow in a user message.
            var images = new JArray();
            for (int i = 0; i < calls.Count; i++)
            {
                messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = calls[i].id, ["content"] = results[i].text });
                if (results[i].image == null) continue;
                images.Add(new JObject { ["type"] = "text", ["text"] = $"Image from {calls[i].name} ({calls[i].id}):" });
                images.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(results[i].image) },
                });
            }
            if (images.Count > 0) messages.Add(new JObject { ["role"] = "user", ["content"] = images });
        }

        static async Task<JObject> Post(AiProvider provider, string key, JObject body, CancellationToken cancel)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, provider == AiProvider.Anthropic
                ? "https://api.anthropic.com/v1/messages"
                : "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
            };
            if (provider == AiProvider.Anthropic)
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else request.Headers.Add("Authorization", "Bearer " + key);

            using var response = await Http.SendAsync(request, cancel);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string message = json;
                try { message = JObject.Parse(json)["error"]?.Value<string>("message") ?? json; }
                catch (Newtonsoft.Json.JsonException) { }
                throw new Exception($"{provider} error {(int)response.StatusCode}: {message}");
            }
            return JObject.Parse(json);
        }

        static string Describe(ToolCall call)
        {
            var a = call.args;
            return call.name switch
            {
                "get_animation" => "Reading the animation",
                "add_pose" => $"Adding pose '{a.Value<string>("name") ?? "AI pose"}' after {a.Value<int?>("after_index")}",
                "update_pose" => $"Editing pose {a.Value<int?>("index")}",
                "delete_pose" => $"Deleting pose {a.Value<int?>("index")}",
                "mirror_pose" => $"Mirroring pose {a.Value<int?>("index")}",
                "set_settings" => "Tuning " + string.Join(", ", ((IDictionary<string, JToken>)a).Keys),
                "render_preview" => a.Value<string>("mode") == "motion" ? "Looking at the motion" : "Looking at the poses",
                _ => call.name,
            };
        }
    }
}
