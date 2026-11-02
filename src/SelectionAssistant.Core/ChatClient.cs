using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SelectionAssistant.Core;

public sealed class ChatClient(HttpClient client)
{
    public async Task<string> CompleteAsync(ApiSettings settings, IReadOnlyList<ChatMessage> messages, Action<string>? onDelta = null, CancellationToken cancellationToken = default)
    {
        var uri = settings.GetUri();
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new ArgumentException("请先在设置中填写 API Key。");
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model.Trim(),
            ["stream"] = true,
            ["max_tokens"] = 4096,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
        if (uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var detail = code switch { 401 or 403 => "密钥无效或没有权限，请检查设置", 402 => "服务余额不足", 429 => "请求过于频繁，请稍后重试", _ => "服务暂时不可用，请检查接口地址或稍后重试" };
            throw new HttpRequestException($"{detail}（HTTP {code}）。", null, response.StatusCode);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choices")[0];
            ValidateFinish(choice);
            var result = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("服务返回了空内容，请重试。");
            onDelta?.Invoke(result);
            return result.Trim();
        }
        var text = new StringBuilder();
        var eventData = new StringBuilder();
        bool complete = false;
        void ReadEvent()
        {
            if (eventData.Length == 0) return;
            var data = eventData.ToString().Trim();
            eventData.Clear();
            if (data == "[DONE]") { complete = true; return; }
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException("服务中断了回答，请重试。");
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            ValidateFinish(choice);
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop") complete = true;
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var value = content.GetString()!;
                text.Append(value);
                if (text.Length > 1_000_000) throw new InvalidDataException("回答过长，已停止接收。");
                if (value.Length != 0) onDelta?.Invoke(value);
            }
        }
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) { ReadEvent(); if (complete) break; }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                eventData.AppendLine(line[5..].TrimStart(' '));
                if (eventData.Length > 1_000_000) throw new InvalidDataException("服务响应格式异常。");
            }
        }
        ReadEvent();
        cancellationToken.ThrowIfCancellationRequested();
        if (!complete) throw new InvalidDataException("连接在回答完成前中断，请重试。");
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidDataException("服务返回了空内容，请重试。");
        return text.ToString().Trim();
    }

    private static void ValidateFinish(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind != JsonValueKind.String) return;
        if (reason.GetString() is { } value && value != "stop")
            throw new InvalidDataException(value == "length" ? "回答被长度限制截断，请缩短内容后重试。" : "服务没有完整生成回答，请重试。");
    }
}
